using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CtxDeck;

await Tests.Run();

static class Tests
{
    static int count;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        count++;
    }

    static string Jwt(string account, int minutes) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        new JsonObject { ["exp"] = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = account } }.ToJsonString()))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test";

    static byte[] Credentials(string account, int minutes, string refresh = "old-refresh") => Encoding.UTF8.GetBytes(
        new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = account, ["access_token"] = Jwt(account, minutes),
            ["refresh_token"] = refresh, ["id_token"] = "old-id" }, ["last_refresh"] = "2026-01-01T00:00:00Z",
            ["extra"] = "preserve-me" }.ToJsonString());

    static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    static HttpResponseMessage Tokens(string account, bool rotate = true) => Reply(200,
        new JsonObject { ["access_token"] = Jwt(account, 60), ["refresh_token"] = rotate ? "new-refresh" : null,
            ["id_token"] = rotate ? "new-id" : null }.ToJsonString());

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request); }
    }

    sealed class Fixture : IDisposable
    {
        readonly string? previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        readonly string root = Path.Combine(Path.GetTempPath(), "cdx-refresh-test-" + Guid.NewGuid());
        public Store Store { get; }
        public Fixture()
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, ".codex"));
            Store = new Store();
        }
        public AuthFile Save(string name, string account, int minutes, string refresh = "old-refresh", bool active = false)
        {
            var bytes = Credentials(account, minutes, refresh);
            Store.AtomicWrite(Store.PathFor(name), bytes);
            if (active) Store.AtomicWrite(Store.ActivePath, bytes);
            return new AuthFile(Store.PathFor(name));
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(root, recursive: true);
        }
    }

    static async Task RotationAndConcurrency()
    {
        using var f = new Fixture();
        var auth = f.Save("one", "one", -10, active: true);
        using var handler = new Handler(async request =>
        {
            Check(request.RequestUri!.ToString() == "https://auth.openai.com/oauth/token", "Wrong endpoint");
            Check(request.Method == HttpMethod.Post, "Wrong refresh method");
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Check(json["grant_type"]!.GetValue<string>() == "refresh_token" &&
                json["client_id"]!.GetValue<string>() == "app_EMoamEEZ73f0CkXaXp7hrann" &&
                json["refresh_token"]!.GetValue<string>() == "old-refresh", "Wrong refresh grant");
            await Task.Delay(30);
            return Tokens("one");
        });
        using var http = new HttpClient(handler);
        var refresher = new AuthRefresher(f.Store, http);
        var refreshed = await Task.WhenAll(refresher.EnsureAsync(auth), refresher.EnsureAsync(auth));
        Check(handler.Calls == 1, "Concurrent calls reused the refresh token");
        Check(refreshed.All(a => !a.AccessExpired && a.RefreshToken == "new-refresh"), "Tokens not rotated");
        Check(f.Store.Active()!.Raw.SequenceEqual(refreshed[0].Raw), "Active credentials not updated");
        var saved = JsonNode.Parse(refreshed[0].Raw)!;
        Check(saved["extra"]!.GetValue<string>() == "preserve-me" && saved["tokens"]!["id_token"]!.GetValue<string>() == "new-id", "Fields lost");
        Check(refreshed[0].LastRefresh > DateTime.UtcNow.AddMinutes(-1), "last_refresh not updated");
        await refresher.EnsureAsync(refreshed[0]);
        Check(handler.Calls == 1, "Healthy token refreshed unnecessarily");
        var inactive = f.Save("two", "two", 3);
        var activeBytes = File.ReadAllBytes(f.Store.ActivePath);
        using var optionalHandler = new Handler(_ => Task.FromResult(Tokens("two", rotate: false)));
        using var optionalHttp = new HttpClient(optionalHandler);
        var optional = await new AuthRefresher(f.Store, optionalHttp).EnsureAsync(inactive);
        Check(optionalHandler.Calls == 1, "Soon-expiring token not refreshed");
        Check(optional.RefreshToken == "old-refresh", "Omitted refresh token erased");
        Check(JsonNode.Parse(optional.Raw)!["tokens"]!["id_token"]!.GetValue<string>() == "old-id", "Omitted ID token erased");
        Check(File.ReadAllBytes(f.Store.ActivePath).SequenceEqual(activeBytes), "Inactive account overwrote active credentials");
    }

    static async Task FailureAndRecovery()
    {
        using var f = new Fixture();
        var auth = f.Save("one", "one", -10);
        using var handler = new Handler(_ => Task.FromResult(Reply(400,
            "{\"error\":{\"code\":\"refresh_token_reused\",\"message\":\"old-refresh secret\"}}")));
        using var http = new HttpClient(handler);
        var refresher = new AuthRefresher(f.Store, http);
        for (int i = 0; i < 2; i++)
        {
            try { await refresher.EnsureAsync(auth); throw new Exception("Failure accepted"); }
            catch (InvalidOperationException e)
            { Check(!e.Message.Contains("old-refresh") && e.Message.Contains("Sign in"), "Unsafe/unclear error"); }
        }
        Check(handler.Calls == 1, "Rejected refresh token retried");
        Check(File.ReadAllBytes(auth.Path).SequenceEqual(auth.Raw), "Failure overwrote credentials");
        auth = f.Save("one", "one", 60, "signed-in-again");
        await refresher.EnsureAsync(auth);
        Check(handler.Calls == 1, "New credentials blocked by failure cache");
        var missing = f.Save("missing", "missing", -10, "");
        var info = await UsageClient.FetchAsync("missing", missing, false, refresher, http);
        Check(info.Error!.Contains("No refresh token") && handler.Calls == 1, "Missing refresh token sent request");
        var near = f.Save("near", "near", 3, "");
        Check((await refresher.EnsureAsync(near)).AccessToken == near.AccessToken, "Valid token without refresh rejected");
        using var invalid = new Handler(_ => Task.FromResult(Reply(200, "{}")));
        using var invalidHttp = new HttpClient(invalid);
        var original = f.Save("invalid", "invalid", -10);
        try { await new AuthRefresher(f.Store, invalidHttp).EnsureAsync(original); throw new Exception("Invalid response accepted"); }
        catch (InvalidDataException) { Check(File.ReadAllBytes(original.Path).SequenceEqual(original.Raw), "Invalid response saved"); }
        using var transient = new Handler(_ => Task.FromResult(Reply(503, "old-refresh secret")));
        using var transientHttp = new HttpClient(transient);
        var retryable = new AuthRefresher(f.Store, transientHttp);
        for (int i = 0; i < 2; i++)
        {
            try { await retryable.EnsureAsync(original); throw new Exception("503 accepted"); }
            catch (InvalidOperationException e) { Check(!e.Message.Contains("secret"), "Response body leaked"); }
        }
        Check(transient.Calls == 2, "Transient error cached as permanent");
    }

    static async Task RaceAndUsageRecovery()
    {
        using var f = new Fixture();
        var auth = f.Save("one", "one", -10, active: true);
        using var switched = new Handler(_ =>
        {
            Store.AtomicWrite(f.Store.ActivePath, Credentials("other", 60));
            return Task.FromResult(Tokens("one"));
        });
        using var switchedHttp = new HttpClient(switched);
        await new AuthRefresher(f.Store, switchedHttp).EnsureAsync(auth);
        Check(f.Store.Active()!.AccountId == "other", "Switch during refresh overwritten");
        auth = f.Save("one", "one", -10, active: true);
        using var raced = new Handler(_ =>
        {
            var changed = JsonNode.Parse(Credentials("one", 60, "codex-refresh"))!;
            changed["last_refresh"] = DateTime.UtcNow.AddSeconds(1).ToString("O");
            Store.AtomicWrite(f.Store.ActivePath, Encoding.UTF8.GetBytes(changed.ToJsonString()));
            return Task.FromResult(Tokens("one"));
        });
        using var racedHttp = new HttpClient(raced);
        var newer = await new AuthRefresher(f.Store, racedHttp).EnsureAsync(auth);
        Check(newer.RefreshToken == "codex-refresh" && f.Store.Active()!.RefreshToken == "codex-refresh", "Newer Codex rotation overwritten");
        auth = f.Save("healthy", "healthy", 60);
        using var refreshHandler = new Handler(_ => Task.FromResult(Tokens("healthy")));
        using var refreshHttp = new HttpClient(refreshHandler);
        int usageRequests = 0;
        using var usageHandler = new Handler(request =>
        {
            usageRequests++;
            if (usageRequests == 1) return Task.FromResult(Reply(401, "{}"));
            Check(request.Headers.GetValues("Authorization").Single() == "Bearer " + new AuthFile(auth.Path).AccessToken, "Old access token retried");
            return Task.FromResult(Reply(200, "{\"rate_limit\":{\"limit_reached\":false,\"primary_window\":{\"used_percent\":12}}}"));
        });
        using var usageHttp = new HttpClient(usageHandler);
        var info = await UsageClient.FetchAsync("healthy", auth, false, new AuthRefresher(f.Store, refreshHttp), usageHttp);
        Check(info.Error == null && info.Left == 88 && usageRequests == 2 && refreshHandler.Calls == 1, "401 did not recover usage");
        using var unauthorized = new Handler(_ => Task.FromResult(Reply(401, "{}")));
        using var unauthorizedHttp = new HttpClient(unauthorized);
        info = await UsageClient.FetchAsync("healthy", new AuthFile(auth.Path), false,
            new AuthRefresher(f.Store, refreshHttp), unauthorizedHttp);
        Check(info.Error!.Contains("HTTP 401") && unauthorized.Calls == 2, "401 recovery loop unbounded");
        var mismatch = f.Save("mismatch", "mismatch", -10);
        using var mismatchHandler = new Handler(_ => Task.FromResult(Tokens("wrong")));
        using var mismatchHttp = new HttpClient(mismatchHandler);
        try { await new AuthRefresher(f.Store, mismatchHttp).EnsureAsync(mismatch); throw new Exception("Wrong account accepted"); }
        catch (InvalidDataException) { Check(File.ReadAllBytes(mismatch.Path).SequenceEqual(mismatch.Raw), "Wrong account tokens saved"); }
    }

    public static async Task Run()
    {
        StoreCompatibility();
        await RotationAndConcurrency();
        await FailureAndRecovery();
        await RaceAndUsageRecovery();
        Console.WriteLine($"PASS: {count} token refresh and persistence checks.");
    }

    static void StoreCompatibility()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ctxdeck-store-test-" + Guid.NewGuid());
        var current = Path.Combine(parent, "CtxDeck");
        var legacy = Path.Combine(parent, "cdxSwapper");
        try
        {
            Check(Store.ResolveRoot(parent) == current, "Fresh install must use CtxDeck store");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"existing\":true}");
            Check(Store.ResolveRoot(parent) == legacy, "Rename must reuse legacy store");
            Check(File.Exists(Path.Combine(Store.ResolveRoot(parent), "settings.json")), "Existing settings lost");
            Directory.CreateDirectory(current);
            Check(Store.ResolveRoot(parent) == current, "Explicit CtxDeck store must take precedence");
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
    }
}
