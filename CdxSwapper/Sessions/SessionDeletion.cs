using Microsoft.Data.Sqlite;

namespace CdxSwapper.Sessions;

static class SessionDeletion
{
    public static string Delete(SessionItem item,string dataRoot,string codexHome)
    {
        var backup=Path.Combine(dataRoot,"deleted",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup,"session.json"),SessionJson.Serialize(item));
        var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? database=null;string table="",column="";
        if(item.Provider=="hermes") { database=item.Path;table="sessions";column="id"; }
        else
        {
            files.Add(item.Path);
            if(item.DesktopPath.Length>0)files.Add(item.DesktopPath);
            if(item.Provider=="antigravity")
            {
                if(item.Id!=Path.GetFileName(item.Id) || item.Id is "." or "..")throw new IOException("Invalid session ID.");
                var brain=Path.Combine(item.ProviderHome,"brain",item.Id);
                foreach(var file in SessionCatalog.Files(brain,"*"))files.Add(file);
                foreach(var ext in new[] { ".db",".pb" })files.Add(Path.Combine(item.ProviderHome,"conversations",item.Id+ext));
                database=Path.Combine(item.ProviderHome,"conversation_summaries.db");table="conversation_summaries";column="conversation_id";
            }
            else if(item.Provider=="codex" && Directory.Exists(codexHome))
            { database=Directory.EnumerateFiles(codexHome,"state_*.sqlite").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();table="threads";column="id"; }
            else if(item.Provider!="claude" && item.Provider!="codex")throw new ArgumentException("Unknown session provider.");
        }
        foreach(var file in files.ToArray())
            if(Path.GetExtension(file)==".db")foreach(var suffix in new[] { "-wal","-shm" })files.Add(file+suffix);
        var moved=new List<(string Source,string Backup)>();
        try
        {
            foreach(var file in files.Where(File.Exists))
            {
                var destination=Path.Combine(backup,moved.Count.ToString()+"-"+Path.GetFileName(file));
                File.Move(file,destination);moved.Add((file,destination));
            }
            File.WriteAllText(Path.Combine(backup,"files.json"),SessionJson.Serialize(moved.Select(f=>new { original=f.Source,backup=f.Backup })));
            if(database!=null && File.Exists(database))
            {
                using var db=HermesStore.Open(database,write:true);
                using(var integrity=db.CreateCommand()) { integrity.CommandText="PRAGMA foreign_keys=ON";integrity.ExecuteNonQuery(); }
                var databaseBackup=Path.Combine(backup,"index.sqlite");File.WriteAllBytes(databaseBackup,Array.Empty<byte>());
                using(var copy=HermesStore.Open(databaseBackup,write:true))db.BackupDatabase(copy);
                using var transaction=db.BeginTransaction();
                using var command=db.CreateCommand();command.Transaction=transaction;
                command.Parameters.AddWithValue("$id",item.Id);
                if(item.Provider=="hermes")
                { command.CommandText="DELETE FROM messages WHERE session_id=$id";command.ExecuteNonQuery(); }
                command.CommandText=$"DELETE FROM {table} WHERE {column}=$id";command.ExecuteNonQuery();
                transaction.Commit();
            }
            return backup;
        }
        catch
        {
            foreach(var file in moved.AsEnumerable().Reverse())File.Move(file.Backup,file.Source);
            throw;
        }
    }
}
