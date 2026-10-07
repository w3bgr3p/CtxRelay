using System.Text.Json;
using System.Text.Json.Serialization;

namespace CtxDeck.Sessions;

sealed record SessionItem
{
    public string Key { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Path { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Model { get; set; } = "";
    public string Created { get; set; } = "";
    public string Updated { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Archived { get; set; }
    public bool Subagent { get; set; }
    public bool MetadataOnly { get; set; }
    public long Size { get; set; }
    public long? Tokens { get; set; }
    public string DesktopId { get; set; } = "";
    public string CliId { get; set; } = "";
    public string DesktopPath { get; set; } = "";
    public string Focused { get; set; } = "";
    public string Client { get; set; } = "";
    public string ProviderHome { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Source { get; set; } = "";
    public string HistoryMode { get; set; } = "";
    [JsonIgnore] public string ContentStamp { get; set; } = "";
    public string Revision => ContentStamp;
}

sealed record SessionMessage(string Role, string Text, string Timestamp, bool Truncated = false,
    string ToolName = "", string CallId = "", string ToolArguments = "");
sealed record SessionPage(SessionItem Session, List<SessionMessage> Messages, long? Before,
    [property: JsonPropertyName("bytesRead")] long BytesRead);
sealed record SessionScan(List<SessionItem> Sessions, List<string> Errors, Dictionary<string, string> Sources);
sealed record IndexStatus(bool Busy, int Done, int Total, List<string> Errors);
sealed record SearchHit(long Offset, string Role, string Snippet);
sealed class SearchMatch
{
    public int Count { get; set; }
    public List<SearchHit> Hits { get; } = new();
}
sealed record SearchResult(Dictionary<string, SearchMatch> Matches, IndexStatus Index);
sealed record ConversionResult(string Provider, string Id, string Cwd, string Path, string SourceId,
    string SourcePath, string TranscriptPath, int Messages, string Created, bool Reused, bool Subagent = false);

static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
    public static JsonElement Get(this JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;
    public static string Str(this JsonElement value, string key) => value.Get(key).ValueKind == JsonValueKind.String
        ? value.Get(key).GetString() ?? "" : "";
    public static bool Bool(this JsonElement value, string key) => value.Get(key).ValueKind == JsonValueKind.True;
    public static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
}
