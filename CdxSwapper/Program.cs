using System.Text.Json;

namespace CdxSwapper;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Localization.Initialize();
        Store store;
        try { store = new Store(); }
        catch (Exception e)
        {
            MessageBox.Show(Log.Error("init_store", e), "CdxSwapper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        Log.FilePath = Path.Combine(store.Root, "cdxSwapper.log");

        // --check: sync + лимиты в <store>\check.txt, без трея. Для проверки без UI.
        if (args.Contains("--check")) return Check(store);

        using var mutex = new Mutex(true, @"Local\CdxSwapper.single", out var first);
        if (!first) return 0;

        Application.ThreadException += (_, e) => Log.Write("[FAIL] " + Log.Error("ui", e.Exception));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("[FATAL] " + Log.Short(e.ExceptionObject, 1000));
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("[warn] " + Log.Error("task", e.Exception)); e.SetObserved(); };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();
        Log.Write($"[start] store={store.Root} codex_home={store.CodexHome}");
        var app = new TrayApp(store);
        var pi = Array.IndexOf(args, "--preview");
        if (pi >= 0 && pi + 1 < args.Length)
        {
            var png = args[pi + 1];
            var t = new System.Windows.Forms.Timer { Interval = 100 };
            t.Tick += (_, _) => { t.Stop(); _ = app.PreviewAsync(png); };
            t.Start();
        }
        Application.Run(app);
        return 0;
    }

    static int Check(Store store)
    {
        var lines = new List<string>();
        try
        {
            lines.Add($"store={store.Root}");
            lines.Add($"active={store.SyncActive() ?? "-"}");
            var activeId = store.Active()?.AccountId;
            foreach (var (n, a) in store.Accounts())
            {
                var u = UsageClient.FetchAsync(n, a, a.AccountId == activeId).GetAwaiter().GetResult();
                lines.Add($"{(u.Active ? "*" : " ")} {n} {a.Email} " + (u.Error ??
                    $"limit={u.LimitReached} 5h_left={u.Primary?.Left} reset={u.Primary?.ResetAt:dd.MM HH:mm} week_left={u.Secondary?.Left} reset={u.Secondary?.ResetAt:dd.MM HH:mm}"));
            }
            File.WriteAllLines(Path.Combine(store.Root, "check.txt"), lines);
            return 0;
        }
        catch (Exception e)
        {
            lines.Add("[FAIL] " + Log.Error("check", e));
            try { File.WriteAllLines(Path.Combine(store.Root, "check.txt"), lines); } catch { }
            return 1;
        }
    }
}

sealed class Settings
{
    public string? ExePath { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] string _path = "";

    public static Settings Load(string root)
    {
        var path = Path.Combine(root, "settings.json");
        Settings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("settings_load", e)); s = new(); }
        s._path = path;
        return s;
    }

    public void Save()
    {
        try { Store.AtomicWrite(_path, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("settings_save", e)); }
    }
}
