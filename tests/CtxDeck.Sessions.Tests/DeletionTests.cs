using CtxDeck.Sessions;

static class DeletionTests
{
    public static async Task Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"cdx-delete-"+Guid.NewGuid());Directory.CreateDirectory(root);
        void Check(bool value,string reason) { if(!value)throw new Exception(reason); }
        try
        {
            var codex=Path.Combine(root,"codex");var claude=Path.Combine(root,"claude");
            var sessions=Path.Combine(codex,"sessions");Directory.CreateDirectory(sessions);
            var first=Path.Combine(sessions,"first.jsonl");var second=Path.Combine(sessions,"second.jsonl");
            File.WriteAllText(first,"{\"type\":\"session_meta\",\"payload\":{\"id\":\"first\"}}\n");
            File.WriteAllText(second,"{\"type\":\"session_meta\",\"payload\":{\"id\":\"second\"}}\n");
            using var api=new SessionApi(codex,claude,Path.Combine(root,"manager"),Path.Combine(root,"missing"),Path.Combine(root,"gemini"));
            var scan=(SessionScan?)null;
            var catalog=new SessionCatalog(codex,claude);scan=catalog.Scan();
            await api.RequestAsync("GET",new Uri("https://local/api/sessions"),"");
            var key=scan.Sessions.Single(s=>s.Id=="first").Key;
            try { await api.RequestAsync("POST",new Uri("https://local/api/action"),SessionJson.Serialize(new {key,action="delete"}));throw new Exception("Unconfirmed deletion accepted"); }
            catch(ArgumentException) { }
            Check(File.Exists(first),"Unconfirmed deletion changed history");
            var result=await api.RequestAsync("POST",new Uri("https://local/api/action"),SessionJson.Serialize(new {key,action="delete",confirmed=true}));
            Check(!File.Exists(first)&&File.Exists(second),"Deletion affected another Codex session");
            Check(catalog.Scan().Sessions.Count==1,"Deleted Codex session still listed");
            Check(Directory.GetFiles(Path.Combine(root,"manager","deleted"),"*-first.jsonl",SearchOption.AllDirectories).Length==1,"Deleted journal backup missing");
            var secondKey=catalog.Scan().Sessions.Single(s=>s.Id=="second").Key;
            try { await api.RequestAsync("POST",new Uri("https://local/api/delete-batch"),SessionJson.Serialize(new {keys=new[] {secondKey}}));throw new Exception("Unconfirmed batch accepted"); }
            catch(ArgumentException) { }
            Check(File.Exists(second),"Unconfirmed batch changed files");
            var batchResult=await api.RequestAsync("POST",new Uri("https://local/api/delete-batch"),SessionJson.Serialize(new {keys=new[] {secondKey,"missing",secondKey},confirmed=true}));
            using(var json=System.Text.Json.JsonDocument.Parse(SessionJson.Serialize(batchResult)))
            {
                var outcomes=json.RootElement.Get("results").EnumerateArray().ToList();
                Check(outcomes.Count==2&&outcomes[0].Bool("deleted")&&!outcomes[1].Bool("deleted")&&outcomes[1].Str("error").Length>0,"Batch duplicates and partial failure results incorrect");
            }
            Check(!File.Exists(second),"Batch did not delete selected journal");
            var hermes=Path.Combine(root,"hermes");HermesTests.Fixture(hermes);
            var hermesItems=HermesStore.Scan(hermes).ToList();var victim=hermesItems.First();
            SessionDeletion.Delete(victim,Path.Combine(root,"manager"),codex);
            Check(!HermesStore.Exists(victim.Path,victim.Id),"Hermes session not removed");
            Check(HermesStore.Scan(hermes).Count()==hermesItems.Count-1,"Hermes neighboring sessions changed");
            var ag=Path.Combine(root,"ag");AntigravityTests.Fixture(ag);
            var agHome=Path.Combine(ag,"antigravity");var agItem=AntigravityStore.Scan(agHome).Single(s=>s.Id=="shared-id");
            var untouched=AntigravityStore.Scan(agHome).Single(s=>s.Id=="native-id");
            SessionDeletion.Delete(agItem,Path.Combine(root,"manager"),codex);
            Check(!AntigravityStore.Scan(agHome).Any(s=>s.Id==agItem.Id)&&File.Exists(untouched.Path),"Antigravity selected-session deletion failed");
            var cli=Path.Combine(root,"claude.jsonl");var card=Path.Combine(root,"local_card.json");File.WriteAllText(cli,"history");File.WriteAllText(card,"card");
            SessionDeletion.Delete(new SessionItem {Provider="claude",Path=cli,DesktopPath=card},Path.Combine(root,"manager"),codex);
            Check(!File.Exists(cli)&&!File.Exists(card),"Claude CLI/Desktop pair not removed");
            if(System.Diagnostics.Process.GetProcessesByName("Claude").Any(p=>p.MainWindowHandle!=IntPtr.Zero))
            {
                var project=Path.Combine(claude,"projects","test");Directory.CreateDirectory(project);
                var journal=Path.Combine(project,"live.jsonl");File.WriteAllText(journal,"{\"type\":\"user\",\"sessionId\":\"live\",\"message\":{\"content\":\"test\"}}\n");
                await api.RequestAsync("GET",new Uri("https://local/api/sessions"),"");
                var liveKey=catalog.Scan().Sessions.Single(s=>s.Provider=="claude").Key;
                try { await api.RequestAsync("POST",new Uri("https://local/api/action"),SessionJson.Serialize(new { key=liveKey,action="delete",confirmed=true }));throw new Exception("Running Claude deletion reported success"); }
                catch(InvalidOperationException) { }
                Check(File.Exists(journal),"Running Claude guard removed history");
                Console.WriteLine("PASS: running Claude Desktop deletion rejected before changing files.");
            }
            Console.WriteLine("PASS: deletion requires confirmation, saves backups, removes selected Codex/Claude/Hermes/Antigravity sessions and preserves neighbors.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
