using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CdxSwapper.Sessions;

sealed class SessionConverter(string codexHome, string claudeHome, string dataRoot, string? hermesHome = null)
{
    readonly object gate = new();
    public ConversionResult Convert(SessionItem item, string target)
    {
        if (target is not ("codex" or "claude" or "hermes" or "antigravity" or "antigravity-app") || target == item.Provider) throw new ArgumentException("Select the other client.");
        if (item.MetadataOnly) throw new InvalidOperationException(SessionText.Pick("Conversion requires the full local transcript.",
            "Для переноса нужна полная локальная переписка.", "La conversión requiere el historial local completo."));
        if (item.Provider is "hermes" or "antigravity" && item.Cwd.Length == 0) item = item with { Cwd = SessionActions.WorkingDirectory(item) };
        if (target is "claude" or "antigravity" or "antigravity-app") item = item with { Cwd = ClaudeWorkingDirectory(item.Cwd) };
        if (!Directory.Exists(item.Cwd)) throw new InvalidOperationException(SessionText.Pick("The project folder no longer exists.",
            "Папка проекта больше не существует.", "La carpeta del proyecto ya no existe."));
        lock (gate) return ConvertCore(item, target);
    }

    ConversionResult ConvertCore(SessionItem item, string target)
    {
        var stamp = SessionSource.Stamp(item);
        var targetHome = target switch { "codex" => codexHome, "claude" => claudeHome, "antigravity" => Path.Combine(AntigravityStore.DefaultRoot,"antigravity-ide"), "antigravity-app" => Path.Combine(AntigravityStore.DefaultRoot,"antigravity"), _ => hermesHome ?? HermesStore.DefaultHome };
        var version = target is "antigravity" or "antigravity-app" ? "v5-antigravity-completed-steps" : target == "hermes" ? "v5-hermes-tool-display" : target == "claude" ? "v6-claude-native-tools" : "v7-codex-visible-tools";
        var identity = $"{version}:{item.Path}:{item.Id}:{stamp}:{target}:{Path.GetFullPath(targetHome)}";
        var fingerprint = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var folder = Path.Combine(dataRoot, fingerprint); var manifest = Path.Combine(folder, "conversion.json");
        if (File.Exists(manifest))
        {
            try
            {
                var old = JsonSerializer.Deserialize<ConversionResult>(File.ReadAllText(manifest), SessionJson.Options);
                if (old != null && File.Exists(old.Path) && File.Exists(old.TranscriptPath) &&
                    (target != "hermes" || HermesStore.Exists(old.Path,old.Id))) return old with { Reused = true };
            }
            catch (JsonException) { }
        }
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid().ToString(); var now = DateTimeOffset.UtcNow; var timestamp = now.ToString("O");
        if(target is "antigravity" or "antigravity-app")
        {
            var path=Path.Combine(folder,id+".db");var history=Path.Combine(folder,"history.md");int messages=0;
            try
            {
                using(var writer=new AntigravityWriter(path,id,item))
                using(var export=new StreamWriter(history,false,new UTF8Encoding(false)))
                {
                    foreach(var (_,message) in SessionSource.Records(item))
                    {
                        if(message.Role=="metadata")continue;
                        writer.Write(message);export.WriteLine($"## {message.Role}\n\n{message.Text}\n");messages++;
                    }
                }
                if(messages==0 || SessionSource.Stamp(item)!=stamp)throw new IOException("Source history was empty or changed during conversion.");
                var result=new ConversionResult(target,id,item.Cwd,path,item.Id,item.Path,history,messages,timestamp,false);
                Store.AtomicWrite(manifest,Encoding.UTF8.GetBytes(SessionJson.Serialize(result)));return result;
            }
            catch { try { if(File.Exists(path))File.Delete(path); } catch(IOException) { } throw; }
        }
        if (target == "hermes") return ConvertToHermes(item,targetHome,id,now,folder,manifest,stamp);
        var destination = target == "codex" ? Path.Combine(codexHome, "sessions", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"), $"rollout-{now:yyyy-MM-ddTHH-mm-ss}-{id}.jsonl")
            : Path.Combine(claudeHome, "projects", Regex.Replace(item.Cwd, "[^a-zA-Z0-9]", "-"), id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var transcript = Path.Combine(folder, "history.md"); var temporary = destination + ".cdx_tmp";
        var exportTemp = transcript + ".cdx_tmp"; int count = 0;
        var note = WebAssets.ReadText("session_import.md");
        foreach (var (key, value) in new Dictionary<string, string> { ["title"] = item.Title.Length > 0 ? item.Title : item.Id,
            ["provider"] = item.Provider, ["target"] = target, ["source_id"] = item.Id, ["source_path"] = item.Path, ["transcript_path"] = transcript })
            note = note.Replace("{{" + key + "}}", value);
        bool published = false, complete = false;
        try
        {
            using (var stream = new StreamWriter(new FileStream(temporary, FileMode.CreateNew), new UTF8Encoding(false)))
            using (var export = new StreamWriter(exportTemp, false, new UTF8Encoding(false)))
            {
                var writer = new NativeSessionWriter(stream, target, id, item, timestamp);
                writer.Write(new("user", note, timestamp));
                foreach (var (_, message) in SessionSource.Records(item))
                {
                    if (message == null || message.Role == "metadata" || (message.Text.Length == 0 && message.Role != "tool")) continue;
                    count++;
                    export.WriteLine($"## {message.Role} · {message.Timestamp}\n\n{message.Text}\n");
                    writer.Write(message);
                }
                if (count == 0) throw new InvalidOperationException("No transferable messages in the source journal.");
                writer.Finish();
            }
            if (SessionSource.Stamp(item) != stamp) throw new IOException(SessionText.Pick("The session changed during conversion. Try again.",
                "Сессия изменилась во время переноса. Повторите попытку.", "La sesión cambió durante la conversión. Inténtalo de nuevo."));
            File.Move(exportTemp, transcript, true);
            File.Move(temporary, destination); published = true;
            var result = new ConversionResult(target, id, item.Cwd, destination, item.Id, item.Path, transcript, count, timestamp, false);
            Store.AtomicWrite(manifest, Encoding.UTF8.GetBytes(SessionJson.Serialize(result))); complete = true;
            return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(exportTemp)) File.Delete(exportTemp);
            if (published && !complete) File.Delete(destination);
        }
    }

    internal static string ClaudeWorkingDirectory(string path)
    {
        // Claude's adoption guard rejects Windows device paths, even for local drives.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 &&
            char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') return path[4..];
        return path;
    }

    ConversionResult ConvertToHermes(SessionItem item, string home, string id, DateTimeOffset now, string folder, string manifest, string stamp)
    {
        var timestamp = now.ToString("O"); var transcript = Path.Combine(folder,"history.md"); var temporary = transcript + ".cdx_tmp";
        var note = WebAssets.ReadText("session_import.md");
        foreach (var (key,value) in new Dictionary<string,string> { ["title"] = item.Title.Length > 0 ? item.Title : item.Id,
            ["provider"] = item.Provider, ["target"] = "hermes", ["source_id"] = item.Id, ["source_path"] = item.Path, ["transcript_path"] = transcript })
            note = note.Replace("{{"+key+"}}",value);
        int count = 0; bool complete = false;
        try
        {
            using var writer = new HermesWriter(home,id,item,now);
            writer.Write(new("user",note,timestamp));
            using (var export = new StreamWriter(temporary,false,new UTF8Encoding(false)))
                foreach (var (_,message) in SessionSource.Records(item))
                {
                    if (message.Role == "metadata" || (message.Text.Length == 0 && message.Role != "tool")) continue;
                    count++; export.WriteLine($"## {message.Role} · {message.Timestamp}\n\n{message.Text}\n"); writer.Write(message);
                }
            if (count == 0) throw new InvalidOperationException("No transferable messages in the source journal.");
            if (SessionSource.Stamp(item) != stamp) throw new IOException("The session changed during conversion. Try again.");
            File.Move(temporary,transcript,true);
            var result = new ConversionResult("hermes",id,item.Cwd,Path.Combine(home,"state.db"),item.Id,item.Path,transcript,count,timestamp,false);
            Store.AtomicWrite(manifest,Encoding.UTF8.GetBytes(SessionJson.Serialize(result)));
            writer.Commit(); complete = true; return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (!complete && File.Exists(manifest)) File.Delete(manifest);
        }
    }
}
