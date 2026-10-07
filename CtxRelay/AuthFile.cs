using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CtxRelay;

/// <summary>Разобранный auth.json. Raw — байты как на диске: копируем их, не пересериализуем.</summary>
sealed class AuthFile
{
    public string Path { get; }
    public byte[] Raw { get; }
    public string AccountId { get; }
    public string AccessToken { get; }
    public string RefreshToken { get; }
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
                   ?? throw new InvalidDataException(Localization.Text(Message.EmptyJson, path));
        var tokens = root["tokens"];
        AccountId = tokens?["account_id"]?.GetValue<string>() ?? "";
        if (AccountId.Length == 0) throw new InvalidDataException(Localization.Text(Message.MissingAccountId, path));
        AccessToken = tokens?["access_token"]?.GetValue<string>() ?? "";
        RefreshToken = tokens?["refresh_token"]?.GetValue<string>() ?? "";
        LastRefreshRaw = root["last_refresh"]?.GetValue<string>() ?? "";
        LastRefresh = ParseTs(LastRefreshRaw);

        var id = JwtClaims(tokens?["id_token"]?.GetValue<string>());
        var at = JwtClaims(AccessToken);
        Email = id?["email"]?.GetValue<string>()
                ?? at?["https://api.openai.com/profile"]?["email"]?.GetValue<string>() ?? "";
        if (at?["exp"] is JsonValue exp && exp.TryGetValue<long>(out var sec))
            AccessExp = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
    }

    public byte[] WithRefreshedTokens(JsonNode response)
    {
        var access = response["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidDataException(Localization.Text(Message.InvalidRefreshResponse));
        var account = JwtClaims(access)?["https://api.openai.com/auth"]?["chatgpt_account_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(account) && account != AccountId)
            throw new InvalidDataException(Localization.Text(Message.RefreshAccountChanged));
        var root = JsonNode.Parse(Encoding.UTF8.GetString(Raw).TrimStart('\uFEFF'))!;
        var tokens = root["tokens"]!;
        tokens["access_token"] = access;
        foreach (var key in new[] { "refresh_token", "id_token" })
            if (response[key]?.GetValue<string>() is { Length: > 0 } value) tokens[key] = value;
        root["last_refresh"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return Encoding.UTF8.GetBytes(root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
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
