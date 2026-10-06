using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CdxSwapper.Sessions;

sealed class SessionCatalog
{
    public string CodexHome { get; }
    public string ClaudeHome { get; }
    public string? HermesHome { get; }
    readonly string? antigravityRoot;
    readonly List<string> desktopRoots;
    readonly object gate = new();
    readonly Dictionary<string, (string Stamp, SessionItem Item)> cache = new();
    Dictionary<string, SessionItem> items = new();

    public SessionCatalog(string codexHome, string claudeHome, IEnumerable<string>? desktopRoots = null, string? hermesHome = null, string? antigravityRoot = null)
    {
        CodexHome = Path.GetFullPath(codexHome); ClaudeHome = Path.GetFullPath(claudeHome);
        HermesHome = hermesHome == null ? null : Path.GetFullPath(hermesHome);
        this.antigravityRoot = antigravityRoot;
        this.desktopRoots = desktopRoots?.ToList() ?? (string.Equals(ClaudeHome,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            StringComparison.OrdinalIgnoreCase) ? DesktopSessions.Roots() : new());
    }

    public static string Stamp(string path) { var f = new FileInfo(path); return $"{f.LastWriteTimeUtc.Ticks}:{f.Length}"; }
    public static IEnumerable<string> Files(string root, string pattern = "*.jsonl") => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true,
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }) : Array.Empty<string>();

    static SessionItem Metadata(string path, string provider)
    {
        var stat = new FileInfo(path);
        var item = new SessionItem { Provider = provider, Path = path, Id = Path.GetFileNameWithoutExtension(path),
            Size = stat.Length, Updated = stat.LastWriteTimeUtc.ToString("O"),
            Archived = path.Split(Path.DirectorySeparatorChar).Contains("archived_sessions"),
            Subagent = path.Split(Path.DirectorySeparatorChar).Contains("subagents") };
        if (Path.GetExtension(path) == ".json")
        {
            using var stream = Journal.Open(path); using var doc = JsonDocument.Parse(stream);
            DesktopSessions.Enrich(item, path, doc.RootElement); item.MetadataOnly = true; return item;
        }
        var head = Journal.Rows(path).TakeWhile(r => r.Offset < 262144).Select(r => r.Row).ToList();
        var tailStart = Journal.RecordStart(path, Math.Max(0, stat.Length - 131072));
        var rows = head.Concat(Journal.Rows(path, tailStart).Select(r => r.Row));
        foreach (var row in rows)
        {
            if (provider == "codex")
            {
                var payload = row.Get("payload"); var kind = row.Str("type");
                if (kind == "session_meta")
                {
                    item.Id = payload.Str("id") is { Length: > 0 } id ? id : item.Id;
                    item.Cwd = payload.Str("cwd"); item.Created = payload.Str("timestamp");
                    item.Version = payload.Str("cli_version"); item.Subagent |= payload.Get("source").ValueKind == JsonValueKind.Object;
                    item.HistoryMode = payload.Str("history_mode");
                    if (item.HistoryMode == "paginated") item.Client = "Codex Desktop";
                }
                if (kind == "event_msg" && payload.Str("type") == "user_message" && item.Title == "")
                    item.Title = SessionJson.Cut(payload.Str("message"), 180);
                if (kind == "turn_context" && payload.Str("model") != "") item.Model = payload.Str("model");
            }
            else
            {
                if (row.Str("sessionId") != "") item.Id = row.Str("sessionId");
                if (row.Str("cwd") != "") item.Cwd = row.Str("cwd");
                if (row.Str("version") != "") item.Version = row.Str("version");
                if (item.Created == "") item.Created = row.Str("timestamp");
                item.Subagent |= row.Bool("isSidechain");
                var msg = row.Get("message");
                if (msg.Str("model") != "") item.Model = msg.Str("model");
                if (row.Str("type") == "user" && item.Title == "") item.Title = SessionJson.Cut(
                    string.Join("\n",Journal.Messages("claude",row).Where(m=>m.Role=="user").Select(m=>m.Text)),180);
                if (row.Str("type") is "summary" or "custom-title")
                    item.Title = row.Str("customTitle") is { Length: > 0 } title ? title : row.Str("summary") is { Length: > 0 } summary ? summary : item.Title;
            }
        }
        return item;
    }

    void EnrichCodex(IEnumerable<SessionItem> group)
    {
        if (!Directory.Exists(CodexHome)) return;
        var path = Directory.EnumerateFiles(CodexHome, "state_*.sqlite").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (path == null) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM threads";
        using var reader = command.ExecuteReader();
        var byId = group.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.ToList());
        var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => i);
        while (reader.Read())
        {
            string Str(string name) => columns.TryGetValue(name, out var n) && !reader.IsDBNull(n) ? reader.GetValue(n).ToString() ?? "" : "";
            if (!byId.TryGetValue(Str("id"), out var matched)) continue;
            foreach (var item in matched)
            {
                if (Str("title") != "") item.Title = Str("title");
                if (Str("cwd") != "") item.Cwd = Str("cwd");
                if (Str("model") != "") item.Model = Str("model");
                if (columns.ContainsKey("archived")) item.Archived = Str("archived") == "1";
                item.Subagent |= Str("agent_role") != "";
                if (long.TryParse(Str("tokens_used"), out var tokens)) item.Tokens = tokens;
            }
        }
    }

    public SessionScan Scan()
    {
        lock (gate)
        {
            var errors = new List<string>();
            var hermesHomes = new List<string>();
            var found = new Dictionary<string, SessionItem>();
            void Add(string path, string provider)
            {
                try
                {
                    path = Path.GetFullPath(path);
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24].ToLowerInvariant();
                    var stamp = Stamp(path);
                    if (!cache.TryGetValue(key, out var cached) || cached.Stamp != stamp)
                        cache[key] = cached = (stamp, Metadata(path, provider));
                    found[key] = cached.Item with { Key = key };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { errors.Add($"{path}: {e.Message}"); }
            }
            foreach (var path in Files(Path.Combine(CodexHome, "sessions")).Concat(Files(Path.Combine(CodexHome, "archived_sessions")))) Add(path, "codex");
            foreach (var path in Files(Path.Combine(ClaudeHome, "projects"))) Add(path, "claude");
            var cliItems = found.Values.Where(s => s.Provider == "claude").GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var (path, data) in DesktopSessions.Entries(desktopRoots))
            {
                if (cliItems.TryGetValue(data.Str("cliSessionId"), out var cli)) DesktopSessions.Enrich(cli, path, data);
                else Add(path, "claude");
            }
            try { EnrichCodex(found.Values.Where(s => s.Provider == "codex")); }
            catch (Exception e) when (e is IOException or SqliteException) { errors.Add("Codex index: " + e.Message); }
            if (HermesHome != null)
            {
                try { hermesHomes = HermesStore.Homes(HermesHome); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"Hermes sources: {e.Message}"); }
                foreach (var home in hermesHomes)
                {
                    try { foreach (var item in HermesStore.Scan(home)) found[item.Key] = item; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { errors.Add($"Hermes {home}: {e.Message}"); }
                }
            }
            foreach (var key in cache.Keys.Except(found.Keys).ToList()) cache.Remove(key);
            if (antigravityRoot != null)
                foreach (var home in AntigravityStore.Homes(antigravityRoot))
                    try { foreach (var item in AntigravityStore.Scan(home, errors)) found[item.Key] = item; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException)
                    { errors.Add($"Antigravity {home}: {e.Message}"); }
            items = found;
            return new(found.Values.OrderByDescending(s => s.Updated, StringComparer.Ordinal).ToList(), errors,
                new() { ["codex"] = CodexHome, ["claude"] = ClaudeHome, ["claude_desktop"] = string.Join("; ", desktopRoots),
                    ["hermes"] = hermesHomes.Count > 0 ? string.Join("; ", hermesHomes) : HermesHome ?? "",
                    ["antigravity"] = antigravityRoot == null ? "" : string.Join("; ", AntigravityStore.Homes(antigravityRoot)) });
        }
    }

    public SessionItem Get(string key)
    {
        lock (gate) return items.TryGetValue(key, out var item) ? item with { }
            : throw new KeyNotFoundException(SessionText.Pick("Session not found. Refresh the list.", "Сессия не найдена. Обновите список.", "Sesión no encontrada. Actualiza la lista."));
    }

    public SessionPage Detail(string key, long? before = null)
    {
        var item = Get(key);
        if (item.Provider == "hermes") return HermesStore.Detail(item, before);
        if (item.Provider == "antigravity" && Path.GetExtension(item.Path) != ".jsonl")
        {
            if (before < 0) throw new ArgumentException("Invalid cursor.");
            var rows = AntigravityStore.Records(item).Where(r => before == null || r.Offset < before).TakeLast(50).ToList();
            var nativeMessages = rows.Select(r => r.Message with { Truncated = r.Message.Text.Length > 60000, Text = SessionJson.Cut(r.Message.Text, 60000) }).ToList();
            return new(item, nativeMessages, rows.Count == 50 ? rows[0].Offset : null, item.Size);
        }
        if (item.MetadataOnly)
        {
            var summary = DesktopSessions.Summary(item.Path);
            return new(item, summary.Text != "" ? new() { summary } : new(), null, item.Size);
        }
        var size = new FileInfo(item.Path).Length;
        if (before < 0 || before > size) throw new ArgumentException("Invalid cursor; refresh the session.");
        var end = before ?? size;
        var start = Journal.RecordStart(item.Path, Math.Max(0, end - 524288));
        var messages = Journal.Rows(item.Path, start, end).SelectMany(r => Journal.Messages(item.Provider, r.Row))
            .Where(m => m != null && m.Role != "metadata" && m.Text.Length > 0)
            .Select(m => m! with { Truncated = m!.Text.Length > 60000, Text = SessionJson.Cut(m.Text, 60000) }).ToList();
        return new(item, messages, start > 0 ? start : null, end - start);
    }

    public SessionMessage? Match(string key, long offset)
    {
        var item = Get(key);
        if (item.Provider == "hermes") return HermesStore.Match(item, offset);
        if (item.Provider == "antigravity" && Path.GetExtension(item.Path) != ".jsonl")
            return AntigravityStore.Records(item).FirstOrDefault(r => r.Offset == offset).Message;
        if (offset < 0 || offset > new FileInfo(item.Path).Length) throw new ArgumentException("Invalid offset.");
        if (item.MetadataOnly) return DesktopSessions.Summary(item.Path);
        var row = Journal.Rows(item.Path, offset).FirstOrDefault();
        if(row.Row.ValueKind==JsonValueKind.Undefined)return null;
        var messages=Journal.Messages(item.Provider,row.Row).ToList();
        return messages.Count==0?null:messages[0] with { Text=string.Join("\n\n",messages.Select(m=>m.Text)) };
    }
}
