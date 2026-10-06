using System.Net;
using System.Text.Json.Nodes;

namespace CdxSwapper;

sealed record UsageWindow(double UsedPercent, DateTime? ResetAt)
{
    public double Left => Math.Max(0, 100 - UsedPercent);
}

sealed class UsageInfo
{
    public required string Name { get; init; }
    public required string Email { get; init; }
    public bool Active { get; init; }
    public bool? LimitReached { get; set; }
    public UsageWindow? Primary { get; set; }     // 5h
    public UsageWindow? Secondary { get; set; }   // неделя
    public string? Error { get; set; }

    /// <summary>Минимум остатка из двух окон — то, что реально ограничивает.</summary>
    public double? Left => Primary == null && Secondary == null ? null
        : Math.Min(Primary?.Left ?? 100, Secondary?.Left ?? 100);
}

static class UsageClient
{
    const string Url = "https://chatgpt.com/backend-api/wham/usage";
    static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<UsageInfo> FetchAsync(string name, AuthFile auth, bool active, AuthRefresher refresher, HttpClient? client = null)
    {
        var info = new UsageInfo { Name = name, Email = auth.Email, Active = active };
        try { auth = await refresher.EnsureAsync(auth); }
        catch (Exception e)
        {
            info.Error = Log.Error("refresh", e);
            return info;
        }
        var recovered = false;
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.AccessToken);
                req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", "CdxSwapper/1.0");
                using var resp = await (client ?? Http).SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                var code = (int)resp.StatusCode;
                if (code == 401 && !recovered)
                {
                    recovered = true;
                    try { auth = await refresher.EnsureAsync(auth, force: true); }
                    catch (Exception e) { info.Error = Log.Error("refresh", e); return info; }
                    attempt--;
                    continue;
                }
                if (code >= 500 && attempt < 3) { await Task.Delay(2000 * attempt); continue; }
                if (code != 200) { info.Error = $"step=usage | HTTP {code} | server: {Log.Short(body, 300)}"; return info; }
                Parse(info, body);
                return info;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = e;
                await Task.Delay(2000 * attempt);
            }
            catch (Exception e) { info.Error = Log.Error("usage", e); return info; }
        }
        info.Error = Log.Error("usage", last!);
        return info;
    }

    static void Parse(UsageInfo info, string body)
    {
        var rl = JsonNode.Parse(body)?["rate_limit"];
        if (rl == null) { info.Error = $"step=usage_parse | {Localization.Text(Message.MissingRateLimit)} | server: {Log.Short(body, 300)}"; return; }
        info.LimitReached = rl["limit_reached"]?.GetValue<bool>();
        info.Primary = Window(rl["primary_window"]);
        info.Secondary = Window(rl["secondary_window"]);
    }

    static UsageWindow? Window(JsonNode? w)
    {
        if (w == null) return null;
        var used = w["used_percent"]?.GetValue<double>() ?? 0;
        DateTime? reset = w["reset_at"] is JsonValue v && v.TryGetValue<long>(out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime : null;
        return new UsageWindow(used, reset);
    }
}
