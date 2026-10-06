using System.Diagnostics;
using System.Text.Json;
using System.Text;
using CdxSwapper.Sessions;
using CdxSwapper;

static class NativeResume
{
    public static async Task Run(string? repairId = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var install = Path.Combine(home, "AppData", "Roaming", "npm", "node_modules", "@openai", "codex");
        var binary = Directory.EnumerateFiles(install, "codex.exe", SearchOption.AllDirectories).First();
        var root = Path.Combine(Path.GetTempPath(), "cdx-native-test-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.jsonl");
            File.WriteAllText(source, string.Join("\n", new object[] {
                new { type = "user", sessionId = "source", cwd = root, message = new { content = "Remember csharp-conversion-marker-927." } },
                new { type = "assistant",message=new { content=new[] { new { type="tool_use",id="toolu_native_check",name="Read",input=new { file_path="example.txt" } } } } },
                new { type="user",message=new { content=new[] { new { type="tool_result",tool_use_id="toolu_native_check",content="Native tool result marker 927" } } } },
                new { type = "assistant", sessionId = "source", cwd = root, message = new { content = new[] { new { type = "text", text = "Saved csharp-conversion-marker-927." } } } }
            }.Select(r => JsonSerializer.Serialize(r))) + "\n", new UTF8Encoding(false));
            var codexHome=Path.Combine(root,"codex"); var claudeHome=Path.Combine(root,"claude"); var exports=Path.Combine(root,"exports");
            var item=new SessionItem { Provider="claude",Id="source",Path=source,Cwd=root,Title="C# conversion check" };
            ConversionResult? previous=null;
            if(repairId!=null)
            {
                var store=new Store();codexHome=store.CodexHome;
                claudeHome=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??Path.Combine(home,".claude");
                exports=Path.Combine(store.Root,".sessions","conversions");
                var old=Directory.EnumerateFiles(exports,"conversion.json",SearchOption.AllDirectories)
                    .Select(p=>JsonSerializer.Deserialize<ConversionResult>(File.ReadAllText(p),SessionJson.Options))
                    .Single(r=>r?.Id==repairId)!;
                if(old.Provider!="codex")throw new Exception("Repair expects a Codex conversion");
                previous=old;
                item=new SessionCatalog(codexHome,claudeHome,antigravityRoot:AntigravityStore.DefaultRoot).Scan().Sessions.Single(s=>s.Path==old.SourcePath);
                source=item.Path;
            }
            var sourceBytes=File.ReadAllBytes(source);
            var converted=new SessionConverter(codexHome,claudeHome,exports).Convert(item,"codex");
            if(!sourceBytes.SequenceEqual(File.ReadAllBytes(source)))throw new Exception("Source journal changed");
            if(previous!=null)
            {
                try { using var probe=File.Open(previous.Path,FileMode.Open,FileAccess.Read,FileShare.Read); }
                catch(IOException) { Console.WriteLine("Original session is open and locked; preserving it and verifying a fresh import instead."); previous=null; }
            }
            if(previous!=null)
            {
                var originalBytes=File.ReadAllBytes(previous.Path);
                var originalRows=Journal.Rows(previous.Path).Select(r=>r.Row).ToList();
                if(originalRows[0].Get("payload").Str("originator")!="cdxSwapper" ||
                    originalRows[0].Str("timestamp")!=previous.Created)
                    throw new Exception("Repair refused: original import prefix no longer matches its manifest");
                var retained=originalRows.SkipWhile(r=>r.Str("timestamp")==previous.Created).ToList();
                var rebuilt=Journal.Rows(converted.Path).Select(r=>r.Row.GetRawText()).ToList();
                var meta=System.Text.Json.Nodes.JsonNode.Parse(rebuilt[0])!;
                meta["payload"]!["id"]=previous.Id; rebuilt[0]=meta.ToJsonString();
                rebuilt.AddRange(retained.Select(r=>r.GetRawText()));
                var folder=Path.GetDirectoryName(converted.TranscriptPath)!;
                File.WriteAllBytes(Path.Combine(folder,"original-before-tool-repair.jsonl.backup"),originalBytes);
                if(!originalBytes.SequenceEqual(File.ReadAllBytes(previous.Path)))throw new Exception("Original import changed during repair");
                Store.AtomicWrite(previous.Path,Encoding.UTF8.GetBytes(string.Join("\n",rebuilt)+"\n"));
                File.Move(converted.Path,Path.Combine(folder,"verified-generated.jsonl.backup"));
                converted=converted with { Id=previous.Id,Path=previous.Path };
                Store.AtomicWrite(Path.Combine(folder,"conversion.json"),Encoding.UTF8.GetBytes(SessionJson.Serialize(converted)));
                Console.WriteLine($"Repaired original session in place; ID retained, {retained.Count} later records retained, original backup saved.");
            }
            var journal=Journal.Rows(converted.Path).Select(r=>r.Row).ToList();
            var toolCalls=journal.Count(r=>r.Get("payload").Str("type")=="function_call");
            var toolResults=journal.Count(r=>r.Get("payload").Str("type")=="function_call_output");
            if(toolCalls==0||toolResults==0)throw new Exception("Converted native tool records missing");
            if(journal.Where(r=>r.Str("type")=="event_msg"&&r.Get("payload").Str("type") is "user_message" or "agent_message")
                .Any(r=>r.Get("payload").Str("message").StartsWith("{\"tool_use_id\"")||r.Get("payload").Str("message").StartsWith("{\"type\":\"tool_use\"")))
                throw new Exception("Raw Claude tool envelope still in conversational events");
            Console.WriteLine($"Native tool records: {toolCalls} calls, {toolResults} results; source preserved={sourceBytes.SequenceEqual(File.ReadAllBytes(source))}.");
            Console.WriteLine($"Verified conversion ID={converted.Id}; path={converted.Path}");
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
            start.Environment["CODEX_HOME"] = codexHome;
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            async Task<JsonElement> Request(int id, string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    if (line == null) throw new Exception("Codex app-server exited");
                    using var doc = JsonDocument.Parse(line); var response = doc.RootElement;
                    if (response.Get("id").ValueKind != JsonValueKind.Number || !response.Get("id").TryGetInt32(out var replyId) || replyId != id) continue;
                    if (response.Get("error").ValueKind != JsonValueKind.Undefined) throw new Exception(response.Get("error").GetRawText());
                    return response.Get("result").Clone();
                }
            }
            try
            {
                await Request(1, "initialize", new { clientInfo = new { name = "cdxswapper-check", version = "1.0" }, capabilities = new { experimentalApi = true } });
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}"); await process.StandardInput.FlushAsync();
                var read = await Request(2, "thread/read", new { threadId = converted.Id, includeTurns = true });
                if (repairId==null&&!read.GetRawText().Contains("csharp-conversion-marker-927")) throw new Exception("Codex failed to read imported messages");
                var items=read.Get("thread").Get("turns").EnumerateArray().SelectMany(t=>t.Get("items").EnumerateArray()).ToList();
                if(items.Any(i=>i.Str("type") is "userMessage" or "agentMessage" && i.GetRawText().Contains("Native tool result marker 927")))
                    throw new Exception("Native Codex renders historic tool result as prose");
                if(items.Count==0)throw new Exception("Native Codex returned no readable conversation");
                if(repairId==null)Console.WriteLine("Tool projection: "+JsonSerializer.Serialize(items.Where(i=>i.Str("type") is not ("userMessage" or "agentMessage"))));
                if(items.Count(i=>i.Str("type")=="mcpToolCall")!=toolResults)
                    throw new Exception("Native desktop history did not expose every imported tool result");
                if(!items.Any(i=>i.Str("type")=="mcpToolCall" && i.GetRawText().Contains("Native tool result marker 927")) && repairId==null)
                    throw new Exception("Installed Codex failed to expose imported tool calls and results in desktop history");
                Console.WriteLine("Native thread/read item counts: "+string.Join(",",items.GroupBy(i=>i.Str("type")).Select(g=>g.Key+"="+g.Count())));
                var resumed = await Request(3, "thread/resume", new { threadId = converted.Id, path = converted.Path });
                if (resumed.Get("thread").Str("id") != converted.Id) throw new Exception("Codex resumed wrong session");
                Console.WriteLine("PASS: installed Codex thread/read + thread/resume accept C# conversion and its full history; no inference.");
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await errors; }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
