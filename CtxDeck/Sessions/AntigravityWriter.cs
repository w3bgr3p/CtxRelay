using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CtxDeck.Sessions;

sealed class AntigravityWriter : IDisposable
{
    readonly SqliteConnection db;
    readonly Dictionary<string, byte[]> calls = new();
    readonly string source;
    readonly byte[] metadata;
    int index;
    public AntigravityWriter(string path, string id, SessionItem item)
    {
        source=item.Provider;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path,Array.Empty<byte>());
        db=HermesStore.Open(path,write:true);
        try
        {
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE trajectory_meta(trajectory_id TEXT PRIMARY KEY,cascade_id TEXT,trajectory_type INTEGER,source INTEGER);
            CREATE TABLE steps(idx INTEGER PRIMARY KEY,step_type INTEGER,status INTEGER,has_subtrajectory NUMERIC DEFAULT 0,metadata BLOB,error_details BLOB,permissions BLOB,task_details BLOB,render_info BLOB,step_payload BLOB,step_format INTEGER DEFAULT 0);
            CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY,data BLOB,size INTEGER DEFAULT 0);
            CREATE TABLE executor_metadata(idx INTEGER PRIMARY KEY,data BLOB);
            CREATE TABLE parent_references(idx INTEGER PRIMARY KEY,data BLOB);
            CREATE TABLE trajectory_metadata_blob(id TEXT PRIMARY KEY DEFAULT 'main',data BLOB);
            CREATE TABLE battle_mode_infos(idx INTEGER PRIMARY KEY,data BLOB);
            INSERT INTO trajectory_meta VALUES($trajectory,$id,4,1);
            INSERT INTO trajectory_metadata_blob VALUES('main',$metadata);
            """;
        var stamp=Number(1,(ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        metadata=Join(Bytes(1,stamp),Bytes(6,stamp),Bytes(7,stamp),Bytes(8,stamp));
        cmd.Parameters.AddWithValue("$trajectory","00000000-0000-4000-8000-"+id.Replace("-","")[..12]);cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$metadata",Join(Bytes(2,stamp),Text(7,new Uri(Path.GetFullPath(item.Cwd)+Path.DirectorySeparatorChar).AbsoluteUri)));
        cmd.ExecuteNonQuery();
        }
        catch { db.Dispose();throw; }
    }
    public void Write(SessionMessage message)
    {
        if(message.Role is "user" or "assistant" && message.Text.Contains("[Historical tool call / result]",StringComparison.Ordinal))
        {
            int position=0;
            foreach(Match block in Regex.Matches(message.Text,@"(?m)^\[Historical tool call / result\][^\r\n]*(?:\r?\n(?!\s*\r?$)[^\r\n]+)*"))
            {
                var prose=message.Text[position..block.Index].Trim();
                if(prose.Length>0)Write(message with { Text=prose });
                var record=block.Value["[Historical tool call / result]".Length..].Trim();
                var id=Guid.NewGuid().ToString("N");
                Write(new("tool",record,message.Timestamp,ToolName:"historical_context",CallId:id,
                    ToolArguments:JsonSerializer.Serialize(new { record })));
                Write(new("tool",record,message.Timestamp,CallId:id));
                position=block.Index+block.Length;
            }
            var remainder=message.Text[position..].Trim();
            // An inline marker is ordinary prose; avoid recursively parsing it.
            if(position>0) { if(remainder.Length>0)Write(message with { Text=remainder });return; }
        }
        if(message.Role=="user") Add(14,19,Bytes(3,Text(1,message.Text)));
        else if(message.Role=="assistant") Add(15,20,Join(Text(1,message.Text),Text(8,message.Text)));
        else if(message.Role=="tool")
        {
            var id=message.CallId.Length>0?message.CallId:Guid.NewGuid().ToString("N");
            if(message.ToolName.Length>0)
            {
                var call=Join(Text(1,id),Text(2,message.ToolName),Text(3,message.ToolArguments.Length>0?message.ToolArguments:"{}"));
                calls[id]=call; Add(15,20,Bytes(7,call));
            }
            else
            {
                if(!calls.Remove(id,out var call)) call=Join(Text(1,id),Text(2,"historical_context"),Text(3,"{}"));
                Add(38,47,Join(Text(1,source),Bytes(2,call),Text(3,message.Text)),call);
            }
        }
    }
    void Add(int type,int field,byte[] payload,byte[]? toolCall=null)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO steps(idx,step_type,status,has_subtrajectory,metadata,step_payload,step_format) VALUES($idx,$type,3,0,$meta,$payload,0)";
        var stepMetadata=Join(metadata,Number(3,type==14?4UL:2UL),toolCall==null?Array.Empty<byte>():Bytes(4,toolCall));
        cmd.Parameters.AddWithValue("$idx",index++);cmd.Parameters.AddWithValue("$type",type);cmd.Parameters.AddWithValue("$meta",stepMetadata);
        cmd.Parameters.AddWithValue("$payload",Join(Number(1,(ulong)type),Number(4,3),Bytes(5,stepMetadata),Bytes(field,payload)));cmd.ExecuteNonQuery();
    }
    public static byte[] Join(params byte[][] values)=>values.SelectMany(v=>v).ToArray();
    static byte[] Varint(ulong value) { var bytes=new List<byte>();while(value>127){bytes.Add((byte)((value&127)|128));value>>=7;}bytes.Add((byte)value);return bytes.ToArray(); }
    static byte[] Number(int field,ulong value)=>Join(Varint((ulong)(field<<3)),Varint(value));
    static byte[] Bytes(int field,byte[] value)=>Join(Varint((ulong)((field<<3)|2)),Varint((ulong)value.Length),value);
    static byte[] Text(int field,string value)=>Bytes(field,Encoding.UTF8.GetBytes(value));
    public void Dispose()
    {
        foreach(var call in calls.Values)Add(38,47,Join(Text(1,source),Bytes(2,call),Text(3,"[Result absent in source history]")),call);
        calls.Clear();db.Dispose();SqliteConnection.ClearPool(db);
    }
}
