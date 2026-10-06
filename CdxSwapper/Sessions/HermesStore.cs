using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CdxSwapper.Sessions;

static class HermesStore
{
    public static string DefaultHome => Path.GetFullPath(Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes"));
    public static string Profile(string home) => Path.GetFileName(Path.GetDirectoryName(home)) == "profiles" ? Path.GetFileName(home) : "default";
    public static List<string> Homes(string home)
    {
        home = Path.GetFullPath(home);
        var root = Profile(home) == "default" ? home : Path.GetDirectoryName(Path.GetDirectoryName(home))!;
        var result = new List<string> { home, root };
        var profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles)) result.AddRange(Directory.EnumerateDirectories(profiles));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Where(h => File.Exists(Path.Combine(h, "state.db"))).ToList();
    }
    public static SqliteConnection Open(string path, bool write = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false,
            Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, DefaultTimeout = 10 }.ToString());
        db.Open(); return db;
    }
    static Dictionary<string, int> Columns(SqliteDataReader reader) => Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => i);
    static string Str(SqliteDataReader reader, Dictionary<string, int> cols, string name) =>
        cols.TryGetValue(name, out var n) && !reader.IsDBNull(n) ? Convert.ToString(reader.GetValue(n), CultureInfo.InvariantCulture) ?? "" : "";
    static string Timestamp(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && seconds >= -62135596800 && seconds <= 253402300799 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToString("O") : "";

    public static List<SessionItem> Scan(string home)
    {
        var path = Path.Combine(home, "state.db");
        using var db = Open(path); using var transaction = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = "SELECT * FROM sessions";
        var items = new List<SessionItem>();
        using (var reader = cmd.ExecuteReader())
        {
            var cols = Columns(reader);
            while (reader.Read())
            {
                string Get(string name) => Str(reader, cols, name);
                var id = Get("id"); var source = Get("source");
                items.Add(new() { Provider = "hermes", Id = id, Path = path, ProviderHome = home, Profile = Profile(home), Source = source,
                    Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\0" + id)))[..24].ToLowerInvariant(),
                    Title = Get("title"), Cwd = Get("cwd") is { Length: > 0 } cwd ? cwd : Get("git_repo_root"), Model = Get("model"),
                    Created = Timestamp(Get("started_at")), Updated = Timestamp(Get("last_activity_at")) is { Length: > 0 } updated ? updated : Timestamp(Get("started_at")),
                    Archived = Get("archived") == "1", Subagent = source == "tool", Tokens =
                        (long.TryParse(Get("input_tokens"), out var input) ? input : 0) + (long.TryParse(Get("output_tokens"), out var output) ? output : 0) });
            }
        }
        foreach (var item in items)
        {
            var records = Records(db, transaction, item.Id);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (id, message) in records)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(SessionJson.Serialize(new { id, message })));
                item.Size += Encoding.UTF8.GetByteCount(message.Text);
                if (item.Title.Length == 0 && message.Role == "user") item.Title = SessionJson.Cut(message.Text, 180);
                if (string.CompareOrdinal(message.Timestamp, item.Updated) > 0) item.Updated = message.Timestamp;
            }
            item.ContentStamp = Convert.ToHexString(hash.GetHashAndReset());
        }
        return items;
    }

    static SessionMessage Decode(SqliteDataReader reader, Dictionary<string,int> cols, bool includeCalls = true)
    {
        string Get(string name) => Str(reader, cols, name);
        var role = Get("role"); var content = Get("content");
        if (content.TrimStart().StartsWith('['))
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.EnumerateArray().Any(b =>
                    b.Str("type") is "text" or "input_text" or "output_text" or "image_url" or "image" or "input_image" or "input_audio" or "audio"))
                    content = string.Join("\n",doc.RootElement.EnumerateArray().Select(b => b.Str("type") switch {
                        "text" or "input_text" or "output_text" => b.Str("text"),
                        "image_url" or "image" or "input_image" => "[Image]", "input_audio" or "audio" => "[Audio]", _ => b.GetRawText() }));
            }
            catch (JsonException) { }
        var calls = Get("tool_calls");
        if (includeCalls && calls.Length > 0 && calls != "[]" && calls != "null") content += "\n[Historical tool calls]\n" + calls;
        if (role == "tool" && Get("tool_name").Length > 0) content = Get("tool_name") + "\n" + content;
        var reasoning = Get("reasoning_content"); if (reasoning.Length == 0) reasoning = Get("reasoning");
        if (reasoning.Length > 0) content += "\n[Reasoning]\n" + reasoning;
        return new(role == "system" ? "metadata" : role is "user" or "assistant" or "tool" ? role : "metadata", content, Timestamp(Get("timestamp")));
    }

    static IEnumerable<(long Offset, SessionMessage Message)> Records(SqliteConnection db, SqliteTransaction? transaction,
        string sessionId, long? before = null, int? limit = null, long? exact = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "SELECT * FROM messages WHERE session_id=$id AND active=1" +
            (before != null ? " AND id < $before" : "") + (exact != null ? " AND id=$exact" : "") +
            " ORDER BY id " + (limit != null ? "DESC LIMIT $limit" : "ASC");
        cmd.Parameters.AddWithValue("$id", sessionId);
        if (before != null) cmd.Parameters.AddWithValue("$before", before.Value);
        if (limit != null) cmd.Parameters.AddWithValue("$limit", limit.Value);
        if (exact != null) cmd.Parameters.AddWithValue("$exact", exact.Value);
        using var reader = cmd.ExecuteReader(); var cols = Columns(reader);
        while (reader.Read()) yield return (reader.GetInt64(cols["id"]), Decode(reader, cols));
    }
    public static IEnumerable<(long Offset, SessionMessage Message)> Records(SessionItem item)
    {
        using var db = Open(item.Path);
        foreach (var record in Records(db, null, item.Id)) yield return record;
    }
    public static IEnumerable<(long Offset, SessionMessage Message)> NativeRecords(SessionItem item)
    {
        using var db = Open(item.Path); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE session_id=$id AND active=1 ORDER BY id";
        cmd.Parameters.AddWithValue("$id",item.Id); using var reader = cmd.ExecuteReader(); var cols = Columns(reader);
        while (reader.Read())
        {
            string Get(string name) => Str(reader,cols,name);
            var offset = reader.GetInt64(cols["id"]); var role = Get("role"); var timestamp = Timestamp(Get("timestamp"));
            if (role == "tool")
            {
                var output = Get("content");
                try { using var doc = JsonDocument.Parse(output); if (doc.RootElement.Get("cdx_source_result").ValueKind == JsonValueKind.String) output = doc.RootElement.Str("cdx_source_result"); }
                catch (JsonException) { }
                yield return (offset,new("tool",output,timestamp,CallId:Get("tool_call_id"))); continue;
            }
            var content = Decode(reader,cols,includeCalls:false).Text;
            if (content.Length > 0) yield return (offset,new(role is "user" or "assistant" ? role : "metadata",content,timestamp));
            var calls = Get("tool_calls"); if (role != "assistant" || calls.Length == 0) continue;
            JsonElement data;
            try { using var doc = JsonDocument.Parse(calls); data = doc.RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (data.ValueKind != JsonValueKind.Array) continue;
            foreach (var call in data.EnumerateArray())
            {
                var function = call.Get("function"); var name = function.Str("name"); var args = function.Str("arguments");
                try { using var doc = JsonDocument.Parse(args); if(doc.RootElement.Str("cdx_source_tool").Length > 0) { name = doc.RootElement.Str("cdx_source_tool"); args = doc.RootElement.Get("cdx_source_arguments").GetRawText(); } }
                catch (JsonException) { }
                yield return (offset,new("tool",name+"\n"+args,timestamp,ToolName:name,CallId:call.Str("id"),ToolArguments:args));
            }
        }
    }

    public static string Stamp(SessionItem item)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (id, message) in Records(item)) hash.AppendData(Encoding.UTF8.GetBytes(SessionJson.Serialize(new { id, message })));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static SessionPage Detail(SessionItem item, long? before)
    {
        if (before <= 0) throw new ArgumentException("Invalid message cursor.");
        using var db = Open(item.Path);
        var rows = Records(db, null, item.Id, before, 51).ToList();
        bool more = rows.Count > 50; if (more) rows.RemoveAt(rows.Count - 1);
        rows.Reverse();
        return new(item, rows.Select(r => r.Message with { Truncated = r.Message.Text.Length > 60000,
            Text = SessionJson.Cut(r.Message.Text,60000) }).ToList(), more ? rows[0].Offset : null,
            rows.Sum(r => (long)Encoding.UTF8.GetByteCount(r.Message.Text)));
    }
    public static SessionMessage? Match(SessionItem item, long id)
    {
        if (id <= 0) throw new ArgumentException("Invalid message ID.");
        using var db = Open(item.Path);
        return Records(db, null, item.Id, exact: id).FirstOrDefault().Message;
    }
    public static bool Exists(string path, string id)
    {
        if (!File.Exists(path)) return false;
        using var db = Open(path); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sessions WHERE id=$id"; cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() != null;
    }
}
