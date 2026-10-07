using System.Text.Json;
using CtxDeck;
using CtxDeck.Sessions;

static class NativeAntigravity
{
    public static async Task Run(bool useIde=true)
    {
        var store=new Store();var folder=Path.Combine(store.Root,".sessions","ide-check");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"source.jsonl");
        File.WriteAllLines(path,new object[] {
            new { type="response_item",payload=new { type="message",role="user",content=new[] { new { type="input_text",text="IDE native import marker 927" } } } },
            new { type="response_item",payload=new { type="function_call",name="Read",call_id="ide_tool_927",arguments="{\"file_path\":\"sample.txt\"}" } },
            new { type="response_item",payload=new { type="function_call_output",call_id="ide_tool_927",output="IDE native tool result marker 927" } },
            new { type="response_item",payload=new { type="message",role="assistant",content=new[] { new { type="output_text",text="Imported answer marker 927\n\n[Historical tool call / result] exec {\"input\":\"$destination = Join-Path $root 'historical-marker-927.exe'\"}" } } } }
        }.Select(r=>JsonSerializer.Serialize(r)));
        var item=new SessionItem { Provider="codex",Id="ide-check",Title="IDE import check",Cwd=Environment.CurrentDirectory,Path=path };
        var converter=new SessionConverter(store.CodexHome,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude"),Path.Combine(store.Root,".sessions","conversions"));
        var converted=converter.Convert(item,useIde?"antigravity":"antigravity-app");
        using(var db=HermesStore.Open(converted.Path))
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="SELECT step_type,step_payload FROM steps ORDER BY idx";using var reader=cmd.ExecuteReader();
            var types=new List<long>();while(reader.Read())
            {
                var type=reader.GetInt64(0);types.Add(type);
                var step=AntigravityProto.Fields((byte[])reader[1]);
                if(step.First(f=>f.Number==1).Value!=(ulong)type)throw new Exception("Native IDE step envelope incorrect");
                if(type==38 && !AntigravityProto.Text(AntigravityProto.Blob((byte[])reader[1],47),3).Contains("927"))throw new Exception("Native tool result missing");
            }
            if(!types.SequenceEqual(new long[] {14,15,38,15,15,38}))throw new Exception("Native IDE history roles incorrect");
        }
        var read=await AntigravityIde.OpenAsync(converted.Id,converted.Path,true,"CtxDeck import verification "+converted.Id[..8],useIde:useIde);
        var nativeSteps=read.Get("trajectory").Get("steps").EnumerateArray().ToList();
        if(nativeSteps.Count!=6 || !nativeSteps[0].GetRawText().Contains("IDE native import marker 927") ||
            !nativeSteps[2].GetRawText().Contains("sample.txt") || !nativeSteps[2].GetRawText().Contains("IDE native tool result marker 927") ||
            !nativeSteps[3].GetRawText().Contains("Imported answer marker 927"))throw new Exception("Actual IDE history omitted imported messages or native tool contents");
        if(!nativeSteps[5].GetRawText().Contains("historical-marker-927.exe") || nativeSteps[3].GetRawText().Contains("$destination"))throw new Exception("Historical tool leaked into rendered answer");
        if(!useIde)await Task.Run(()=>AntigravityWindowCheck.Run(converted.Id));
        Console.WriteLine($"PASS: Antigravity {(useIde?"IDE":"application")} accepted, read and opened imported history including native tool call/result. ID={converted.Id}");
    }
}
