using System.Text.Json;

namespace CtxDeck.Sessions;

static class DesktopSessions
{
    public static List<string> Roots()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string> { Path.Combine(roaming, "Claude"), Path.Combine(local, "Claude") };
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages)) candidates.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
            .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        return candidates.Select(p => Path.Combine(p, "claude-code-sessions")).Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<(string Path, JsonElement Data)> Entries(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        foreach (var path in SessionCatalog.Files(root, "local_*.json"))
        {
            JsonElement data;
            try { using var file = Journal.Open(path); using var doc = JsonDocument.Parse(file); data = doc.RootElement.Clone(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (data.Str("sessionId") != "") yield return (path, data);
        }
    }

    public static string Timestamp(JsonElement data, string name)
    {
        var value = data.Get(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var ms))
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("O"); }
            catch (ArgumentOutOfRangeException) { return ""; }
        return data.Str(name);
    }

    public static void Enrich(SessionItem item, string path, JsonElement data)
    {
        item.Id = item.DesktopId = data.Str("sessionId");
        item.CliId = data.Str("cliSessionId");
        item.DesktopPath = Path.GetFullPath(path);
        item.Client = "Claude Desktop";
        if (data.Str("title") != "") item.Title = data.Str("title");
        if (data.Str("cwd") != "") item.Cwd = data.Str("cwd");
        if (data.Str("model") != "") item.Model = data.Str("model");
        item.Archived = data.Bool("isArchived");
        item.Created = Timestamp(data, "createdAt");
        item.Updated = Timestamp(data, "lastActivityAt");
        if (item.Updated == "") item.Updated = item.Created;
        item.Focused = Timestamp(data, "lastFocusedAt");
    }

    public static SessionMessage Summary(string path)
    {
        using var stream = Journal.Open(path);
        using var doc = JsonDocument.Parse(stream);
        var data = doc.RootElement;
        return new("assistant", data.Get("postTurnSummary").Str("status_detail"), Timestamp(data, "lastActivityAt"));
    }
}
