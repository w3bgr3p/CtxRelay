using System.Diagnostics;

namespace CdxSwapper;

/// <summary>kill ChatGPT -> подмена auth.json -> запуск. Процесс поднимается обратно на любом исходе.</summary>
static class Swapper
{
    public const string ProcessName = "ChatGPT";

    public static string? FindExe()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            try { var f = p.MainModule?.FileName; if (!string.IsNullOrEmpty(f)) return f; }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    static int CountAlive()
    {
        var ps = Process.GetProcessesByName(ProcessName);
        foreach (var p in ps) p.Dispose();
        return ps.Length;
    }

    static void KillTree()
    {
        var psi = new ProcessStartInfo("taskkill", $"/F /T /IM {ProcessName}.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)!)
        {
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) throw new TimeoutException(Localization.Text(Message.KillTimeout));
            Log.Write($"[kill] taskkill rc={p.ExitCode} {Log.Short(output, 200)}");
        }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (CountAlive() == 0) { Thread.Sleep(1000); return; }   // даём ОС отпустить хэндлы
            Thread.Sleep(500);
        }
        throw new InvalidOperationException(Localization.Text(Message.ProcessStillRunning, ProcessName, CountAlive()));
    }

    static void Start(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        Log.Write($"[start] {exe}");
    }

    /// <summary>Делает target активным. exeFallback — последний известный путь, если приложение не запущено.</summary>
    public static void Swap(Store store, string target, string? exeFallback)
    {
        var step = "resolve";
        try
        {
            var accounts = store.Accounts();
            if (!accounts.TryGetValue(target, out var next))
                throw new InvalidOperationException(Localization.Text(Message.AccountNotFound, target, store.Root));

            step = "sync";                                   // сохраняем свежие токены текущего до подмены
            var current = store.SyncActive() ?? "unknown";
            if (store.Active()?.AccountId == next.AccountId) { Log.Write($"[swap] '{target}' уже активен"); return; }

            step = "find_exe";
            var running = FindExe();
            var exe = running ?? exeFallback;
            var killed = false;
            try
            {
                if (running != null)
                {
                    step = "kill";
                    killed = true;                           // taskkill мог убить часть дерева даже при ошибке
                    KillTree();
                }
                step = "replace";
                var backup = Path.Combine(store.Root, ".backup", $"auth_{current}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                if (File.Exists(store.ActivePath)) Store.AtomicWrite(backup, File.ReadAllBytes(store.ActivePath));
                Store.AtomicWrite(store.ActivePath, next.Raw);
                Log.Write($"[swap] {current} -> {target} ({next.Email}); бэкап {backup}");
            }
            finally
            {
                if (killed && exe != null)
                {
                    try { Start(exe); }
                    catch (Exception e) { Log.Write("[warn] " + Log.Error("start", e)); }
                }
            }
        }
        catch (Exception e)
        {
            throw new SwapException(Log.Error(step, e), e);
        }
    }
}

sealed class SwapException(string message, Exception inner) : Exception(message, inner);
