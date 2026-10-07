using System.Text;
using System.Text.Json;

namespace CtxRelay.Sessions;

static class Journal
{
    public static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);

    public static IEnumerable<(long Offset, JsonElement Row)> Rows(string path, long start = 0, long? end = null)
    {
        using var file = Open(path);
        file.Position = start;
        using var line = new MemoryStream();
        var buffer = new byte[65536];
        long offset = start, position = start;
        int count;
        while ((count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Max(0, (end ?? long.MaxValue) - position)))) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                position++;
                if (buffer[i] != 10) { line.WriteByte(buffer[i]); continue; }
                var row = Parse(line.ToArray());
                if (row.HasValue) yield return (offset, row.Value);
                line.SetLength(0); offset = position;
            }
        }
        if (line.Length > 0 && Parse(line.ToArray()) is { } last) yield return (offset, last);
    }

    public static long RecordStart(string path, long position)
    {
        using var file = Open(path);
        position = Math.Clamp(position, 0, file.Length);
        var buffer = new byte[65536];
        while (position > 0)
        {
            var probe = Math.Max(0, position - buffer.Length);
            file.Position = probe;
            var count = file.Read(buffer, 0, (int)(position - probe));
            for (int i = count - 1; i >= 0; i--) if (buffer[i] == 10) return probe + i + 1;
            position = probe;
        }
        return 0;
    }

    public static string Content(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var result = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            var kind = block.Str("type");
            if (kind is "text" or "input_text" or "output_text") result.Add(block.Str("text"));
            else if(kind=="tool_use")result.Add(block.Str("name")+"\n"+(block.Get("input").ValueKind==JsonValueKind.Undefined?"{}":block.Get("input").GetRawText()));
            else if(kind=="tool_result")result.Add(Content(block.Get("content")));
            else if (kind is "image" or "input_image") result.Add("[Image]");
        }
        return string.Join("\n", result);
    }

    public static SessionMessage? Message(string provider, JsonElement row)
    {
        if (provider == "antigravity") return AntigravityStore.Message(row);
        var kind = row.Str("type");
        var timestamp = row.Str("timestamp");
        if (provider == "claude")
        {
            var messages=Messages(provider,row).ToList();
            return messages.Count==0?null:messages[0] with { Text=string.Join("\n\n",messages.Select(m=>m.Text)) };
        }
        var payload = row.Get("payload");
        if (kind == "session_meta") return payload.ValueKind == JsonValueKind.Object ? new("metadata", payload.GetRawText(), timestamp) : null;
        if (kind != "response_item") return null;
        var role = payload.Str("role");
        if (payload.Str("type") == "message" && role is "user" or "assistant")
            return new(role, Content(payload.Get("content")), timestamp);
        if (payload.Str("type") is "function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output")
        {
            var call = payload.Str("type") is "function_call" or "custom_tool_call";
            var body = payload.Get("arguments");
            if (body.ValueKind == JsonValueKind.Undefined) body = payload.Get("output");
            if (body.ValueKind == JsonValueKind.Undefined) body = payload.Get("input");
            var text = body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" :
                body.ValueKind == JsonValueKind.Undefined ? "" : body.GetRawText();
            if (!call && text.TrimStart().StartsWith('['))
                try
                {
                    using var result = JsonDocument.Parse(text);
                    if (result.RootElement.ValueKind == JsonValueKind.Array && result.RootElement.GetArrayLength() > 0 && result.RootElement.EnumerateArray().All(b => b.Str("type") is "text" or "input_text" or "output_text"))
                        text = Content(result.RootElement);
                }
                catch (JsonException) { }
            var arguments = call ? text : "";
            if (call && payload.Str("type") == "custom_tool_call") arguments = JsonSerializer.Serialize(new { input = text });
            return new("tool", call ? payload.Str("name") + "\n" + text : text, timestamp,
                ToolName: call ? payload.Str("name") : "", CallId: payload.Str("call_id"), ToolArguments: arguments);
        }
        return null;
    }

    public static IEnumerable<SessionMessage> Messages(string provider, JsonElement row)
    {
        if (provider != "claude")
        {
            if (Message(provider,row) is { } message) yield return message;
            yield break;
        }
        var role=row.Str("type"); if(role is not ("user" or "assistant"))yield break;
        var content=row.Get("message").Get("content"); var timestamp=row.Str("timestamp");
        if(content.ValueKind!=JsonValueKind.Array)
        {
            var text=Content(content);if(text.Length>0)yield return new(role,text,timestamp);yield break;
        }
        foreach(var block in content.EnumerateArray())
        {
            var kind=block.Str("type");
            if(kind=="tool_use")
            {
                var input=block.Get("input"); var arguments=input.ValueKind==JsonValueKind.Undefined?"{}":input.GetRawText();
                yield return new("tool",block.Str("name")+"\n"+arguments,timestamp,ToolName:block.Str("name"),CallId:block.Str("id"),ToolArguments:arguments);
            }
            else if(kind=="tool_result")
            {
                var text=Content(block.Get("content"));
                if(text.Length==0)text="[No text output]";
                if(block.Bool("is_error"))text="[Tool error]\n"+text;
                yield return new("tool",text,timestamp,CallId:block.Str("tool_use_id"));
            }
            else if(kind=="text"&&block.Str("text").Length>0)yield return new(role,block.Str("text"),timestamp);
            else if(kind=="image")yield return new(role,"[Image]",timestamp);
        }
    }

    static JsonElement? Parse(byte[] line)
    {
        try
        {
            // Strip a possible UTF-8 BOM only at the start of a record.
            var start = line.Length >= 3 && line[0] == 239 && line[1] == 187 && line[2] == 191 ? 3 : 0;
            using var doc = JsonDocument.Parse(line.AsMemory(start));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }
}
