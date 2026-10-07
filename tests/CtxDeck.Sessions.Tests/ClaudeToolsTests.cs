using System.Text.Json;
using CtxDeck.Sessions;

static class ClaudeToolsTests
{
    public static void Run()
    {
        var row = JsonSerializer.SerializeToElement(new { type = "assistant", message = new { content = new object[] {
            new { type = "text", text = "Checking the file." },
            new { type = "tool_use", id = "toolu_regression", name = "Read", input = new { file_path = "sample.txt" } }
        } } });
        var messages = Journal.Messages("claude",row).ToList();
        if(messages.Count!=2 || messages[0].Role!="assistant" || messages[0].Text!="Checking the file." ||
            messages[1].Role!="tool" || messages[1].ToolName!="Read" || messages[1].CallId!="toolu_regression")
            throw new Exception("Claude mixed prose and tools were not separated");
        var result = JsonSerializer.SerializeToElement(new { type="user",message=new { content=new[] {
            new { type="tool_result",tool_use_id="toolu_regression",content="File contents marker" }
        } } });
        var output=Journal.Messages("claude",result).Single();
        if(output.Role!="tool" || output.Text!="File contents marker" || output.CallId!="toolu_regression")
            throw new Exception("Claude tool result became a user message or raw JSON");
        using var stream=new StringWriter();
        var writer=new NativeSessionWriter(stream,"codex",Guid.NewGuid().ToString(),new SessionItem { Cwd=Path.GetTempPath() },DateTimeOffset.UtcNow.ToString("O"));
        writer.Write(new("user","Question",""));foreach(var message in messages)writer.Write(message);writer.Write(output);writer.Finish();
        var rows=stream.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToList();
        var prose=rows.Where(r=>r.Str("type")=="event_msg"&&r.Get("payload").Str("type") is "user_message" or "agent_message").ToList();
        if(prose.Any(r=>r.GetRawText().Contains("toolu_regression")||r.GetRawText().Contains("File contents marker")))
            throw new Exception("Historic tools rendered as conversational messages");
        if(!rows.Any(r=>r.Get("payload").Str("type")=="function_call")||
            !rows.Any(r=>r.Get("payload").Str("type")=="function_call_output"&&r.Get("payload").Str("output")=="File contents marker"))
            throw new Exception("Native Codex tool history missing");
        using var claudeStream = new StringWriter();
        var claudeWriter = new NativeSessionWriter(claudeStream,"claude",Guid.NewGuid().ToString(),new SessionItem(),DateTimeOffset.UtcNow.ToString("O"));
        foreach(var record in rows)
            foreach(var message in Journal.Messages("codex",record))
                if(message.Role != "metadata") claudeWriter.Write(message);
        claudeWriter.Finish();
        var claudeRows = claudeStream.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToList();
        var blocks = claudeRows.Select(r=>r.Get("message").Get("content")).Where(c=>c.ValueKind==JsonValueKind.Array).SelectMany(c=>c.EnumerateArray()).ToList();
        var call = blocks.Single(b=>b.Str("type")=="tool_use");
        var reply = blocks.Single(b=>b.Str("type")=="tool_result");
        if(call.Str("id")!=reply.Str("tool_use_id") || call.Str("name")!="Read" || call.Get("input").Str("file_path")!="sample.txt" || reply.Str("content")!="File contents marker")
            throw new Exception("Codex to Claude lost native tool arguments, output or pairing");
        if(claudeStream.ToString().Contains("[Historical tool call / result]")) throw new Exception("Claude tools leaked into prose");
        Console.WriteLine("PASS: Native tools round-trip Codex and Claude with arguments, results and matching IDs.");
    }
}
