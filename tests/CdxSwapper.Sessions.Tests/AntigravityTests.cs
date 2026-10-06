using System.Text;
using System.Text.Json;
using CdxSwapper.Sessions;

static class AntigravityTests
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    public static void Fixture(string root)
    {
        foreach (var name in new[] { "antigravity-ide", "antigravity" })
        {
            var home = Path.Combine(root, name);
            var path = Path.Combine(home, "brain", "shared-id", ".system_generated", "logs", "transcript.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var rows = new object[] {
                new { type="USER_INPUT", source="USER_EXPLICIT", created_at="2026-10-05T01:00:00Z", content="Gemini Ω <script>window.pwned=true</script>" },
                new { type="PLANNER_RESPONSE", source="MODEL", created_at="2026-10-05T01:01:00Z", content="Antigravity answer", thinking="Reasoning history" },
                new { type="RUN_COMMAND", source="MODEL", content="DO NOT EXECUTE: tool result" },
                new { type="PLANNER_RESPONSE", source="MODEL", tool_calls=new[] { new { name="tool", args="historical" } } },
                new { type="SYSTEM_MESSAGE", source="SYSTEM", content="private system settings" }
            };
            File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)), new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(home,"conversations"));
            File.WriteAllBytes(Path.Combine(home,"conversations","legacy.pb"),new byte[] { 0xe4,0x0d,0x66,0x24 });
        }
        var dbPath = Path.Combine(root,"antigravity","conversations","native-id.db");
        File.WriteAllBytes(dbPath,Array.Empty<byte>());
        using var db = HermesStore.Open(dbPath,write:true); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE trajectory_metadata_blob(id TEXT PRIMARY KEY,data BLOB);
            CREATE TABLE steps(idx INTEGER PRIMARY KEY,step_type INTEGER,status INTEGER,metadata BLOB,step_payload BLOB,step_format INTEGER);
            INSERT INTO trajectory_metadata_blob VALUES('main',X'');
            """; cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO steps VALUES($idx,$type,3,X'',$payload,0)";
        var type = cmd.Parameters.AddWithValue("$type",15);
        var index = cmd.Parameters.AddWithValue("$idx",0); var payload = cmd.Parameters.AddWithValue("$payload",Array.Empty<byte>());
        for (int i=0;i<65;i++)
        {
            index.Value=i; type.Value=i==0?14:15;
            var text=TextField(1,"Native Gemini entry " + i);
            payload.Value=i==0?new byte[] { 26,(byte)text.Length }.Concat(text).ToArray():text;
            cmd.ExecuteNonQuery();
        }
    }
    static byte[] TextField(int field, string text)
    {
        var bytes=Encoding.UTF8.GetBytes(text);
        return new[] { (byte)(field*8+2), (byte)bytes.Length }.Concat(bytes).ToArray();
    }
    public static async Task Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"cdx-antigravity-"+Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var gemini=Path.Combine(root,"gemini"); Fixture(gemini);
            var codex=Path.Combine(root,"codex"); var claude=Path.Combine(root,"claude");
            var catalog=new SessionCatalog(codex,claude,antigravityRoot:gemini);
            var scan=catalog.Scan(); Check(scan.Errors.Count==0,"Antigravity scan errors");
            Check(scan.Sessions.Count==5,"Both Antigravity stores plus encrypted cards and native DB");
            Check(scan.Sessions.Select(s=>s.Key).Distinct().Count()==5,"Migrated IDs remain distinct by application");
            var source=scan.Sessions.Single(s=>s.Id=="shared-id"&&s.Source=="ide");
            Check(catalog.Detail(source.Key).Messages.Count==4,"Native transcript roles");
            Check(catalog.Detail(source.Key).Messages.Any(m=>m.Text.Contains("Reasoning history")),"Thinking retained");
            Check(!SessionSource.Records(source).Any(r=>r.Message.Text.Contains("private system")),"System settings excluded");
            var native=scan.Sessions.Single(s=>s.Id=="native-id"); var page=catalog.Detail(native.Key);
            Check(page.Messages.Count==50&&page.Before==15,"SQLite step pagination");
            Check(catalog.Detail(native.Key,page.Before).Messages.Count==15,"SQLite older page");
            Check(catalog.Match(native.Key,0)?.Text=="Native Gemini entry 0","SQLite exact search step");
            using var search=new SessionSearch(Path.Combine(root,"search.sqlite"));
            search.Sync(scan.Sessions);
            for(int i=0;i<200&&search.Status().Busy;i++) await Task.Delay(20);
            Check(search.Status().Errors.Count==0,"Antigravity indexing");
            Check(search.Search("GEMINI Ω").Matches.Count==2,"Both applications searchable");
            var match=search.Search("Native Gemini entry 0").Matches[native.Key];
            Check(catalog.Match(native.Key,match.Hits[0].Offset)?.Text.Contains("entry 0")==true,"Search hit opens native step");
            var converter=new SessionConverter(codex,claude,Path.Combine(root,"conversions"));
            var original=File.ReadAllBytes(source.Path);
            var converted=converter.Convert(source,"codex");
            Check(converted.Messages==6,"Antigravity tool calls and orphan results converted as complete pairs");
            Check(original.SequenceEqual(File.ReadAllBytes(source.Path)),"Antigravity source untouched");
            Check(catalog.Scan().Sessions.Any(s=>s.Id==converted.Id),"Imported Codex journal indexed");
            Check(converter.Convert(source,"codex").Reused,"Repeated conversion reused");
            var toolPath=Path.Combine(root,"tools.jsonl");
            File.WriteAllLines(toolPath,new object[] {
                new { type="PLANNER_RESPONSE",content="Checking",tool_calls=new object[] {
                    new { name="view_file",args=new { AbsolutePath="\"sample.txt\"", StartLine="12" } },
                    new { name="list_dir",args=new { DirectoryPath="\"folder\"", StartLine="0" } } } },
                new { type="LIST_DIRECTORY",content="Directory result" },
                new { type="VIEW_FILE",content="File result" }
            }.Select(r=>JsonSerializer.Serialize(r)));
            var toolItem=new SessionItem { Provider="antigravity",Path=toolPath,Id="tools",Cwd=root };
            var records=AntigravityStore.Records(toolItem).Select(r=>r.Message).ToList();
            Check(records.Count==5 && records[0].Text=="Checking","Tool envelopes excluded from assistant prose");
            Check(records[3].CallId==records[2].CallId && records[4].CallId==records[1].CallId,"Parallel Antigravity results matched by tool name");
            using(var args=JsonDocument.Parse(records[1].ToolArguments))
                Check(args.RootElement.Str("AbsolutePath")=="sample.txt" && args.RootElement.Get("StartLine").GetInt32()==12,"Encoded argument values decoded");
            var imported=converter.Convert(toolItem,"claude");
            var blocks=Journal.Rows(imported.Path).Select(r=>r.Row.Get("message").Get("content")).Where(c=>c.ValueKind==JsonValueKind.Array).SelectMany(c=>c.EnumerateArray()).ToList();
            Check(blocks.Count(b=>b.Str("type")=="tool_use")==2 && blocks.Count(b=>b.Str("type")=="tool_result")==2,"Antigravity to Claude native pairs");
            Check(!File.ReadAllText(imported.Path).Contains("[Tool calls]") && !File.ReadAllText(imported.Path).Contains("historical_context"),"No tool calls as prose or generic placeholder");
            var legacy=scan.Sessions.First(s=>s.MetadataOnly);
            Check(catalog.Detail(legacy.Key).Messages.Count==0,"Encrypted PB not parsed as text");
            try { converter.Convert(legacy,"codex"); throw new Exception("Encrypted conversion accepted"); }
            catch(InvalidOperationException) { checks++; }
            var stamp=SessionSource.Stamp(source);
            File.AppendAllText(source.Path,"\n"+JsonSerializer.Serialize(new { type="USER_INPUT",content="updated history" })+"\n");
            Check(SessionSource.Stamp(source)!=stamp,"Transcript changes invalidate index");
            var export=SessionActions.ExportTranscript(source,root);
            Check(File.ReadAllText(export).Contains("updated history"),"Full history export");
            var corrupt=Path.Combine(gemini,"antigravity","conversations","broken.db");
            File.WriteAllText(corrupt,"corrupt database");
            var after=catalog.Scan();
            Check(after.Errors.Count==1&&after.Sessions.Any(s=>s.Id=="native-id")&&after.Sessions.Any(s=>s.Id=="shared-id"&&s.Source=="app"),"A damaged database does not hide other conversations");
            Console.WriteLine($"PASS: {checks} Antigravity checks (IDE/app, SQLite/protobuf, encrypted legacy, search, paging, conversion, source preservation).");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}
