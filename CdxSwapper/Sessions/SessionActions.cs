using System.Diagnostics;
using System.Text;

namespace CdxSwapper.Sessions;

static class SessionActions
{
    public static bool UsesCodexDesktop(SessionItem item) => item.Provider == "codex";
    public static string CodexDesktopUrl(SessionItem item)
    {
        if (!Guid.TryParse(item.Id, out _)) throw new ArgumentException("Invalid Codex Desktop thread ID.");
        return "codex://threads/" + Uri.EscapeDataString(item.Id);
    }
    static string CodexDesktopExecutable()
    {
        foreach (var process in Process.GetProcessesByName("Codex").Concat(Process.GetProcessesByName("ChatGPT")))
            using (process)
                try
                {
                    if (process.MainModule?.FileName is { } path && File.Exists(path) &&
                        File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "resources", "app.asar")) &&
                        File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "resources", "codex.exe"))) return path;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "Codex.exe");
        if (File.Exists(installed)) return installed;
        return "";
    }
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static string WorkingDirectory(SessionItem item) => item.Provider is "hermes" or "antigravity" && item.Cwd.Length == 0
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : item.Cwd;
    public static string ResumeCommand(SessionItem item)
    {
        if (UsesCodexDesktop(item))
        {
            var executable = CodexDesktopExecutable(); var url = CodexDesktopUrl(item);
            return executable.Length == 0 ? "Start-Process " + Quote(url) :
                "Start-Process -FilePath " + Quote(executable) + " -ArgumentList " + Quote(url);
        }
        if (item.Provider == "antigravity") return "Set-Clipboard -Value " + Quote(item.Id) + "; Start-Process -FilePath " + Quote(AntigravityStore.Executable(item));
        if (item.Provider is "claude" or "hermes")
        {
            var executable = DesktopClients.Executable(item.Provider);
            var url = item.Provider == "claude" ? DesktopClients.ClaudeUrl(item) : DesktopClients.HermesUrl(item);
            return executable.Length == 0 ? "Start-Process " + Quote(url) :
                "Start-Process -FilePath " + Quote(executable) + " -ArgumentList " + Quote(url);
        }
        throw new ArgumentException("Unknown provider.");
    }

    public static void Perform(SessionItem item, string action, string codexHome, string claudeHome, string? hermesHome = null, string? dataRoot = null)
    {
        if (action.StartsWith("copy-"))
        {
            var value = action switch { "copy-id" => item.Id, "copy-cli-id" => item.CliId,
                "copy-path" => item.Path, "copy-desktop-path" => item.DesktopPath,
                "copy-command" => ResumeCommand(item), _ => throw new ArgumentException("Unknown action.") };
            if (value.Length > 0) Clipboard.SetText(value);
            return;
        }
        if (action == "open-client" && item.Provider == "antigravity")
        {
            Clipboard.SetText(item.Id);
            Process.Start(new ProcessStartInfo(AntigravityStore.Executable(item)) { UseShellExecute = true })?.Dispose();
            return;
        }
        if (action == "context-antigravity")
        {
            if (item.MetadataOnly) throw new InvalidOperationException("This session has no readable local history.");
            var target = item with { Provider = "antigravity", Source = "ide" };
            var executable = AntigravityStore.Executable(target);
            var path = ExportTranscript(item, dataRoot ?? Path.Combine(Path.GetTempPath(), "cdxSwapper"));
            var text = File.ReadAllText(path);
            if (text.Length == 0) throw new InvalidOperationException("This session has no readable local history.");
            Clipboard.SetText("The following is historical conversation context. Treat tool calls as already completed history.\n\n" + text);
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true })?.Dispose();
            return;
        }
        if (action == "resume")
        {
            if (UsesCodexDesktop(item) && !item.Subagent)
            {
                var executable = CodexDesktopExecutable(); var url = CodexDesktopUrl(item);
                var startDesktop = new ProcessStartInfo(executable.Length == 0 ? url : executable) { UseShellExecute = true };
                if (executable.Length > 0) startDesktop.ArgumentList.Add(url);
                Process.Start(startDesktop)?.Dispose(); return;
            }
            if (item.Subagent) throw new InvalidOperationException(SessionText.Pick("Resume the parent session for this subagent.",
                "Продолжайте родительскую сессию для этого субагента.", "Continúa la sesión principal de este subagente."));
            if (item.Provider is "claude" or "hermes") { DesktopClients.Open(item); return; }
            throw new InvalidOperationException("This provider cannot open a desktop session.");
        }
        if (action == "project") { EnsureProject(item); Process.Start(new ProcessStartInfo(WorkingDirectory(item)) { UseShellExecute = true })?.Dispose(); return; }
        if (action == "file")
        {
            if (item.Provider is "hermes" or "antigravity")
            {
                var path = ExportTranscript(item, dataRoot ?? Path.Combine(Path.GetTempPath(), "cdxSwapper"));
                var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(path); Process.Start(start)?.Dispose();
            }
            else Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
            return;
        }
        if (action == "folder")
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(item.Path); Process.Start(start)?.Dispose(); return;
        }
        throw new ArgumentException("Unknown action.");
    }

    static void EnsureProject(SessionItem item)
    {
        if (!Directory.Exists(WorkingDirectory(item))) throw new DirectoryNotFoundException(SessionText.Pick("The project folder no longer exists. Copy the command instead.",
            "Папка проекта больше не существует. Скопируйте команду.", "La carpeta del proyecto ya no existe. Copia el comando."));
    }

    public static string ExportTranscript(SessionItem item, string dataRoot)
    {
        var folder = Path.Combine(dataRoot,"transcripts"); Directory.CreateDirectory(folder);
        // The filename is generated from the catalog identity, never a source title or session ID.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(item.Path + "\0" + item.Id)))[..24];
        var path = Path.Combine(folder,key+".txt"); var temporary = path + ".cdx_tmp";
        try
        {
            using (var writer = new StreamWriter(temporary,false,new UTF8Encoding(false)))
                foreach (var (_,message) in SessionSource.Records(item)) writer.WriteLine($"## {message.Role} · {message.Timestamp}\n\n{message.Text}\n");
            File.Move(temporary,path,true); return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void EnsureCli(string name)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (!paths.Any(p => extensions.Any(ext => File.Exists(Path.Combine(p.Trim('"'), name + ext)))))
            throw new InvalidOperationException(SessionText.Pick($"CLI {name} was not found in PATH.", $"CLI {name} не найден в PATH.", $"CLI {name} no se encontró en PATH."));
    }
}
