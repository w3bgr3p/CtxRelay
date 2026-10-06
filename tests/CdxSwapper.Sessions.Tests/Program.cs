using System.Text;
using System.Text.Json;
using CdxSwapper;
using CdxSwapper.Sessions;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var culture = args.FirstOrDefault(a => a.StartsWith("--culture="));
        if (culture != null) System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture[10..]);
        Localization.Initialize();
        if(SessionConverter.ClaudeWorkingDirectory(@"\\?\W:\code_hard\.net\cdxSwapper")!=@"W:\code_hard\.net\cdxSwapper" ||
            SessionConverter.ClaudeWorkingDirectory(@"W:\normal")!=@"W:\normal" ||
            SessionConverter.ClaudeWorkingDirectory(@"\\server\share")!=@"\\server\share")throw new Exception("Claude working directory normalization regression");
        if (args.Contains("--ui")) return UiSmoke.Run();
        if(args.Contains("--native-antigravity")) { NativeAntigravity.Run().GetAwaiter().GetResult();return 0; }
        if(args.Contains("--native-antigravity-app")) { NativeAntigravity.Run(false).GetAwaiter().GetResult();return 0; }
        if(args.FirstOrDefault(a=>a.StartsWith("--check-antigravity-window=")) is { } checkWindow)
        { AntigravityWindowCheck.Run(checkWindow.Split('=',2)[1]);return 0; }
        if(args.FirstOrDefault(a=>a.StartsWith("--open-antigravity-window=")) is { } appWindow)
        { AntigravityAppWindow.OpenAsync(appWindow.Split('=',2)[1],"").GetAwaiter().GetResult();return 0; }
        if(args.FirstOrDefault(a=>a.StartsWith("--api-convert-ide=")) is { } ide)
        {
            var store=new Store();var claude=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
            using var api=new SessionApi(store.CodexHome,claude,Path.Combine(store.Root,".sessions"));
            api.RequestAsync("GET",new Uri("https://cdx.local/api/sessions"),"").GetAwaiter().GetResult();
            var item=new SessionCatalog(store.CodexHome,claude).Scan().Sessions.Single(s=>s.Id==ide[18..]);
            var result=api.RequestAsync("POST",new Uri("https://cdx.local/api/action"),JsonSerializer.Serialize(new {key=item.Key,action="continue-antigravity-ide"})).GetAwaiter().GetResult();
            Console.WriteLine("Actual IDE conversion API: "+JsonSerializer.Serialize(result));return 0;
        }
        if(args.FirstOrDefault(a=>a.StartsWith("--convert-claude=")) is { } convertClaude)
        {
            var store=new Store(); var home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
            var source=new SessionCatalog(store.CodexHome,home,antigravityRoot:AntigravityStore.DefaultRoot).Scan().Sessions.Single(s=>s.Id==convertClaude[17..]);
            var result=new SessionConverter(store.CodexHome,home,Path.Combine(store.Root,".sessions","conversions")).Convert(source,"claude");
            var blocks=Journal.Rows(result.Path).Select(r=>r.Row.Get("message").Get("content")).Where(c=>c.ValueKind==JsonValueKind.Array).SelectMany(c=>c.EnumerateArray()).ToList();
            var calls=blocks.Where(b=>b.Str("type")=="tool_use").ToList();
            var results=blocks.Where(b=>b.Str("type")=="tool_result").ToList();
            if(calls.Count==0 || calls.Any(c=>!results.Any(r=>r.Str("tool_use_id")==c.Str("id"))))throw new Exception("Live Claude conversion has missing native tool results");
            Console.WriteLine($"Live import {result.Id}: {calls.Count} native calls, {results.Count} results; all IDs paired.");
            var item=new SessionItem { Provider="claude",Id=result.Id,CliId=result.Id,Path=result.Path,Cwd=result.Cwd };
            SessionActions.Perform(item,"resume",store.CodexHome,home);
            DesktopClients.VerifyClaudeAsync(item).GetAwaiter().GetResult();return 0;
        }
        if(args.FirstOrDefault(a=>a.StartsWith("--api-open=")) is { } apiOpen)
        {
            var store=new Store();var claude=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
            using var api=new SessionApi(store.CodexHome,claude,Path.Combine(store.Root,".sessions"));
            api.RequestAsync("GET",new Uri("https://cdx.local/api/sessions"),"").GetAwaiter().GetResult();
            var item=new SessionCatalog(store.CodexHome,claude).Scan().Sessions.Single(s=>s.Id==apiOpen[11..]);
            var result=api.RequestAsync("POST",new Uri("https://cdx.local/api/action"),JsonSerializer.Serialize(new{key=item.Key,action="resume"})).GetAwaiter().GetResult();
            Console.WriteLine("Verified actual resume API: "+JsonSerializer.Serialize(result));return 0;
        }
        if (args.Contains("--native")) { NativeResume.Run().GetAwaiter().GetResult(); return 0; }
        if(args.FirstOrDefault(a=>a.StartsWith("--repair-conversion=")) is { } repair)
        { NativeResume.Run(repair[20..]).GetAwaiter().GetResult();return 0; }
        if(args.FirstOrDefault(a=>a.StartsWith("--open-session=")||a.StartsWith("--open-claude-cli=")) is { } open)
        {
            var store=new Store();var claude=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
            var cli=open.StartsWith("--open-claude-cli=");var id=open[(cli?18:15)..];
            var item=new SessionCatalog(store.CodexHome,claude,hermesHome:HermesStore.DefaultHome).Scan().Sessions.Single(s=>s.Id==id||(cli&&s.CliId==id));
            if(cli)item=item with{Id=id,CliId=id,DesktopPath=""};
            SessionActions.Perform(item,"resume",store.CodexHome,claude);
            if(item.Provider=="hermes")DesktopClients.VerifyHermesAsync(item).GetAwaiter().GetResult();
            if(item.Provider=="claude")DesktopClients.VerifyClaudeAsync(item).GetAwaiter().GetResult();
            Console.WriteLine((item.Provider is "hermes" or "claude"?"Opened verified session: ":"Dispatched desktop session: ")+item.Id);return 0;
        }
        if (args.Contains("--native-hermes")) { NativeHermes.Run().GetAwaiter().GetResult(); return 0; }
        if (args.Contains("--repair-hermes")) { RepairHermes.Run();return 0; }
        if (args.Contains("--repair-hermes-display")) { RepairHermes.Display();return 0; }
        if (args.Contains("--hermes-live"))
        {
            var items=HermesStore.Homes(HermesStore.DefaultHome).SelectMany(HermesStore.Scan).ToList();
            var messages=items.Sum(i=>HermesStore.Records(i).Count());
            Console.WriteLine($"READ-ONLY live Hermes: {items.Count} sessions, {messages} active messages, {items.Select(i=>i.Profile).Distinct().Count()} profiles.");
            var runtimeIndex=Path.Combine(new Store().Root,".sessions","search.sqlite");
            if(File.Exists(runtimeIndex))
            {
                using var db=HermesStore.Open(runtimeIndex);using var cmd=db.CreateCommand();
                cmd.CommandText="SELECT count(*) FROM files WHERE key=$key";var key=cmd.Parameters.AddWithValue("$key","");
                int indexed=0;foreach(var item in items){key.Value=item.Key;indexed+=(int)(long)cmd.ExecuteScalar()!;}
                Console.WriteLine($"Published app search cache: {indexed}/{items.Count} Hermes sessions indexed.");
            }
            return 0;
        }
        if (args.Contains("--antigravity-live"))
        {
            var catalog = new SessionCatalog(Path.Combine(Path.GetTempPath(),"no-codex"), Path.Combine(Path.GetTempPath(),"no-claude"),
                antigravityRoot: AntigravityStore.DefaultRoot);
            var scan = catalog.Scan();
            Console.WriteLine($"READ-ONLY Antigravity: {scan.Sessions.Count} sessions, {scan.Sessions.Count(s => s.MetadataOnly)} encrypted legacy cards.");
            foreach (var group in scan.Sessions.GroupBy(s => s.Client))
                Console.WriteLine($"{group.Key}: {group.Count()} sessions, {group.Sum(s => SessionSource.Records(s).Count())} readable messages, {group.Count(s => s.Cwd.Length > 0)} project paths.");
            foreach (var error in scan.Errors) Console.WriteLine("ERROR: " + error);
            return scan.Errors.Count > 0 ? 1 : 0;
        }
        if (typeof(Store).Assembly.GetType("CdxSwapper.Sessions.SessionCatalog") is null)
            throw new Exception("Session integration missing: no C# SessionCatalog.");
        SessionTests.Run().GetAwaiter().GetResult();
        ClaudeToolsTests.Run();
        var claudeDesktop = new SessionItem { Provider = "claude", Id = "local_96eb40e2-7d9d-42a3-9b69-06c011bcc798", DesktopPath = "metadata.json" };
        if (DesktopClients.ClaudeUrl(claudeDesktop) != "claude://claude.ai/epitaxy/local_96eb40e2-7d9d-42a3-9b69-06c011bcc798" ||
            DesktopClients.ClaudeUrl(claudeDesktop with { DesktopPath = "", CliId = "85220af2-4fff-42d0-8a62-6ebc84f49230" }) != "claude://resume?session=85220af2-4fff-42d0-8a62-6ebc84f49230")
            throw new Exception("Claude Desktop routes must distinguish an existing card from CLI import.");
        Console.WriteLine("PASS: Claude Desktop card and native CLI import URLs.");
        HermesTests.Run().GetAwaiter().GetResult();
        HermesToolsTests.Run();
        var desktopCodex = new SessionItem { Provider = "codex", Id = "01a10cad-f82e-7b03-92d9-440043ee32ff", HistoryMode = "paginated" };
        if (!SessionActions.UsesCodexDesktop(desktopCodex) ||
            !SessionActions.UsesCodexDesktop(desktopCodex with { HistoryMode = "legacy" }) ||
            SessionActions.CodexDesktopUrl(desktopCodex) != "codex://threads/01a10cad-f82e-7b03-92d9-440043ee32ff")
            throw new Exception("Codex paginated Desktop resume routing failed.");
        Console.WriteLine("PASS: All Codex sessions, including imported legacy history, open in the desktop application.");
        var desktopCommand = SessionActions.ResumeCommand(desktopCodex);
        if (desktopCommand.Contains("codex resume") || !desktopCommand.Contains("codex://threads/")) throw new Exception("Desktop launch used CLI resume");
        Console.WriteLine("PASS: Desktop launch command=" + desktopCommand);
        AntigravityTests.Run().GetAwaiter().GetResult();
        DeletionTests.Run().GetAwaiter().GetResult();
        return 0;
    }
}

static class SessionTests
{
    static int checks;
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
    static void Write(string path, IEnumerable<object> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", rows.Select(r => JsonSerializer.Serialize(r))) + "\n{incomplete", new UTF8Encoding(false));
    }
    static object Meta(string id, string cwd) => new { type = "session_meta", payload = new { id, cwd, timestamp = "2026-10-05T00:00:00Z" } };
    static object Message(string text) => new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } } };
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdx-sessions-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var codex = Path.Combine(root, "codex"); var claude = Path.Combine(root, "claude");
            var path = Path.Combine(codex, "archived_sessions", "source.jsonl");
            Write(path, new object[] { Meta("cx", root), Message("Вопрос Ω <script>danger</script>"),
                new { type = "turn_context", payload = new { model = "test-model" } } });
            var cp = Path.Combine(claude, "projects", "project", "cli.jsonl");
            Write(cp, new object[] {
                new { type = "user", sessionId = "cli", cwd = root, message = new { content = "Claude question" } },
                new { type = "assistant", sessionId = "cli", message = new { model = "test-claude", content = new object[] {
                    new { type = "tool_use", name = "Bash", input = new { command = "DO NOT EXECUTE" } },
                    new { type = "text", text = "Claude answer" } } } } });
            var desktop = Path.Combine(root, "desktop"); Directory.CreateDirectory(desktop);
            File.WriteAllText(Path.Combine(desktop, "local_card.json"), JsonSerializer.Serialize(new {
                sessionId = "local_card", cliSessionId = "cli", title = "Desktop title", cwd = root, createdAt = 1700000000000L }));
            File.WriteAllText(Path.Combine(desktop, "local_missing.json"), JsonSerializer.Serialize(new {
                sessionId = "local_missing", cliSessionId = "missing", title = "Only card", cwd = root,
                postTurnSummary = new { status_detail = "Сводка без журнала" } }));
            var catalog = new SessionCatalog(codex, claude, new[] { desktop });
            var scan = catalog.Scan();
            Check(scan.Errors.Count == 0 && scan.Sessions.Count == 3, "Two providers and unmatched Desktop card");
            var cx = scan.Sessions.Single(s => s.Id == "cx");
            Check(cx.Archived && cx.Model == "test-model", "Codex archive/model");
            Check(catalog.Detail(cx.Key).Messages.Single().Text.Contains("Ω"), "UTF-8 / malformed rows");
            var cl = scan.Sessions.Single(s => s.Id == "local_card");
            Check(cl.CliId == "cli" && cl.Path == cp && cl.Title == "Desktop title", "Desktop links retain CLI path/ID");
            Check(catalog.Detail(cl.Key).Messages.Count == 3 && catalog.Detail(cl.Key).Messages.Count(m=>m.Role=="tool")==1, "Claude prose and tools are separate");
            Check(catalog.Detail(scan.Sessions.Single(s => s.MetadataOnly).Key).Messages.Single().Text.Contains("Сводка"), "Desktop summary");
            using (var state = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
                DataSource = Path.Combine(codex, "state_5.sqlite"), Pooling = false }.ToString()))
            {
                state.Open(); using var command = state.CreateCommand();
                command.CommandText = "CREATE TABLE threads(id TEXT,title TEXT,cwd TEXT,model TEXT,archived INTEGER,tokens_used INTEGER,agent_role TEXT); " +
                    "INSERT INTO threads VALUES('cx','Database title',$cwd,'database-model',1,927,NULL)";
                command.Parameters.AddWithValue("$cwd", root); command.ExecuteNonQuery();
            }
            var dbBytes = File.ReadAllBytes(Path.Combine(codex, "state_5.sqlite"));
            cx = catalog.Scan().Sessions.Single(s => s.Id == "cx");
            Check(cx.Title == "Database title" && cx.Tokens == 927 && cx.Model == "database-model", "Codex state enrichment");
            Check(File.ReadAllBytes(Path.Combine(codex, "state_5.sqlite")).SequenceEqual(dbBytes), "State database remains unchanged");
            var paging = Path.Combine(codex, "sessions", "paging.jsonl");
            Write(paging, new object[] { Meta("paged", root) }.Concat(Enumerable.Range(0, 80).Select(i => Message(i + ":" + new string('я', 20000)))));
            var paged = catalog.Scan().Sessions.Single(s => s.Id == "paged");
            var seen = new List<int>(); long? cursor = null;
            do
            {
                var page = catalog.Detail(paged.Key, cursor);
                seen.InsertRange(0, page.Messages.Select(m => int.Parse(m.Text.Split(':')[0])));
                cursor = page.Before;
            } while (cursor != null);
            Check(seen.SequenceEqual(Enumerable.Range(0, 80)), "Pagination lost/duplicated UTF-8 records");
            var huge = Path.Combine(codex, "sessions", "huge.jsonl");
            Write(huge, new object[] { Meta("huge", root), Message(new string('x', 900000)) });
            var hugeItem = catalog.Scan().Sessions.Single(s => s.Id == "huge");
            Check(catalog.Detail(hugeItem.Key).Messages.Single() is { Truncated: true, Text.Length: 60000 }, "Huge line paging/truncation");
            var indexPath = Path.Combine(root, "search.sqlite");
            using (var index = new SessionSearch(indexPath))
            {
                index.Sync(catalog.Scan().Sessions); await index.WaitAsync();
                var match = index.Search("ВОПРОС Ω").Matches[cx.Key];
                Check(match.Count == 1 && catalog.Match(cx.Key, match.Hits.Single().Offset)!.Text.Contains("Вопрос Ω"), "Search Cyrillic byte offset");
                Check(index.Search("СВОДКА").Matches.Count == 1, "Desktop indexed");
                Check(index.Search("<script>").Matches.Count == 1, "Literal search");
                Write(path, new object[] { Meta("cx", root), Message("replacement") });
                File.Delete(huge);
                index.Sync(catalog.Scan().Sessions); await index.WaitAsync();
                Check(index.Search("ВОПРОС Ω").Matches.Count == 0 && index.Search("replacement").Matches.Count == 1, "Changed source reindexing");
                Check(index.Search(new string('x', 20)).Matches.Count == 0, "Deleted source search cleanup");
            }
            using (var index = new SessionSearch(indexPath)) Check(index.Search("replacement").Matches.Count == 1, "Persistent search cache");
            var converter = new SessionConverter(codex, claude, Path.Combine(root, "converted"));
            var bytes = File.ReadAllBytes(cp);
            var result = converter.Convert(cl, "codex");
            Check(result.Messages == 3 && result.Id != cl.Id && File.ReadAllBytes(cp).SequenceEqual(bytes), "Conversion preserves source");
            Check(converter.Convert(cl, "codex") is { Reused: true } reuse && reuse.Id == result.Id, "Conversion reused");
            var converted = catalog.Scan().Sessions.Single(s => s.Id == result.Id);
            Check(catalog.Detail(converted.Key).Messages.Any(m => m.Text.Contains("Claude answer")), "Codex import readable");
            var reverse = converter.Convert(converted, "claude");
            var rows = Journal.Rows(reverse.Path).Select(r => r.Row).Where(r => r.Str("type") is "user" or "assistant").ToList();
            Check(rows[0].Get("parentUuid").ValueKind == JsonValueKind.Null, "Claude root chain");
            for (int i = 1; i < rows.Count; i++) Check(rows[i].Str("parentUuid") == rows[i - 1].Str("uuid"), "Claude parent chain");
            Check(File.ReadAllText(reverse.Path).Contains("DO NOT EXECUTE") &&
                rows.Any(r => r.Get("message").Get("content").ValueKind == JsonValueKind.Array &&
                    r.Get("message").Get("content").EnumerateArray().Any(b => b.Str("type") == "tool_use")), "Historic tools retain native Claude blocks");
            try { converter.Convert(scan.Sessions.Single(s => s.MetadataOnly), "codex"); throw new Exception("Metadata conversion accepted"); }
            catch (InvalidOperationException) { checks++; }
            var empty = Path.Combine(codex, "sessions", "empty.jsonl"); Write(empty, new object[] { Meta("empty", root) });
            var emptyItem = catalog.Scan().Sessions.Single(s => s.Id == "empty");
            var countBefore = SessionCatalog.Files(Path.Combine(claude, "projects")).Count();
            try { converter.Convert(emptyItem, "claude"); throw new Exception("Empty conversion accepted"); }
            catch (InvalidOperationException) { checks++; }
            Check(countBefore == SessionCatalog.Files(Path.Combine(claude, "projects")).Count() &&
                !Directory.EnumerateFiles(root, "*.cdx_tmp", SearchOption.AllDirectories).Any(), "Failed conversion leaves no partial journal");
            Check(Journal.Message("codex", JsonSerializer.SerializeToElement(new { type = "session_meta" })) == null,
                "Structurally incomplete metadata skipped");
            Console.WriteLine($"PASS: {checks} session checks (parsers, Desktop, paging, persistent search, conversion).");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
