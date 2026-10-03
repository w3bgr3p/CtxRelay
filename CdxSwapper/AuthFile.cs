using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CdxSwapper;

/// <summary>Разобранный auth.json. Raw — байты как на диске: копируем их, не пересериализуем.</summary>
sealed class AuthFile
{
    public string Path { get; }
    public byte[] Raw { get; }
    public string AccountId { get; }
    public string AccessToken { get; }
    public string LastRefreshRaw { get; }
    public DateTime? LastRefresh { get; }
    public string Email { get; }
    public DateTime? AccessExp { get; }

    public bool AccessExpired => AccessExp is { } e && e <= DateTime.UtcNow;

    public AuthFile(string path)
    {
        Path = path;
        Raw = File.ReadAllBytes(path);
        var root = JsonNode.Parse(Encoding.UTF8.GetString(Raw).TrimStart('\uFEFF'))
                   ?? throw new InvalidDataException($"{path}: пустой JSON");
        var tokens = root["tokens"];
        AccountId = tokens?["account_id"]?.GetValue<string>() ?? "";
        if (AccountId.Length == 0) throw new InvalidDataException($"{path}: нет tokens.account_id");
        AccessToken = tokens?["access_token"]?.GetValue<string>() ?? "";
        LastRefreshRaw = root["last_refresh"]?.GetValue<string>() ?? "";
        LastRefresh = ParseTs(LastRefreshRaw);

        var id = JwtClaims(tokens?["id_token"]?.GetValue<string>());
        var at = JwtClaims(AccessToken);
        Email = id?["email"]?.GetValue<string>()
                ?? at?["https://api.openai.com/profile"]?["email"]?.GetValue<string>() ?? "";
        if (at?["exp"] is JsonValue exp && exp.TryGetValue<long>(out var sec))
            AccessExp = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
    }

    public static AuthFile? TryLoad(string path)
    {
        try { return File.Exists(path) ? new AuthFile(path) : null; }
        catch (Exception e) { Log.Write($"[warn] не читается {path}: {Log.Error("read_auth", e)}"); return null; }
    }

    static JsonNode? JwtClaims(string? token)
    {
        try
        {
            var p = token?.Split('.');
            if (p is not { Length: >= 2 }) return null;
            var s = p[1].Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            return JsonNode.Parse(Convert.FromBase64String(s));
        }
        catch { return null; }
    }

    /// <summary>2026-09-25T14:58:53.006686900Z -> UTC. Дробная часть обрезается до 7 знаков.</summary>
    static DateTime? ParseTs(string v)
    {
        var m = System.Text.RegularExpressions.Regex.Match(v ?? "", @"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?");
        if (!m.Success) return null;
        var frac = (m.Groups[2].Success ? m.Groups[2].Value : "0");
        frac = (frac.Length > 7 ? frac[..7] : frac).PadRight(7, '0');
        return DateTime.TryParseExact($"{m.Groups[1].Value}.{frac}", "yyyy-MM-ddTHH:mm:ss.fffffff",
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;
    }
}
