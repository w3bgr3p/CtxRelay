using System.Text;
using System.Text.Json;
using CdxSwapper;
using CdxSwapper.Sessions;

static class RepairHermes
{
    public static void Display()
    {
        var root=Path.Combine(new Store().Root,".sessions","conversions");
        foreach(var path in Directory.EnumerateFiles(root,"conversion.json",SearchOption.AllDirectories))
        {
            var conversion=JsonSerializer.Deserialize<ConversionResult>(File.ReadAllText(path),SessionJson.Options)!;
            if(conversion.Provider!="hermes"||!File.Exists(conversion.Path))continue;
            using var db=HermesStore.Open(conversion.Path,write:true);
            var rows=new List<(long Id,string Role,string Content,string Calls,string CallId)>();
            using(var read=db.CreateCommand())
            {
                read.CommandText="SELECT id,role,coalesce(content,''),coalesce(tool_calls,''),coalesce(tool_call_id,'') FROM messages WHERE session_id=$id ORDER BY id LIMIT $limit";
                read.Parameters.AddWithValue("$id",conversion.Id);read.Parameters.AddWithValue("$limit",conversion.Messages+1);
                using var r=read.ExecuteReader();while(r.Read())rows.Add((r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)));
            }
            var updates=new Dictionary<long,(string? Calls,string? Content,string? Name)>();
            var calls=new Dictionary<string,(string Name,string Arguments)>();
            foreach(var row in rows.Where(r=>r.Calls.Length>0))
            {
                var data=System.Text.Json.Nodes.JsonNode.Parse(row.Calls)!.AsArray();bool changed=false;
                foreach(var node in data)
                {
                    var fn=node!["function"]!;var args=fn["arguments"]!.ToString();
                    if(System.Text.Json.Nodes.JsonNode.Parse(args)?["cdx_source_tool"]!=null)continue;
                    var adapted=HermesToolDisplay.Call(fn["name"]!.ToString(),args);
                    fn["name"]=adapted.Name;fn["arguments"]=adapted.Arguments;
                    calls[node["id"]!.ToString()]=adapted;changed=true;
                }
                if(changed)updates[row.Id]=(data.ToJsonString(),null,null);
            }
            foreach(var row in rows.Where(r=>r.Role=="tool"&&calls.ContainsKey(r.CallId)))
            {
                var call=calls[row.CallId];updates[row.Id]=(null,HermesToolDisplay.Result(call.Arguments,row.Content),call.Name);
            }
            if(updates.Count==0)continue;
            var backup=Path.Combine(Path.GetDirectoryName(path)!,"hermes-before-tool-display.db");
            if(!File.Exists(backup))using(var target=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+backup)){target.Open();db.BackupDatabase(target);}
            using var tx=db.BeginTransaction();
            foreach(var (id,update) in updates)
            {
                using var cmd=db.CreateCommand();cmd.Transaction=tx;
                cmd.CommandText="UPDATE messages SET tool_calls=coalesce($calls,tool_calls),content=coalesce($content,content),tool_name=coalesce($name,tool_name) WHERE id=$row AND session_id=$session";
                cmd.Parameters.AddWithValue("$calls",(object?)update.Calls??DBNull.Value);cmd.Parameters.AddWithValue("$content",(object?)update.Content??DBNull.Value);
                cmd.Parameters.AddWithValue("$name",(object?)update.Name??DBNull.Value);cmd.Parameters.AddWithValue("$row",id);cmd.Parameters.AddWithValue("$session",conversion.Id);cmd.ExecuteNonQuery();
            }
            tx.Commit();Console.WriteLine($"DISPLAY REPAIRED {conversion.Id}: {calls.Count} calls, {updates.Count} rows; IDs and subsequent messages preserved. Backup: {backup}");
        }
    }
    public static void Run()
    {
        var root=Path.Combine(new Store().Root,".sessions","conversions");
        foreach(var path in Directory.EnumerateFiles(root,"conversion.json",SearchOption.AllDirectories))
        {
            var conversion=JsonSerializer.Deserialize<ConversionResult>(File.ReadAllText(path),SessionJson.Options)!;
            if(conversion.Provider!="hermes"||!File.Exists(conversion.SourcePath))continue;
            using var db=HermesStore.Open(conversion.Path,write:true);using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT id,role,content,timestamp FROM messages WHERE session_id=$id ORDER BY id LIMIT $limit";
            cmd.Parameters.AddWithValue("$id",conversion.Id);cmd.Parameters.AddWithValue("$limit",conversion.Messages+1);
            var ids=new List<long>();SessionMessage? note=null;bool broken=false;
            using(var reader=cmd.ExecuteReader())while(reader.Read())
            {
                ids.Add(reader.GetInt64(0));var content=reader.IsDBNull(2)?"":reader.GetString(2);
                broken|=content.StartsWith("[Historical tool call / result]");
                note??=new(reader.GetString(1),content,DateTimeOffset.FromUnixTimeMilliseconds((long)(reader.GetDouble(3)*1000)).ToString("O"));
            }
            if(!broken)continue;
            var provider=conversion.SourcePath.EndsWith(".jsonl")&&Path.GetFileName(conversion.SourcePath).StartsWith("rollout-")?"codex":"claude";
            var source=new SessionItem{Provider=provider,Id=conversion.SourceId,Path=conversion.SourcePath,Cwd=conversion.Cwd};
            var messages=new List<SessionMessage>();using var previous=new StringWriter();
            foreach(var (_,row) in Journal.Rows(source.Path))foreach(var message in Journal.Messages(provider,row))
            {
                if(message.Role=="metadata")continue;
                var oldText=message.Text;var payload=row.Get("payload");
                if(provider=="codex"&&message.Role=="tool")
                {
                    var body=payload.Get("arguments");if(body.ValueKind==JsonValueKind.Undefined)body=payload.Get("output");
                    if(body.ValueKind==JsonValueKind.Undefined)body=payload.Get("input");
                    oldText=payload.Str("name")+"\n"+(body.ValueKind==JsonValueKind.String?body.GetString():body.ValueKind==JsonValueKind.Undefined?"":body.GetRawText());
                }
                if(oldText.Length==0)continue;
                if(messages.Count==conversion.Messages)break;
                previous.WriteLine($"## {message.Role} · {message.Timestamp}\n\n{oldText}\n");messages.Add(message);
            }
            if(ids.Count!=conversion.Messages+1||messages.Count!=conversion.Messages||previous.ToString()!=File.ReadAllText(conversion.TranscriptPath))
                throw new Exception("Source differs from original import; refused unsafe repair: "+conversion.Id);
            var backup=Path.Combine(Path.GetDirectoryName(path)!,"hermes-before-native-tools.db");
            if(!File.Exists(backup))using(var target=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+backup)){target.Open();db.BackupDatabase(target);}
            using(var writer=new HermesWriter(Path.GetDirectoryName(conversion.Path)!,conversion.Id,source,DateTimeOffset.UtcNow,ids))
            {writer.Write(note!);foreach(var message in messages)writer.Write(message);writer.Commit();}
            Console.WriteLine($"REPAIRED {conversion.Id}: {messages.Count(m=>m.ToolName.Length>0)} native calls, {messages.Count(m=>m.Role=="tool"&&m.ToolName.Length==0)} results; original row IDs and subsequent messages preserved. Backup: {backup}");
        }
    }
}
