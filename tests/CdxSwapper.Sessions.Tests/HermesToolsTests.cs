using System.Text.Json;
using CdxSwapper.Sessions;

static class HermesToolsTests
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"cdx-hermes-tools-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var script="text(await tools.exec_command({cmd:'rtk git status'}));text(await tools.apply_patch('patch'));";
            var display=HermesToolDisplay.Call("exec",JsonSerializer.Serialize(new{input=script}));
            using(var args=JsonDocument.Parse(display.Arguments))
            {
                var data=args.RootElement;
                if(display.Name!="execute_code"||data.GetProperty("code").GetString()!=script||
                    data.GetProperty("command").GetString()!="tools.exec_command / tools.apply_patch")throw new Exception("Exec wrapper loses code or nested tool identities.");
            }
            using(var output=JsonDocument.Parse(HermesToolDisplay.Result(display.Arguments,"actual result")))
            {
                var detail=output.RootElement.GetProperty("output").GetString()!;
                if(!detail.Contains(script)||!detail.Contains("Result:\nactual result"))throw new Exception("Hermes display omits arguments or output.");
            }
            if(HermesToolDisplay.Call(display.Name,display.Arguments)!=display)throw new Exception("Hermes display adaptation is not idempotent.");
            var literal=Journal.Message("codex",JsonSerializer.SerializeToElement(new{type="response_item",payload=new{type="function_call_output",call_id="literal",output=new[]{1,2,3}}}));
            if(literal?.Text!="[1,2,3]")throw new Exception("Literal JSON tool result array was lost.");
            var home=Path.Combine(root,"hermes");HermesTests.Fixture(home);
            var path=Path.Combine(root,"source.jsonl");
            var rows=new object[] {
                new {type="session_meta",payload=new{id="source",cwd=root}},
                new {type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text="Question"}}}},
                new {type="response_item",payload=new{type="function_call",name="functions.exec",call_id="parallel-a",arguments="{\"code\":\"DO NOT EXECUTE\"}"}},
                new {type="response_item",payload=new{type="function_call",name="search",call_id="parallel-b",arguments="{\"query\":\"test\"}"}},
                new {type="response_item",payload=new{type="function_call_output",call_id="parallel-b",output="[{\"type\":\"input_text\",\"text\":\"Found Ω\"}]"}},
                new {type="response_item",payload=new{type="function_call_output",call_id="parallel-a",output="Completed"}},
                new {type="response_item",payload=new{type="custom_tool_call",name="apply_patch",call_id="custom",input="*** Begin Patch\n*** End Patch"}},
                new {type="response_item",payload=new{type="custom_tool_call_output",call_id="custom",output="Done"}},
                new {type="response_item",payload=new{type="function_call",name="pending",call_id="pending",arguments="{}"}},
                new {type="response_item",payload=new{type="message",role="assistant",content=new[]{new{type="output_text",text="Answer"}}}} };
            File.WriteAllLines(path,rows.Select(r=>JsonSerializer.Serialize(r)));
            var result=new SessionConverter(Path.Combine(root,"codex"),Path.Combine(root,"claude"),Path.Combine(root,"exports"),home)
                .Convert(new SessionItem{Provider="codex",Id="source",Path=path,Cwd=root},"hermes");
            using var db=HermesStore.Open(result.Path);using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT role,content,tool_calls,tool_call_id,tool_name FROM messages WHERE session_id=$id ORDER BY id";cmd.Parameters.AddWithValue("$id",result.Id);
            var calls=new Dictionary<string,string>();var outputs=new Dictionary<string,string>();var prose=new List<string>();
            using(var reader=cmd.ExecuteReader())while(reader.Read())
            {
                if(!reader.IsDBNull(2))foreach(var call in JsonDocument.Parse(reader.GetString(2)).RootElement.EnumerateArray())
                { var function=call.GetProperty("function");calls.Add(call.GetProperty("id").GetString()!,function.GetProperty("name").GetString()!); }
                else if(reader.GetString(0)=="tool")outputs.Add(reader.GetString(3),reader.GetString(1));
                else prose.Add(reader.GetString(1));
            }
            if(calls.Count!=4||outputs.Count!=4||!calls.Keys.Order().SequenceEqual(outputs.Keys.Order())||
                calls["parallel-a"]!="execute_code"||JsonDocument.Parse(outputs["parallel-b"]).RootElement.GetProperty("cdx_source_result").GetString()!="Found Ω"||
                !outputs["pending"].Contains("No recorded result")||prose.Any(p=>p.Contains("Historical tool")||p.Contains("DO NOT EXECUTE")))
                throw new Exception("Hermes native tool linkage, output decoding, or completion failed.");
            cmd.CommandText="SELECT tool_call_count FROM sessions WHERE id=$id";
            if((long)cmd.ExecuteScalar()! != 4)throw new Exception("Hermes tool count incorrect.");
            Console.WriteLine("PASS: Hermes native calls/results, parallel IDs, custom tools, decoded text output, completed historical calls, clean prose.");
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
}
