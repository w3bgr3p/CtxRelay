using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CdxSwapper.Sessions;

sealed class HermesWriter : IDisposable
{
    readonly SqliteConnection db;
    readonly SqliteTransaction transaction = null!;
    readonly string id;
    int count;
    readonly Dictionary<string, string> calls = new();
    readonly Dictionary<string, string> callArguments = new();
    readonly HashSet<string> completed = new();
    readonly Queue<long>? replacement;
    bool committed;
    public HermesWriter(string home, string id, SessionItem source, DateTimeOffset now, IEnumerable<long>? replaceIds = null)
    {
        var path = Path.Combine(home, "state.db");
        if (!File.Exists(path)) Initialize(home);
        db = HermesStore.Open(path, write: true);
        this.id = id;
        replacement = replaceIds == null ? null : new Queue<long>(replaceIds);
        try
        {
            transaction = db.BeginTransaction();
            if (replacement == null) Insert("sessions", new() { ["id"] = id, ["source"] = "cli", ["started_at"] = now.ToUnixTimeMilliseconds() / 1000.0,
                ["cwd"] = source.Cwd, ["title"] = SessionJson.Cut($"From {source.Provider} · {(source.Title.Length > 0 ? source.Title : source.Id)} · {id[..8]}", 180),
                ["message_count"] = 0, ["tool_call_count"] = 0, ["input_tokens"] = 0, ["output_tokens"] = 0 });
        }
        catch { transaction?.Dispose(); db.Dispose(); throw; }
    }
    void Insert(string table, Dictionary<string, object> values)
    {
        using var info = db.CreateCommand(); info.Transaction = transaction; info.CommandText = "PRAGMA table_info(" + table + ")";
        var columns = new HashSet<string>(); using (var reader = info.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        values = values.Where(p => columns.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        if (table == "messages" && replacement != null)
        {
            if (replacement.Count == 0) throw new InvalidOperationException("Repair would overwrite subsequent conversation. No changes saved.");
            foreach(var column in new[]{"tool_calls","tool_call_id","tool_name"})if(columns.Contains(column)&&!values.ContainsKey(column))values[column]=DBNull.Value;
            cmd.CommandText = $"UPDATE messages SET {string.Join(',',values.Keys.Select(k=>k+"=$"+k))} WHERE id=$replacement AND session_id=$session_id";
            cmd.Parameters.AddWithValue("$replacement",replacement.Dequeue());
        }
        else cmd.CommandText = $"INSERT INTO {table}({string.Join(',', values.Keys)}) VALUES({string.Join(',',values.Keys.Select(k => "$"+k))})";
        foreach (var (key,value) in values) cmd.Parameters.AddWithValue("$"+key,value);
        cmd.ExecuteNonQuery();
    }
    public void Write(SessionMessage message)
    {
        var role = message.Role;
        if (role == "tool")
        {
            var callId = message.CallId.Length > 0 ? message.CallId : "cdx_history_" + Guid.NewGuid().ToString("N");
            if (message.ToolName.Length > 0)
            {
                WriteCall(callId, message.ToolName, message.ToolArguments, message.Timestamp); return;
            }
            if (!calls.ContainsKey(callId)) WriteCall(callId, "historical_context", "{}", message.Timestamp);
            var toolTime = DateTimeOffset.TryParse(message.Timestamp, out var parsed) ? parsed : DateTimeOffset.UtcNow;
            Insert("messages", new() { ["session_id"] = id, ["role"] = "tool", ["content"] = HermesToolDisplay.Result(callArguments[callId], message.Text),
                ["tool_call_id"] = callId, ["tool_name"] = calls[callId], ["timestamp"] = toolTime.ToUnixTimeMilliseconds() / 1000.0, ["active"] = 1 });
            completed.Add(callId); count++; return;
        }
        if (role is not ("user" or "assistant")) return;
        var timestamp = DateTimeOffset.TryParse(message.Timestamp, out var time) ? time : DateTimeOffset.UtcNow;
        Insert("messages", new() { ["session_id"] = id, ["role"] = role, ["content"] = message.Text,
            ["timestamp"] = timestamp.ToUnixTimeMilliseconds() / 1000.0, ["active"] = 1 }); count++;
    }
    void WriteCall(string callId, string name, string arguments, string timestamp)
    {
        if (arguments.Length == 0) arguments = "{}";
        try { using var parsed = JsonDocument.Parse(arguments); if (parsed.RootElement.ValueKind != JsonValueKind.Object) arguments = JsonSerializer.Serialize(new { input = arguments }); }
        catch (JsonException) { arguments = JsonSerializer.Serialize(new { input = arguments }); }
        (name, arguments) = HermesToolDisplay.Call(name, arguments);
        var time = DateTimeOffset.TryParse(timestamp, out var parsedTime) ? parsedTime : DateTimeOffset.UtcNow;
        var tools = JsonSerializer.Serialize(new[] { new { id = callId, type = "function", function = new { name, arguments } } });
        Insert("messages", new() { ["session_id"] = id, ["role"] = "assistant", ["content"] = "", ["tool_calls"] = tools,
            ["timestamp"] = time.ToUnixTimeMilliseconds() / 1000.0, ["active"] = 1 });
        calls[callId] = name; callArguments[callId] = arguments; count++;
    }
    public void Commit()
    {
        foreach (var call in calls.Keys.Where(k => !completed.Contains(k)).ToList())
            Write(new("tool", "[No recorded result in the imported history]", DateTimeOffset.UtcNow.ToString("O"), CallId: call));
        if (replacement?.Count > 0) throw new InvalidOperationException("Repair source differs from the imported prefix. No changes saved.");
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = replacement == null ? "UPDATE sessions SET message_count=$count,tool_call_count=$tools WHERE id=$id" :
            "UPDATE sessions SET message_count=(SELECT count(*) FROM messages WHERE session_id=$id AND active=1),tool_call_count=(SELECT coalesce(sum(json_array_length(tool_calls)),0) FROM messages WHERE session_id=$id AND active=1 AND json_valid(tool_calls)) WHERE id=$id";
        cmd.Parameters.AddWithValue("$tools",calls.Count);
        cmd.Parameters.AddWithValue("$count",count); cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery();
        transaction.Commit(); committed = true;
    }
    public void Dispose() { if (!committed) transaction.Rollback(); transaction.Dispose(); db.Dispose(); }

    static void Initialize(string home)
    {
        SessionActions.EnsureCli("hermes");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        var script = "$env:HERMES_HOME = " + SessionActions.Quote(home) + "; & hermes --profile " +
            SessionActions.Quote(HermesStore.Profile(home)) + " sessions list --limit 1; exit $LASTEXITCODE";
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Hermes database initialization timed out. Start Hermes once and retry."); }
        Task.WhenAll(output,error).GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(Path.Combine(home,"state.db")))
            throw new InvalidOperationException(SessionText.Pick("Unable to initialize the Hermes store. Start Hermes once and retry.",
                "Не удалось создать хранилище Hermes. Запустите Hermes и повторите попытку.", "No se pudo crear el almacén de Hermes. Inicia Hermes y vuelve a intentarlo."));
    }
}
