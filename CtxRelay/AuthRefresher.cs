using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CtxRelay;

sealed class AuthRefresher(Store store, HttpClient? client = null)
{
    const string TokenUrl = "https://auth.openai.com/oauth/token";
    const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(20) };
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    readonly ConcurrentDictionary<string, string> Rejected = new();

    public async Task<AuthFile> EnsureAsync(AuthFile auth, bool force = false)
    {
        var gate = Gates.GetOrAdd(auth.AccountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            using var fileLock = await AcquireFileLock(auth.Path + ".refresh.lock");
            var current = store.ReloadForRefresh(auth);
            if ((!force || !current.Raw.AsSpan().SequenceEqual(auth.Raw)) &&
                current.AccessToken.Length > 0 &&
                (current.AccessExp == null || current.AccessExp > DateTime.UtcNow.AddMinutes(5))) return current;
            if (string.IsNullOrWhiteSpace(current.RefreshToken))
            {
                if (!force && !current.AccessExpired && current.AccessToken.Length > 0) return current;
                throw new InvalidOperationException(Localization.Text(Message.RefreshUnavailable));
            }
            if (Rejected.TryGetValue(current.AccountId, out var rejected) && rejected == current.RefreshToken)
                throw new InvalidOperationException(Localization.Text(Message.RefreshSignIn));
            return await RefreshAsync(current);
        }
        finally { gate.Release(); }
    }
    static async Task<FileStream> AcquireFileLock(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(100); }
        }
    }

    async Task<AuthFile> RefreshAsync(AuthFile auth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = JsonContent.Create(new { grant_type = "refresh_token", client_id = ClientId, refresh_token = auth.RefreshToken })
        };
        using var response = await (client ?? Http).SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                var error = JsonNode.Parse(body)?["error"];
                code = error is JsonObject obj ? obj["code"]?.GetValue<string>() : error?.GetValue<string>();
            }
            catch { /* Never include the response body: it may contain credentials. */ }
            if (response.StatusCode == HttpStatusCode.Unauthorized || code is
                "invalid_grant" or "refresh_token_expired" or "refresh_token_reused" or "refresh_token_invalidated")
            {
                Rejected[auth.AccountId] = auth.RefreshToken;
                throw new InvalidOperationException(Localization.Text(Message.RefreshSignIn));
            }
            throw new InvalidOperationException(Localization.Text(Message.RefreshFailed, (int)response.StatusCode));
        }
        JsonNode tokens;
        try { tokens = JsonNode.Parse(body) ?? throw new InvalidDataException(); }
        catch { throw new InvalidDataException(Localization.Text(Message.InvalidRefreshResponse)); }
        var updated = auth.WithRefreshedTokens(tokens);
        var saved = store.SaveRefreshed(auth, updated);
        Rejected.TryRemove(auth.AccountId, out _);
        Log.Write($"[refresh] {auth.Email}: credentials saved");
        return saved;
    }
}
