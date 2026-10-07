using Microsoft.Data.Sqlite;

namespace CtxDeck.Sessions;

sealed class SessionSearch : IDisposable
{
    readonly string connectionString;
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    List<SessionItem>? pending;
    Task worker = Task.CompletedTask;
    IndexStatus progress = new(false, 0, 0, new());

    public SessionSearch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        using var db = Open();
        Execute(db, "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS files(key TEXT PRIMARY KEY, stamp TEXT); " +
            "CREATE VIRTUAL TABLE IF NOT EXISTS messages USING fts5(key UNINDEXED, offset UNINDEXED, role UNINDEXED, text, tokenize='trigram', detail=none);");
    }

    SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }
    public IndexStatus Status() { lock (gate) return progress with { Errors = progress.Errors.ToList() }; }
    public void Sync(IEnumerable<SessionItem> items)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) return;
            pending = items.Select(s => s with { }).ToList();
            if (progress.Busy) return;
            progress = new(true, 0, pending.Count, new());
            worker = Task.Run(Run);
        }
    }
    public async Task WaitAsync()
    {
        Task active; lock (gate) active = worker;
        await active;
    }

    void Run()
    {
        try
        {
            using var db = Open();
            while (!stop.IsCancellationRequested)
            {
                List<SessionItem> batch;
                lock (gate)
                {
                    if (pending == null) { progress = progress with { Busy = false }; return; }
                    batch = pending; pending = null;
                    progress = new(true, 0, batch.Count, new());
                }
                var known = new Dictionary<string, string>();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT key, stamp FROM files";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) known[reader.GetString(0)] = reader.GetString(1);
                }
                foreach (var item in batch)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var stamp = item.Provider == "hermes" && item.ContentStamp.Length > 0 ? item.ContentStamp : SessionSource.Stamp(item);
                        if (!known.TryGetValue(item.Key, out var previous) || previous != stamp) IndexFile(db, item, stamp);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
                    {
                        lock (gate) progress = progress with { Errors = progress.Errors.Append(item.Path + ": " + e.Message).TakeLast(10).ToList() };
                    }
                    lock (gate) progress = progress with { Done = progress.Done + 1 };
                }
                var alive = batch.Select(s => s.Key).ToHashSet();
                foreach (var key in known.Keys.Where(k => !alive.Contains(k)))
                    Execute(db, "DELETE FROM messages WHERE key=$key; DELETE FROM files WHERE key=$key;", ("$key", key));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { lock (gate) progress = progress with { Errors = new() { e.Message } }; }
        finally { lock (gate) progress = progress with { Busy = false }; }
    }

    void IndexFile(SqliteConnection db, SessionItem item, string stamp)
    {
        using var transaction = db.BeginTransaction();
        using var delete = db.CreateCommand(); delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM messages WHERE key=$key"; delete.Parameters.AddWithValue("$key", item.Key); delete.ExecuteNonQuery();
        using var insert = db.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO messages(key,offset,role,text) VALUES($key,$offset,$role,$text)";
        insert.Parameters.AddWithValue("$key", item.Key); insert.Parameters.AddWithValue("$offset", 0L);
        insert.Parameters.AddWithValue("$role", ""); insert.Parameters.AddWithValue("$text", ""); insert.Prepare();
        void Add(long offset, SessionMessage? message)
        {
            stop.Token.ThrowIfCancellationRequested();
            if (message == null || message.Text.Length == 0) return;
            insert.Parameters["$offset"].Value = offset; insert.Parameters["$role"].Value = message.Role;
            insert.Parameters["$text"].Value = message.Text.ToLowerInvariant(); insert.ExecuteNonQuery();
        }
        foreach (var (offset, message) in SessionSource.Records(item)) Add(offset, message);
        // A changing log is intentionally left unstamped so the next scan indexes it again.
        if (SessionSource.Stamp(item) != stamp) return;
        using var file = db.CreateCommand(); file.Transaction = transaction;
        file.CommandText = "INSERT OR REPLACE INTO files VALUES($key,$stamp)";
        file.Parameters.AddWithValue("$key", item.Key); file.Parameters.AddWithValue("$stamp", stamp); file.ExecuteNonQuery();
        transaction.Commit();
    }

    public SearchResult Search(string query)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length > 500) throw new ArgumentException(SessionText.Pick("Search is limited to 500 characters.",
            "Запрос слишком длинный (максимум 500 символов).", "La búsqueda admite hasta 500 caracteres."));
        var matches = new Dictionary<string, SearchMatch>();
        if (query.Length == 0) return new(matches, Status());
        using var db = Open(); using var cmd = db.CreateCommand();
        var where = "instr(text,$query)>0";
        // FTS5 trigram counts Unicode codepoints; leave short/surrogate-containing queries literal.
        if (query.Length >= 3 && !query.Any(char.IsSurrogate))
        {
            where = "messages MATCH $gram AND " + where;
            cmd.Parameters.AddWithValue("$gram", "\"" + query[..3].Replace("\"", "\"\"") + "\"");
        }
        cmd.CommandText = "SELECT key,offset,role,text FROM messages WHERE " + where;
        cmd.Parameters.AddWithValue("$query", query);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0); var offset = reader.GetInt64(1); var role = reader.GetString(2); var text = reader.GetString(3);
            if (!matches.TryGetValue(key, out var match)) matches[key] = match = new();
            match.Count++;
            var position = text.IndexOf(query, StringComparison.Ordinal);
            var start = Math.Max(0, position - 80);
            match.Hits.Add(new(offset, role, text.Substring(start, Math.Min(text.Length - start, position - start + query.Length + 160))));
            match.Hits.Sort((a, b) =>
            {
                int prose(SearchHit h) => h.Snippet.TrimStart().StartsWith('{') || h.Snippet.TrimStart().StartsWith('[') ? 1 : 0;
                var p = prose(a).CompareTo(prose(b)); if (p != 0) return p;
                p = (a.Role != "assistant").CompareTo(b.Role != "assistant");
                return p != 0 ? p : b.Offset.CompareTo(a.Offset);
            });
            if (match.Hits.Count > 30) match.Hits.RemoveAt(30);
        }
        return new(matches, Status());
    }

    public void Dispose()
    {
        stop.Cancel();
        try { worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}
