using System.Text.Json;

namespace CdxSwapper.Sessions;

sealed class SessionApi : IDisposable
{
    readonly SessionCatalog catalog;
    readonly SessionSearch index;
    readonly SessionConverter converter;
    readonly string hermesHome;
    readonly string dataRoot;
    public SessionApi(string codexHome, string claudeHome, string dataRoot, string? hermesHome = null, string? antigravityRoot = null)
    {
        this.hermesHome = hermesHome ?? HermesStore.DefaultHome;
        this.dataRoot = dataRoot;
        catalog = new(codexHome, claudeHome, hermesHome: this.hermesHome, antigravityRoot: antigravityRoot ?? AntigravityStore.DefaultRoot);
        index = new(Path.Combine(dataRoot, "search.sqlite"));
        converter = new(codexHome, claudeHome, Path.Combine(dataRoot, "conversions"), this.hermesHome);
    }
    public async Task<object> RequestAsync(string method, Uri url, string body)
    {
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]),
                p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
        string Get(string name) => query.TryGetValue(name, out var value) ? value : "";
        if (method == "GET")
        {
            switch (url.AbsolutePath)
            {
                case "/api/sessions":
                    var scan = await Task.Run(catalog.Scan); index.Sync(scan.Sessions);
                    return new { sessions = scan.Sessions, errors = scan.Errors, sources = scan.Sources, index = index.Status() };
                case "/api/search": return await Task.Run(() => index.Search(Get("q")));
                case "/api/index": return new { index = index.Status() };
                case "/api/session":
                    long? before = Get("before") == "" ? null : long.Parse(Get("before"));
                    return await Task.Run(() => catalog.Detail(Get("key"), before));
                case "/api/match": return new { message = await Task.Run(() => catalog.Match(Get("key"), long.Parse(Get("offset")))) };
            }
        }
        if(method=="POST" && url.AbsolutePath=="/api/delete-batch")
        {
            if(body.Length is 0 or > 131072)throw new ArgumentException("Invalid request size.");
            using var batch=JsonDocument.Parse(body);
            if(!batch.RootElement.Bool("confirmed"))throw new ArgumentException("Deletion requires confirmation.");
            var keys=batch.RootElement.Get("keys");
            if(keys.ValueKind!=JsonValueKind.Array || keys.GetArrayLength() is 0 or > 2000)throw new ArgumentException("Select 1–2000 sessions.");
            var unique=keys.EnumerateArray().Select(k=>k.GetString()??throw new ArgumentException("Invalid key.")).Distinct().ToList();
            var results=new List<object>();
            foreach(var key in unique)
                try { var item=catalog.Get(key);var backup=await Task.Run(()=>Delete(item));results.Add(new {key,deleted=true,backup,error=""}); }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
                { results.Add(new {key,deleted=false,backup="",error=e.Message}); }
            var scan=catalog.Scan();index.Sync(scan.Sessions);
            return new { results };
        }
        if (method == "POST" && url.AbsolutePath == "/api/action")
        {
            if (body.Length is 0 or > 4096) throw new ArgumentException("Invalid request size.");
            using var doc = JsonDocument.Parse(body);
            var item = catalog.Get(doc.RootElement.Str("key")); var action = doc.RootElement.Str("action");
            if(action=="delete")
            {
                if(!doc.RootElement.Bool("confirmed"))throw new ArgumentException("Deletion requires confirmation.");
                var backup=await Task.Run(()=>Delete(item));
                var scan=catalog.Scan();index.Sync(scan.Sessions);
                return new { ok=true,deleted=true,backup };
            }
            if (action is "continue-codex" or "continue-claude" or "continue-hermes" or "continue-antigravity" or "continue-antigravity-ide")
            {
                var target = action switch { "continue-antigravity"=>"antigravity-app", "continue-antigravity-ide"=>"antigravity",_=>action[9..] };
                var conversion = await Task.Run(() => converter.Convert(item, target));
                if(target is "antigravity" or "antigravity-app")
                {
                    await AntigravityIde.OpenAsync(conversion.Id,conversion.Path,true,SessionJson.Cut($"From {item.Provider} · {item.Title}",180),useIde:target=="antigravity");
                    return new { ok=true,conversion };
                }
                var converted = new SessionItem { Provider = target, Id = conversion.Id, Cwd = conversion.Cwd, Path = conversion.Path,
                    ProviderHome = target == "hermes" ? this.hermesHome : "" };
                SessionActions.Perform(converted, "resume", catalog.CodexHome, catalog.ClaudeHome, this.hermesHome);
                if (target == "hermes") await DesktopClients.VerifyHermesAsync(converted);
                if (target == "claude") await DesktopClients.VerifyClaudeAsync(converted);
                return new { ok = true, conversion };
            }
            if(item.Provider=="antigravity" && action is "resume" or "open-client")
                await AntigravityIde.OpenAsync(item.Id,item.Path,false,item.Title,useIde:item.Source=="ide");
            else SessionActions.Perform(item, action, catalog.CodexHome, catalog.ClaudeHome, this.hermesHome, dataRoot);
            if (item.Provider == "hermes" && action == "resume") await DesktopClients.VerifyHermesAsync(item);
            if (item.Provider == "claude" && action == "resume") await DesktopClients.VerifyClaudeAsync(item);
            return new { ok = true };
        }
        throw new KeyNotFoundException("Unknown API route.");
    }
    string Delete(SessionItem item)
    {
        if(item.Provider=="claude" && System.Diagnostics.Process.GetProcessesByName("Claude").Any(p=>
            { using(p)return p.MainWindowHandle!=IntPtr.Zero; }))
            throw new InvalidOperationException(SessionText.Pick("Quit Claude Desktop before deleting sessions.",
                "Полностью закройте Claude Desktop перед удалением сессий.","Cierra Claude Desktop antes de eliminar sesiones."));
        return SessionDeletion.Delete(item,dataRoot,catalog.CodexHome);
    }
    public void Dispose() => index.Dispose();
}
