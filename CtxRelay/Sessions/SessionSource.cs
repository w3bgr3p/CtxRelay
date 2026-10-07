namespace CtxRelay.Sessions;

static class SessionSource
{
    public static string Stamp(SessionItem item) => item.Provider switch { "hermes" => HermesStore.Stamp(item),
        "antigravity" => AntigravityStore.Stamp(item), "claude" => "claude-tools-v2:" + SessionCatalog.Stamp(item.Path),
        _ => SessionCatalog.Stamp(item.Path) };
    public static IEnumerable<(long Offset, SessionMessage Message)> Records(SessionItem item)
    {
        if (item.Provider == "antigravity")
        {
            foreach (var record in AntigravityStore.Records(item)) yield return record;
        }
        else if (item.Provider == "hermes")
        {
            foreach (var record in HermesStore.NativeRecords(item)) yield return record;
        }
        else if (item.MetadataOnly) yield return (0, DesktopSessions.Summary(item.Path));
        else
            foreach (var (offset, row) in Journal.Rows(item.Path))
                foreach(var message in Journal.Messages(item.Provider,row))yield return (offset,message);
    }
}
