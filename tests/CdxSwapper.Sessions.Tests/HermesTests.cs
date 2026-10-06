using System.Text.Json;
using CdxSwapper.Sessions;
using Microsoft.Data.Sqlite;

static class HermesTests
{
    static int checks;
    static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); checks++; }
    public static void Fixture(string home)
    {
        Directory.CreateDirectory(home);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(home, "state.db"), Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE sessions(id TEXT PRIMARY KEY,source TEXT NOT NULL,model TEXT,cwd TEXT,title TEXT,
              started_at REAL NOT NULL,ended_at REAL,last_activity_at REAL,parent_session_id TEXT,
              message_count INTEGER DEFAULT 0,tool_call_count INTEGER DEFAULT 0,input_tokens INTEGER DEFAULT 0,
              output_tokens INTEGER DEFAULT 0,archived INTEGER DEFAULT 0);
            CREATE TABLE messages(id INTEGER PRIMARY KEY AUTOINCREMENT,session_id TEXT NOT NULL,role TEXT NOT NULL,
              content TEXT,tool_calls TEXT,tool_name TEXT,tool_call_id TEXT,timestamp REAL NOT NULL,active INTEGER DEFAULT 1);
            CREATE INDEX messages_session ON messages(session_id,id);
            INSERT INTO sessions(id,source,model,cwd,title,started_at,input_tokens,output_tokens)
              VALUES('hermes-one','desktop','hermes-model',$cwd,'Hermes title',1700000000,10,20);
            INSERT INTO messages(session_id,role,content,timestamp) VALUES('hermes-one','user','Первый Hermes Ω',1700000001);
            INSERT INTO messages(session_id,role,content,tool_calls,timestamp)
              VALUES('hermes-one','assistant','Ответ Hermes','[{"function":{"name":"Bash","arguments":"DO NOT EXECUTE"}}]',1700000002);
            INSERT INTO messages(session_id,role,content,timestamp,active) VALUES('hermes-one','user','rewound-marker',1700000003,0);
            """;
        cmd.Parameters.AddWithValue("$cwd", home); cmd.ExecuteNonQuery();
    }
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdx-hermes-test-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var home = Path.Combine(root, "hermes"); Fixture(home);
        var previous = Environment.GetEnvironmentVariable("HERMES_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", home);
            var codex = Path.Combine(root, "codex"); var claude = Path.Combine(root, "claude");
            var profile = Path.Combine(home, "profiles", "coder"); Fixture(profile);
            var catalog = new SessionCatalog(codex, claude, hermesHome: home);
            var scan = catalog.Scan();
            Check(scan.Sessions.Any(s => s.Provider == "hermes"), "Hermes sessions missing from catalog");
            Check(scan.Errors.Count == 0 && scan.Sessions.Count == 2 && scan.Sessions.Select(s => s.Key).Distinct().Count() == 2, "Profiles / identical IDs must remain separate");
            var item = scan.Sessions.Single(s => s.Profile == "default");
            Check(item.Title == "Hermes title" && item.Model == "hermes-model" && item.Tokens == 30, "Hermes metadata");
            Check(catalog.Detail(item.Key).Messages.Count == 3 && catalog.Detail(item.Key).Messages.Last().Role=="tool" && catalog.Detail(item.Key).Messages.Last().ToolName=="Bash" && catalog.Detail(item.Key).Messages.Last().Text.Contains("DO NOT EXECUTE"), "History separates native calls from assistant prose and excludes rewound rows");
            Check(SessionActions.ResumeCommand(scan.Sessions.Single(s => s.Profile == "coder")).Contains("?profile=coder") &&
                SessionActions.ResumeCommand(item).Contains("hermes://cdx-session/hermes-one?profile=default"), "Hermes desktop resume routes correct profile");
            var export = SessionActions.ExportTranscript(item,Path.Combine(root,"transcripts"));
            Check(File.ReadAllText(export).Contains("Первый Hermes Ω") && !File.ReadAllText(export).Contains("rewound-marker"), "Hermes opens complete text export, not binary SQLite");
            using var live = HermesStore.Open(item.Path, write: true);
            using var command = live.CreateCommand();
            command.CommandText = "UPDATE sessions SET archived=1,parent_session_id='compression-parent' WHERE id='hermes-one'";
            command.ExecuteNonQuery();
            var archived = catalog.Scan().Sessions.Single(s => s.Key == item.Key);
            Check(archived.Archived && !archived.Subagent, "Compression lineage is not a subagent");
            command.CommandText = "INSERT INTO messages(session_id,role,content,timestamp) VALUES('hermes-one','tool','[1,2,3]',1700000002)";
            command.ExecuteNonQuery();
            Check(catalog.Detail(item.Key).Messages.Last().Text == "[1,2,3]", "Literal JSON arrays in tool output preserved");
            command.CommandText = "DELETE FROM messages WHERE content='[1,2,3]'";command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO messages(session_id,role,content,timestamp) VALUES('hermes-one','user',$text,1700000004)";
            var parameter = command.Parameters.AddWithValue("$text", "");
            for (int i = 0; i < 105; i++) { parameter.Value = "entry-" + i; command.ExecuteNonQuery(); }
            var ids = new List<string>(); long? before = null;
            do { var page = catalog.Detail(item.Key, before); ids.InsertRange(0, page.Messages.Select(m => m.Text)); before = page.Before; } while (before != null);
            Check(ids.Count == 108 && ids.Skip(3).SequenceEqual(Enumerable.Range(0,105).Select(i => "entry-" + i)), "Hermes message-ID pagination with separate tool records");
            using (var index = new SessionSearch(Path.Combine(root, "search.sqlite")))
            {
                index.Sync(catalog.Scan().Sessions); await index.WaitAsync();
                var match = index.Search("ПЕРВЫЙ HERMES Ω").Matches[item.Key];
                Check(catalog.Match(item.Key, match.Hits.Single().Offset)!.Text == "Первый Hermes Ω", "Hermes content search opens exact message ID");
                Check(index.Search("rewound-marker").Matches.Count == 0, "Inactive row not indexed");
                command.CommandText = "UPDATE messages SET content='Заменён Hermes Ω' WHERE id=1"; command.ExecuteNonQuery();
                index.Sync(catalog.Scan().Sessions); await index.WaitAsync();
                Check(index.Search("ПЕРВЫЙ").Matches.Count == 1 && index.Search("ЗАМЕНЁН").Matches.ContainsKey(item.Key), "WAL same-length update reindexed independently");
                command.CommandText = "UPDATE messages SET active=0 WHERE id=1"; command.ExecuteNonQuery();
                index.Sync(catalog.Scan().Sessions); await index.WaitAsync();
                Check(!index.Search("ЗАМЕНЁН").Matches.ContainsKey(item.Key), "Rewind removes indexed message");
                command.CommandText = "DELETE FROM messages WHERE session_id='hermes-one'; DELETE FROM sessions WHERE id='hermes-one'";
                command.ExecuteNonQuery(); index.Sync(catalog.Scan().Sessions); await index.WaitAsync();
                Check(!index.Search("entry-").Matches.ContainsKey(item.Key), "Deleted Hermes session removed from index");
            }
            var source = scan.Sessions.Single(s => s.Profile == "coder");
            var converter = new SessionConverter(codex, claude, Path.Combine(root, "exports"), home);
            var toCodex = converter.Convert(source, "codex"); var toClaude = converter.Convert(source, "claude");
            Check(toCodex.Messages == 3 && toClaude.Messages == 3, "Hermes prose and native tool call to both clients");
            var cx = catalog.Scan().Sessions.Single(s => s.Id == toCodex.Id); var cl = catalog.Scan().Sessions.Single(s => s.Id == toClaude.Id);
            var fromCodex = converter.Convert(cx, "hermes"); var fromClaude = converter.Convert(cl, "hermes");
            foreach (var imported in new[] { fromCodex, fromClaude })
            {
                var importedItem = catalog.Scan().Sessions.Single(s => s.Provider == "hermes" && s.Id == imported.Id);
                Check(catalog.Detail(importedItem.Key).Messages.Any(m => m.Text.Contains("Ответ Hermes")), "Hermes import history round trip");
                if(imported.Id==fromCodex.Id)Check(SessionSource.Records(importedItem).Any(r=>r.Message.Role=="tool"&&r.Message.ToolName=="Bash"), "Codex to Hermes retains native calls");
            }
            Check(converter.Convert(source,"codex").Id == toCodex.Id && converter.Convert(cx,"hermes").Reused, "Unrelated Hermes sessions do not duplicate conversion");
            var empty = source with { Id = "missing" };
            var count = catalog.Scan().Sessions.Count;
            try { converter.Convert(empty,"hermes"); throw new Exception("Same-provider conversion accepted"); } catch (ArgumentException) { checks++; }
            Check(catalog.Scan().Sessions.Count == count, "Rejected conversion leaves store unchanged");
            var emptyPath=Path.Combine(root,"empty-claude.jsonl");File.WriteAllText(emptyPath,"{\"type\":\"summary\"}\n");
            try { converter.Convert(new SessionItem {Provider="claude",Id="empty",Path=emptyPath,Cwd=root},"hermes");throw new Exception("Empty Hermes import accepted"); }
            catch(InvalidOperationException) { checks++; }
            Check(catalog.Scan().Sessions.Count == count,"Empty Hermes import rolls back its session/messages");
            Console.WriteLine($"PASS: {checks} Hermes checks (profiles, SQLite/WAL, pages, search, resume routing, four-way conversion).");
        }
        finally { Environment.SetEnvironmentVariable("HERMES_HOME", previous); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
