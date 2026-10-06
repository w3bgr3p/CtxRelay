using System.Text.Json;

namespace CdxSwapper.Sessions;

sealed class NativeSessionWriter
{
    readonly TextWriter stream;
    readonly string provider, sessionId, cwd, timestamp, title;
    string? parent, turn;
    string? lastAnswer;
    readonly HashSet<string> claudeCalls = new();
    readonly Dictionary<string, (string Name, JsonElement Arguments)> codexCalls = new();
    readonly string importedProvider;
    public NativeSessionWriter(TextWriter stream, string provider, string id, SessionItem item, string timestamp)
    {
        this.stream = stream; this.provider = provider; sessionId = id; cwd = item.Cwd; this.timestamp = timestamp;
        importedProvider = item.Provider.Length > 0 ? item.Provider : "imported";
        title = SessionJson.Cut($"From {item.Provider} · {(item.Title.Length > 0 ? item.Title : item.Id)}", 180);
        if (provider == "codex") Record("session_meta", new { id, timestamp, cwd, originator = "cdxSwapper",
            cli_version = "0.148.0", source = "cli", model_provider = "openai", history_mode = "legacy" });
    }
    void Dump(object value) => stream.WriteLine(JsonSerializer.Serialize(value));
    void Record(string type, object payload) => Dump(new { timestamp, type, payload });
    public void Write(SessionMessage message)
    {
        var role = message.Role == "tool" ? "assistant" : message.Role;
        var text = message.Text;
        if (provider == "codex")
        {
            if(message.Role=="tool")
            {
                var callId=message.CallId.Length>0?message.CallId:"cdx_history_"+Guid.NewGuid().ToString("N");
                if(message.ToolName.Length>0)
                {
                    Record("response_item",new { type="function_call",name=message.ToolName,arguments=message.ToolArguments.Length>0?message.ToolArguments:"{}",call_id=callId });
                    CodexToolStart(callId,message.ToolName,message.ToolArguments);
                }
                else
                {
                    if(message.CallId.Length==0)
                    {
                        Record("response_item",new { type="function_call",name="historical_context",arguments="{}",call_id=callId });
                        CodexToolStart(callId,"historical_context","{}");
                    }
                    Record("response_item",new { type="function_call_output",call_id=callId,output=message.Text });
                    if(codexCalls.Remove(callId,out var call))
                        Record("event_msg",new { type="mcp_tool_call_end",call_id=callId,turn_id=turn,
                            invocation=new { server=importedProvider,tool=call.Name,arguments=call.Arguments },
                            result=new { Ok=new { content=new[] { new { type="text",text=message.Text } },isError=false } },duration=new { secs=0,nanos=0 } });
                }
                return;
            }
            if (role == "user")
            {
                Finish(); turn = Guid.NewGuid().ToString();
                Record("event_msg", new { type = "task_started", turn_id = turn, model_context_window = (int?)null });
                Record("event_msg", new { type = "user_message", message = text, images = Array.Empty<string>(), local_images = Array.Empty<string>() });
            }
            else { lastAnswer = text; Record("event_msg", new { type = "agent_message", message = text, phase = "final_answer" }); }
            Record("response_item", new { type = "message", role,
                content = new[] { new { type = role == "user" ? "input_text" : "output_text", text } } });
            return;
        }
        if (message.Role == "tool")
        {
            var callId = message.CallId.Length > 0 ? message.CallId : "toolu_" + Guid.NewGuid().ToString("N");
            if (message.ToolName.Length > 0)
            {
                object input;
                try { using var parsed = JsonDocument.Parse(message.ToolArguments); input = parsed.RootElement.ValueKind == JsonValueKind.Object ? parsed.RootElement.Clone() : new { input = message.ToolArguments }; }
                catch (JsonException) { input = new { input = message.ToolArguments }; }
                ClaudeRecord("assistant", new object[] { new { type = "tool_use", id = callId, name = message.ToolName, input } });
                claudeCalls.Add(callId);
            }
            else
            {
                if (!claudeCalls.Contains(callId))
                    ClaudeRecord("assistant", new object[] { new { type = "tool_use", id = callId, name = "historical_context", input = new { } } });
                ClaudeRecord("user", new object[] { new { type = "tool_result", tool_use_id = callId, content = text } });
                claudeCalls.Remove(callId);
            }
            return;
        }
        ClaudeRecord(role, role == "user" ? text : new object[] { new { type = "text", text } });
    }
    void CodexToolStart(string id, string name, string arguments)
    {
        JsonElement input;
        try { using var doc=JsonDocument.Parse(arguments.Length>0?arguments:"{}"); input=doc.RootElement.Clone(); }
        catch(JsonException) { input=JsonSerializer.SerializeToElement(new { input=arguments }); }
        codexCalls[id]=(name,input);
        Record("event_msg",new { type="mcp_tool_call_begin",call_id=id,turn_id=turn,invocation=new { server=importedProvider,tool=name,arguments=input } });
    }
    void ClaudeRecord(string role, object content)
    {
        var uid = Guid.NewGuid().ToString();
        object msg = role == "user" ? new { role, content } : new { role, content,
            id = "msg_" + Guid.NewGuid().ToString("N"), type = "message", model = "imported", stop_reason = "end_turn",
            stop_sequence = (string?)null, usage = new { input_tokens = 0, output_tokens = 0 } };
        Dump(new { type = role, uuid = uid, parentUuid = parent, sessionId, cwd, timestamp, isSidechain = false,
            userType = "external", version = "2.1.190", message = msg });
        parent = uid;
    }
    public void Finish()
    {
        if (provider == "claude")
        {
            foreach (var id in claudeCalls)
                ClaudeRecord("user", new object[] { new { type = "tool_result", tool_use_id = id, content = "[Result absent in source history]" } });
            claudeCalls.Clear();
            Dump(new { type = "custom-title", sessionId, customTitle = title }); return;
        }
        if (turn != null) Record("event_msg", new { type = "task_complete", turn_id = turn, last_agent_message = lastAnswer });
        turn = null; lastAnswer = null;
    }
}
