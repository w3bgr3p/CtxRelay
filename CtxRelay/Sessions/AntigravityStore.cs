using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CtxRelay.Sessions;

static class AntigravityStore
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
    public static IEnumerable<string> Homes(string root) => new[] { Path.Combine(root, "antigravity-ide"), Path.Combine(root, "antigravity") };
    public static string Executable(SessionItem item)
    {
        var name = item.Source == "ide" ? "Antigravity IDE" : "Antigravity";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name, name + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException($"{name} executable not found.", path);
        return path;
    }
    public static IEnumerable<SessionItem> Scan(string home, List<string>? errors = null)
    {
        if (!Directory.Exists(home)) yield break;
        var transcripts = SessionCatalog.Files(Path.Combine(home, "brain"), "transcript.jsonl")
            .ToDictionary(p => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(p))))!, p => p);
        var native = SessionCatalog.Files(Path.Combine(home, "conversations"), "*.db")
            .Concat(SessionCatalog.Files(Path.Combine(home, "conversations"), "*.pb"))
            .GroupBy(Path.GetFileNameWithoutExtension).ToDictionary(g => g.Key!, g => g.OrderBy(p => Path.GetExtension(p) == ".db" ? 0 : 1).First());
        foreach (var id in transcripts.Keys.Union(native.Keys))
        {
            SessionItem? item = null;
            var path = transcripts.GetValueOrDefault(id) ?? native[id];
            try { item = Read(home, id, path, native.GetValueOrDefault(id) ?? ""); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException or ArgumentException)
            { errors?.Add($"Antigravity {path}: {e.Message}"); }
            if (item != null) yield return item;
        }
    }
    static SessionItem Read(string home, string id, string path, string dbPath)
    {
            var ide = Path.GetFileName(home) == "antigravity-ide";
            var stat = new FileInfo(path);
            var item = new SessionItem { Provider = "antigravity", Client = ide ? "Antigravity IDE" : "Antigravity", Source = ide ? "ide" : "app",
                ProviderHome = home, Path = path, Id = id, DesktopPath = dbPath, Size = stat.Length,
                Updated = stat.LastWriteTimeUtc.ToString("O"), MetadataOnly = Path.GetExtension(path) == ".pb" };
            item.Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home + "\0" + id)))[..24].ToLowerInvariant();
            if (File.Exists(dbPath) && Path.GetExtension(dbPath) == ".db")
            {
                using var db = HermesStore.Open(dbPath); using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT data FROM trajectory_metadata_blob LIMIT 1";
                if (cmd.ExecuteScalar() is byte[] metadata)
                {
                    item.Cwd = AntigravityProto.Workspace(metadata); item.Subagent = AntigravityProto.Text(metadata, 5).Length > 0;
                    item.Created = AntigravityProto.Timestamp(metadata, 2);
                }
            }
            foreach (var (_, message) in Records(item).Take(20))
            {
                if (item.Created.Length == 0) item.Created = message.Timestamp;
                if (message.Role == "user" && item.Title.Length == 0) item.Title = SessionJson.Cut(message.Text, 180);
            }
            EnrichSummary(item);
            item.ContentStamp = Stamp(item);
            return item;
    }
    static void EnrichSummary(SessionItem item)
    {
        var path = Path.Combine(item.ProviderHome, "conversation_summaries.db"); if (!File.Exists(path)) return;
        using var db = HermesStore.Open(path); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT title,workspace_uris,last_modified_time,parent_conversation_id FROM conversation_summaries WHERE conversation_id=$id";
        cmd.Parameters.AddWithValue("$id", item.Id); using var reader = cmd.ExecuteReader(); if (!reader.Read()) return;
        if (reader.GetString(0).Length > 0) item.Title = reader.GetString(0);
        if (item.Cwd.Length == 0 && reader.GetString(1).Length > 0)
        {
            using var doc = JsonDocument.Parse(reader.GetString(1));
            var uri = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0].GetString() : null;
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile) item.Cwd = parsed.LocalPath;
        }
        if (DateTimeOffset.TryParse(reader.GetString(2), out var updated)) item.Updated = updated.ToUniversalTime().ToString("O");
        item.Subagent |= reader.GetString(3).Length > 0;
    }
    public static string Stamp(SessionItem item) => "native-tools-v2:" + string.Join("|", new[] { item.Path, item.DesktopPath, item.DesktopPath + "-wal", item.Path + "-wal" }
        .Where(File.Exists).Distinct().Select(SessionCatalog.Stamp));

    public static SessionMessage? Message(JsonElement row)
    {
        var type = row.Str("type");
        var role = type switch { "USER_INPUT" => "user", "PLANNER_RESPONSE" => "assistant",
            "SYSTEM_MESSAGE" or "EPHEMERAL_MESSAGE" or "CONVERSATION_HISTORY" or "KNOWLEDGE_ARTIFACTS" or "CHECKPOINT" => "metadata", _ => "tool" };
        if (role == "metadata") return null;
        var text = Journal.Content(row.Get("content"));
        if (row.Str("thinking").Length > 0) text += "\n\n[Thinking]\n" + row.Str("thinking");
        if (row.Get("tool_calls").ValueKind is JsonValueKind.Array or JsonValueKind.Object) text += "\n\n[Tool calls]\n" + row.Get("tool_calls").GetRawText();
        if (row.Str("error").Length > 0) text += "\n\n[Error]\n" + row.Str("error");
        if (role == "tool" && text.Length > 0) text = "[" + type + "]\n" + text;
        return text.Length == 0 ? null : new(role, text.Trim(), row.Str("created_at"));
    }
    public static IEnumerable<(long Offset, SessionMessage Message)> Records(SessionItem item)
    {
        if (Path.GetExtension(item.Path) == ".jsonl")
        {
            var pending = new List<(string Id, string Name)>();
            foreach (var (offset, row) in Journal.Rows(item.Path))
            {
                var type = row.Str("type"); var timestamp = row.Str("created_at");
                if (type == "PLANNER_RESPONSE")
                {
                    var text = Journal.Content(row.Get("content"));
                    if (row.Str("thinking").Length > 0) text += "\n\n[Thinking]\n" + row.Str("thinking");
                    if (text.Trim().Length > 0) yield return (offset, new("assistant", text.Trim(), timestamp));
                    var calls = row.Get("tool_calls");
                    foreach (var call in calls.ValueKind == JsonValueKind.Array ? calls.EnumerateArray().ToArray() : calls.ValueKind == JsonValueKind.Object ? new[] { calls } : Array.Empty<JsonElement>())
                    {
                        var name = call.Str("name"); if (name.Length == 0) continue;
                        var id = call.Str("id"); if (id.Length == 0) id = $"ag_{offset}_{pending.Count}";
                        var args = call.Get("args");
                        var arguments = args.ValueKind == JsonValueKind.Object ? DecodeArguments(args) : JsonSerializer.Serialize(new { input = args.ValueKind == JsonValueKind.Undefined ? "" : args.ToString() });
                        pending.Add((id, name));
                        yield return (offset, new("tool", name + "\n" + arguments, timestamp, ToolName: name, CallId: id, ToolArguments: arguments));
                    }
                }
                else if (Message(row) is { } message)
                {
                    if (message.Role == "tool")
                    {
                        var name = type.ToLowerInvariant(); if (name == "list_directory") name = "list_dir";
                        var index = pending.FindIndex(p => p.Name == name);
                        if (index >= 0)
                        {
                            var call = pending[index]; pending.RemoveAt(index);
                            yield return (offset, message with { CallId = call.Id });
                        }
                        else
                        {
                            var id = $"ag_result_{offset}";
                            yield return (offset, new("tool", name, timestamp, ToolName: name, CallId: id, ToolArguments: "{}"));
                            yield return (offset, message with { CallId = id });
                        }
                    }
                    else yield return (offset, message);
                }
            }
            foreach (var call in pending)
                yield return (new FileInfo(item.Path).Length, new("tool", "[Result absent in source history]", "", CallId: call.Id));
            yield break;
        }
        if (item.MetadataOnly) yield break;
        using var db = HermesStore.Open(item.Path); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT idx,step_type,status,metadata,step_payload,step_format FROM steps ORDER BY idx";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(2) is 4 or 5 or 6 || reader.GetInt32(5) != 0 || reader.IsDBNull(4)) continue;
            var type = reader.GetInt32(1); var payload = (byte[])reader[4];
            var envelope=AntigravityProto.Fields(payload);
            if(envelope.Any(f=>f.Number==1 && f.Data.Length==0 && f.Value==(ulong)type))
            {
                var field=type switch {14=>19,15=>20,38=>47,_=>0};
                if(field>0)payload=AntigravityProto.Blob(payload,field);
            }
            var text = type switch
            {
                14 => AntigravityProto.Text(payload, 1, 2) + string.Join("\n", AntigravityProto.Fields(payload)
                    .Where(f => f.Number == 3).Select(f => AntigravityProto.Text(f.Data, 1))),
                15 => AntigravityProto.Text(payload, 8) is { Length: > 0 } modified ? modified : AntigravityProto.Text(payload, 1),
                8 => AntigravityProto.Text(payload, 1, 4, 9), 9 => AntigravityProto.Text(payload, 1, 2),
                7 => AntigravityProto.Text(payload, 1, 3, 5, 10, 11), 5 => AntigravityProto.Text(payload, 8, 26),
                21 => AntigravityProto.Text(payload, 23, 25, 1, 2, 3, 4, 5, 7, 8, 24),
                82 => AntigravityProto.Text(payload, 2),
                38 => AntigravityProto.Text(payload,3),
                132 => AntigravityProto.Text(AntigravityProto.Blob(payload, 2), 1), _ => ""
            };
            if (type == 15 && AntigravityProto.Text(payload, 3) is { Length: > 0 } thinking) text += "\n\n[Thinking]\n" + thinking;
            if (type == 15)
            {
                foreach (var call in AntigravityProto.Fields(payload).Where(f => f.Number == 7))
                    yield return (reader.GetInt64(0),new("tool",AntigravityProto.Text(call.Data,2,3),"",ToolName:AntigravityProto.Text(call.Data,2),CallId:AntigravityProto.Text(call.Data,1),ToolArguments:AntigravityProto.Text(call.Data,3)));
            }
            if (type == 21)
                foreach (var output in AntigravityProto.Fields(payload).Where(f => f.Number is 19 or 20 or 21 or 26))
                    text += "\n" + AntigravityProto.Text(output.Data, 1, 2);
            if (text.Length == 0) continue;
            var timestamp = reader.IsDBNull(3) ? "" : AntigravityProto.Timestamp((byte[])reader[3]);
            yield return (reader.GetInt64(0), new(type == 14 ? "user" : type is 15 or 82 ? "assistant" : "tool", text, timestamp,
                CallId:type==38?AntigravityProto.Text(AntigravityProto.Blob(payload,2),1):""));
        }
    }
    static string DecodeArguments(JsonElement args)
    {
        var decoded = new Dictionary<string, JsonElement>();
        foreach (var property in args.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.String)
                try { using var parsed = JsonDocument.Parse(value.GetString()!); value = parsed.RootElement.Clone(); }
                catch (JsonException) { }
            decoded[property.Name] = value;
        }
        return JsonSerializer.Serialize(decoded);
    }
}
