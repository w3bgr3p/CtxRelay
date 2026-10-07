using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static CtxDeck.Localization;

namespace CtxDeck;

sealed class TrayApp : ApplicationContext
{
    const int SyncIntervalMs = 15_000;
    const int UsageIntervalMs = 5 * 60_000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "CtxDeck";
    const string LegacyRunName = "CdxSwapper";

    readonly Store _store;
    readonly AuthRefresher _authRefresher;
    readonly Settings _settings;
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = SyncIntervalMs };
    readonly System.Windows.Forms.Timer _usageTimer = new() { Interval = UsageIntervalMs };

    List<UsageInfo> _usage = new();
    UsageInfo? _claudeUsage;
    DateTime _usageAt = DateTime.MinValue;
    string? _activeName;
    bool _refreshing, _swapping;
    IntPtr _iconHandle;
    SessionWindow? _window;

    public TrayApp(Store store, bool showMainWindow = true)
    {
        _store = store;
        _authRefresher = new AuthRefresher(store);
        _settings = Settings.Load(store.Root);
        UpdateAutostart();
        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = _menu, Text = "CtxDeck" };
        SetIcon(null);
        ModernMenu.Apply(_menu);                    // до BuildMenu: обновляет палитру на Opening
        _menu.Opening += (_, _) => { BuildMenu(); if (DateTime.Now - _usageAt > TimeSpan.FromSeconds(60)) _ = RefreshUsage(); };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
        _syncTimer.Tick += (_, _) => SyncTick();
        _usageTimer.Tick += (_, _) => _ = RefreshUsage();
        _syncTimer.Start();
        _usageTimer.Start();
        EnsureFileAuth();
        SyncTick();
        _ = RefreshUsage();
        if (showMainWindow) ShowMainWindow();
    }

    void ShowMainWindow()
    {
        try
        {
            if (_window == null || _window.IsDisposed)
            {
                var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrWhiteSpace(claudeHome)) claudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                _window = new SessionWindow(_store.CodexHome, claudeHome, Path.Combine(_store.Root, ".sessions"));
            }
            _window.ShowMain();
        }
        catch (Exception e) { MessageBox.Show(Log.Error("session_window", e), "CtxDeck", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>Разовая проверка при старте: Codex должен хранить авторизацию в файле.</summary>
    void EnsureFileAuth()
    {
        try
        {
            if (_store.EnsureFileCredentialStore())
                _tray.ShowBalloonTip(5000, "CtxDeck", Text(Message.FileAuthEnabled), ToolTipIcon.Warning);
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("config", e)); }
    }

    void SyncTick()
    {
        try
        {
            var name = _store.SyncActive();
            if (name != _activeName)
            {
                Log.Write($"[watch] активный аккаунт: {name ?? "-"}");
                _activeName = name;
                UpdateTrayView();
            }
            if (Swapper.FindExe() is { } exe && exe != _settings.ExePath)
            {
                _settings.ExePath = exe;
                _settings.Save();
            }
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("sync", e)); }
    }

    async Task RefreshUsage()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var accounts = _store.Accounts();
            var activeId = _store.Active()?.AccountId;
            var tasks = accounts.Select(kv => UsageClient.FetchAsync(kv.Key, kv.Value, kv.Value.AccountId == activeId, _authRefresher));
            var codexTask=Task.WhenAll(tasks);var claudeTask=ClaudeUsage.FetchAsync();
            await Task.WhenAll(codexTask,claudeTask);
            _usage = codexTask.Result.ToList();_claudeUsage=claudeTask.Result;
            _usageAt = DateTime.Now;
            foreach (var u in _usage)
                Log.Write($"[usage] {u.Name}: " + (u.Error ?? $"limit={u.LimitReached} 5h={Fmt(u.Primary)} week={Fmt(u.Secondary)}"));
            if(_claudeUsage is { } cu)Log.Write("[usage] Claude: "+(cu.Error??$"5h={Fmt(cu.Primary)} week={Fmt(cu.Secondary)}"));
            WriteUsageJson();
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("usage", e)); }
        finally
        {
            _refreshing = false;
            UpdateTrayView();
            if (_menu.Visible) BuildMenu();
        }
    }

    void WriteUsageJson()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { checked_at = _usageAt, accounts = _usage, claude = _claudeUsage },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Store.AtomicWrite(Path.Combine(_store.Root, "usage.json"), System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("usage_json", e)); }
    }

    static string Fmt(UsageWindow? w) => w == null ? "-"
        : Text(Message.WindowRemaining, w.Left, w.ResetAt?.ToString("g") ?? "–");

    UsageInfo? ActiveUsage() => _usage.FirstOrDefault(u => u.Name == _activeName) ?? _usage.FirstOrDefault(u => u.Active);

    // ------------------------------------------------------------ вид

    void UpdateTrayView()
    {
        var u = ActiveUsage();
        SetIcon(u?.Error == null ? u?.Left : null);
        var text = u == null ? $"CtxDeck: {_activeName ?? Text(Message.NoAuth)}"
            : u.Error != null ? $"{u.Name}: {Text(Message.UsageError)}"
            : $"{u.Name}\n{Text(Message.FiveHours)}: {Fmt(u.Primary)}\n{Text(Message.Week)}: {Fmt(u.Secondary)}";
        _tray.Text = text.Length <= 127 ? text : text[..127];
    }

    void SetIcon(double? left)
    {
        using var bmp = RenderTrayIcon(left, Math.Max(16, GetSystemMetrics(49)));
        var h = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(h);
        old?.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = h;
    }

    internal static Bitmap RenderTrayIcon(double? left, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(24, 24, 28));
            var s = left == null ? "?" : ((int)Math.Clamp(Math.Round(left.Value), 0, 100)).ToString();
            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            path.AddString(s, family, (int)FontStyle.Bold, size, Point.Empty, StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            // Fit the actual glyph outlines, without the font's invisible padding.
            var scaleY = (size - 2f) / bounds.Height;
            var scaleX = Math.Min((size - 2f) / bounds.Width, scaleY);
            using var transform = new Matrix(scaleX, 0, 0, scaleY,
                (size - bounds.Width * scaleX) / 2 - bounds.X * scaleX,
                (size - bounds.Height * scaleY) / 2 - bounds.Y * scaleY);
            path.Transform(transform);
            using var brush = new SolidBrush(left == null ? Color.Silver : LeftColor(left));
            g.FillPath(brush, path);
        }
        return bmp;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static Color LeftColor(double? left) => left == null ? Color.FromArgb(110, 110, 110)
        : left <= 10 ? Color.FromArgb(220, 60, 60)
        : left <= 35 ? Color.FromArgb(230, 160, 20)
        : Color.FromArgb(40, 170, 90);

    static string Pct(UsageWindow? w) => w == null ? "–" : $"{w.Left:0}%";

    void BuildMenu()
    {
        var m = _menu;
        m.SuspendLayout();
        foreach (ToolStripItem old in m.Items) old.Image?.Dispose();
        m.Items.Clear();
        var accounts = _store.Accounts();

        // шапка: активный аккаунт и его остаток
        var au = ActiveUsage();
        var head = _activeName == null ? Text(Message.NoActiveAuth)
            : au?.Error == null && au?.Left is { } l ? Text(Message.Remaining, _activeName, l)
            : $"{_activeName}";
        m.Items.Add(ModernMenu.Caption(m, head, ModernMenu.Dot(m, LeftColor(au?.Error == null ? au?.Left : null))));
        m.Items.Add(ModernMenu.Caption(m, _refreshing ? Text(Message.Refreshing)
            : _usageAt == DateTime.MinValue ? Text(Message.NotFetched) : Text(Message.UpdatedAt, _usageAt)));
        m.Items.Add(ModernMenu.Separator());

        foreach (var (name, auth) in accounts)
        {
            var u = _usage.FirstOrDefault(x => x.Name == name);
            var active = name == _activeName;
            var right = u == null ? Text(Message.NoData)
                : u.Error != null ? Text(Message.Error)
                : u.LimitReached == true ? Text(Message.LimitUntil, (u.Primary?.Left == 0 ? u.Primary : u.Secondary)?.ResetAt)
                : $"{Text(Message.FiveHours)} {Pct(u.Primary)}  ·  {Text(Message.WeekShort)} {Pct(u.Secondary)}";
            var img = active ? ModernMenu.Glyph(m, ModernMenu.GlyphCheck, ModernMenu.Accent(m))
                : ModernMenu.Dot(m, LeftColor(u?.Error == null ? u?.Left : null));
            var item = ModernMenu.Item(m, name, right, img);
            item.ToolTipText = u?.Error != null ? $"{auth.Email}\n{u.Error}"
                : u != null ? $"{auth.Email}\n{Text(Message.FiveHours)}: {Fmt(u.Primary)}\n{Text(Message.Week)}: {Fmt(u.Secondary)}" : auth.Email;
            if (active) item.Tag = null;
            else if (_swapping) item.Enabled = false;
            else item.Click += (_, _) => _ = DoSwap(name);
            m.Items.Add(item);
        }
        if (accounts.Count == 0) m.Items.Add(ModernMenu.Caption(m, Text(Message.NoAccounts)));
        if(ClaudeCredentials.Installed)
        {
            m.Items.Add(ModernMenu.Separator());
            var cu=_claudeUsage;
            var right=cu==null?Text(Message.NoData):cu.Error!=null?Text(Message.Error):
                $"{Text(Message.FiveHours)} {Pct(cu.Primary)}  ·  {Text(Message.WeekShort)} {Pct(cu.Secondary)}";
            var item=ModernMenu.Item(m,"Claude",right,ModernMenu.Dot(m,LeftColor(cu?.Error==null?cu?.Left:null)));
            item.ToolTipText=cu?.Error??$"{Text(Message.FiveHours)}: {Fmt(cu?.Primary)}\n{Text(Message.Week)}: {Fmt(cu?.Secondary)}";
            m.Items.Add(item);
        }

        m.Items.Add(ModernMenu.Separator());
        m.Items.Add(ModernMenu.Item(m, Text(Message.RefreshLimits), null, ModernMenu.Glyph(m, ModernMenu.GlyphRefresh), (_, _) => _ = RefreshUsage()));
        m.Items.Add(ModernMenu.Item(m, Text(Message.OpenFolder), null, ModernMenu.Glyph(m, ModernMenu.GlyphFolder),
            (_, _) => Process.Start(new ProcessStartInfo(_store.Root) { UseShellExecute = true })?.Dispose()));
        m.Items.Add(ModernMenu.Item(m, Text(Message.Autostart), Text(IsAutostart() ? Message.On : Message.Off), ModernMenu.Glyph(m, ModernMenu.GlyphStartup),
            (_, _) => ToggleAutostart()));
        m.Items.Add(ModernMenu.Separator());
        m.Items.Add(ModernMenu.Item(m, Text(Message.Exit), null, ModernMenu.Glyph(m, ModernMenu.GlyphPower), (_, _) => ExitThread()));
        m.ResumeLayout();
    }

    /// <summary>--preview: открыть меню программно и сохранить его скриншот (для проверки вида без клика по трею).</summary>
    public async Task PreviewAsync(string pngPath)
    {
        await RefreshUsage();
        while(_refreshing)await Task.Delay(100);
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(_tray, null);
        await Task.Delay(Environment.GetEnvironmentVariable("CDX_PREVIEW_HOLD") is { } h && int.TryParse(h, out var ms) ? ms : 700);
        try
        {
            var b = _menu.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height);
            _menu.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));
            bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("preview", e)); }
        _menu.Close();
        ExitThread();
    }

    // ------------------------------------------------------------ действия

    async Task DoSwap(string name)
    {
        var u = _usage.FirstOrDefault(x => x.Name == name);
        var msg = Text(Message.ConfirmSwap, name) +
                  (u?.LimitReached == true ? Text(Message.LimitWarning) : "");
        if (MessageBox.Show(msg, "CtxDeck", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _swapping = true;
        try
        {
            if (_store.Accounts().TryGetValue(name, out var auth))
                await _authRefresher.EnsureAsync(auth);
            await Task.Run(() => Swapper.Swap(_store, name, _settings.ExePath));
            _tray.ShowBalloonTip(3000, "CtxDeck", Text(Message.ActiveAccount, name), ToolTipIcon.Info);
        }
        catch (Exception e)
        {
            Log.Write("[FAIL] " + e.Message);
            MessageBox.Show(e.Message, Text(Message.SwapFailed), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _swapping = false;
            SyncTick();
            _ = RefreshUsage();
        }
    }

    static bool IsAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) != null; }
        catch { return false; }
    }

    static void UpdateAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null || (k.GetValue(RunName) == null && k.GetValue(LegacyRunName) == null)) return;
            k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            k.DeleteValue(LegacyRunName, throwOnMissingValue: false);
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("autostart_upgrade", e)); }
    }

    static void ToggleAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            else k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception e) { Log.Write("[warn] " + Log.Error("autostart", e)); }
    }

    protected override void ExitThreadCore()
    {
        _window?.Shutdown();
        _syncTimer.Stop();
        _usageTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        base.ExitThreadCore();
    }
}
