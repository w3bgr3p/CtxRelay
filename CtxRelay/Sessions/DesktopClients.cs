using System.Diagnostics;
using System.Text.Json;

namespace CtxRelay.Sessions;

static class DesktopClients
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> claudeRequests = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> claudeLogOffsets = new();
    static string ClaudeLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Claude","Logs","main.log");
    public static string ClaudeUrl(SessionItem item)
    {
        if (item.DesktopPath.Length > 0 && item.Id.StartsWith("local_", StringComparison.Ordinal))
            return "claude://claude.ai/epitaxy/" + Uri.EscapeDataString(item.Id);
        var id = item.CliId.Length > 0 ? item.CliId : item.Id;
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Invalid Claude session ID.");
        return "claude://resume?session=" + Uri.EscapeDataString(id);
    }
    public static string Executable(string provider)
    {
        // MSIX Claude must be activated through its registered protocol, not its protected executable.
        if (provider == "claude") return "";
        var name = provider == "claude" ? "Claude" : "Hermes";
        foreach (var process in Process.GetProcessesByName(name))
            using (process)
                try
                {
                    if (process.MainModule?.FileName is { } path && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "resources", "app.asar"))) return path;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (provider == "hermes")
        {
            var path = Path.Combine(HermesRoot(HermesStore.DefaultHome), "hermes-agent", "apps", "desktop", "release", "win-unpacked", "Hermes.exe");
            if (File.Exists(path)) return path;
        }
        return "";
    }
    public static string HermesRoot(string home) => HermesStore.Profile(home) == "default" ? Path.GetFullPath(home) : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(home)))!;
    public static string HermesUrl(SessionItem item) => "hermes://cdx-session/" + Uri.EscapeDataString(item.Id) + "?profile=" + Uri.EscapeDataString(HermesStore.Profile(item.ProviderHome));
    public static void Open(SessionItem item)
    {
        var executable = Executable(item.Provider);
        var url = item.Provider == "claude" ? ClaudeUrl(item) : HermesUrl(item);
        if (item.Provider == "claude")
        {
            claudeRequests[item.Id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            claudeLogOffsets[item.Id] = File.Exists(ClaudeLog) ? new FileInfo(ClaudeLog).Length : 0;
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "claude-desktop.exe");
            if (File.Exists(alias))
            {
                // The MSIX execution alias forwards arguments to the running Desktop instance.
                // Shell activation of the protected executable can discard the session URL.
                var desktop = new ProcessStartInfo(alias) { UseShellExecute = false, CreateNoWindow = true };
                desktop.ArgumentList.Add(url); Process.Start(desktop)?.Dispose(); return;
            }
        }
        if (item.Provider == "hermes") InstallHermesBridge(item);
        var start = new ProcessStartInfo(executable.Length == 0 ? url : executable) { UseShellExecute = true };
        if (executable.Length > 0) start.ArgumentList.Add(url);
        Process.Start(start)?.Dispose();
    }
    public static async Task VerifyClaudeAsync(SessionItem item)
    {
        if (!claudeRequests.TryGetValue(item.Id, out var requested)) throw new InvalidOperationException("Claude launch was not requested.");
        var cliId = item.CliId.Length > 0 ? item.CliId : item.Id;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            foreach (var (_, data) in DesktopSessions.Entries(DesktopSessions.Roots()))
            {
                if (data.Str("sessionId") != item.Id && data.Str("cliSessionId") != cliId) continue;
                if (data.Get("lastFocusedAt").ValueKind == JsonValueKind.Number &&
                    data.Get("lastFocusedAt").GetDouble() >= requested - 1000) return;
                // Reopening the already focused chat does not update lastFocusedAt.
                // Require both a fresh native acknowledgment and the current focus log.
                try
                {
                    using var stream = new FileStream(ClaudeLog,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    var start = Math.Max(0,stream.Length-1024*1024);stream.Position=start;
                    var tail = new StreamReader(stream).ReadToEnd();
                    var focus = tail.LastIndexOf("LocalSessions.setFocusedSession: sessionId=",StringComparison.Ordinal);
                    var focused = focus < 0 ? "" : tail[(focus+"LocalSessions.setFocusedSession: sessionId=".Length)..].Split('\r','\n')[0];
                    var offset = claudeLogOffsets.TryGetValue(item.Id,out var saved) ? saved : stream.Length;
                    stream.Position = Math.Min(offset,stream.Length);
                    var fresh = new StreamReader(stream).ReadToEnd();
                    if (focused == data.Str("sessionId") && fresh.Contains("CLI session " + cliId + " already imported as " + focused,StringComparison.Ordinal)) return;
                }
                catch(IOException) { }
            }
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Claude Desktop не подтвердил открытие выбранной сессии. Запрос отправлен, но приложение не переключилось на этот чат.");
    }
    public static void InstallHermesBridge(SessionItem item)
    {
        var root = HermesRoot(item.ProviderHome.Length > 0 ? item.ProviderHome : HermesStore.DefaultHome);
        var folder = Path.Combine(root, "desktop-plugins", "cdx-session-bridge"); Directory.CreateDirectory(folder);
        var request = Path.Combine(folder, "request.json");
        using var stream = typeof(DesktopClients).Assembly.GetManifestResourceStream("CtxRelay.Sessions.Web.hermes-bridge.js")!;
        var source = new StreamReader(stream).ReadToEnd().Replace("__REQUEST_PATH__", JsonSerializer.Serialize(request));
        foreach (var home in HermesStore.Homes(root).Append(root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var plugin = Path.Combine(home, "desktop-plugins", "cdx-session-bridge", "plugin.js");
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            if (!File.Exists(plugin) || File.ReadAllText(plugin) != source) File.WriteAllText(plugin, source);
        }
        File.WriteAllText(request + ".tmp", JsonSerializer.Serialize(new { requestId = Guid.NewGuid().ToString(), sessionId = item.Id,
            profile = HermesStore.Profile(item.ProviderHome), expires = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() }));
        File.Move(request + ".tmp", request, true);
    }
    public static async Task VerifyHermesAsync(SessionItem item)
    {
        var path = Path.Combine(HermesRoot(item.ProviderHome.Length > 0 ? item.ProviderHome : HermesStore.DefaultHome), "desktop-plugins", "cdx-session-bridge", "request.json");
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        var id = request.RootElement.GetProperty("requestId").GetString();
        for (var attempt = 0; attempt < 90; attempt++)
        {
            try
            {
                using var ack = JsonDocument.Parse(File.ReadAllText(path + ".ack"));
                if (ack.RootElement.GetProperty("requestId").GetString() == id &&
                    ack.RootElement.GetProperty("activeProfile").GetString() == HermesStore.Profile(item.ProviderHome)) return;
            }
            catch (Exception e) when (e is IOException or JsonException) { }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("Hermes Desktop did not confirm opening the session. Check its connection and the CtxRelay session opener plugin.");
    }
}
