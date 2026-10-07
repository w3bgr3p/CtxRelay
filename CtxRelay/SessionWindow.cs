using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using CtxRelay.Sessions;

namespace CtxRelay;

sealed class SessionWindow : Form
{
    // Preserve the private origin so existing WebView preferences survive the rename.
    internal const string Origin = "https://cdxswapper.local";
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 17, 22) };
    readonly SessionApi api;
    readonly string dataRoot;
    readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    bool exiting, disposed;
    public SessionWindow(string codexHome, string claudeHome, string dataRoot, string? hermesHome = null, string? antigravityRoot = null)
    {
        this.dataRoot = dataRoot;
        api = new(codexHome, claudeHome, dataRoot, hermesHome, antigravityRoot);
        Text = "CtxRelay · " + SessionText.Pick("Sessions", "Сессии", "Sesiones");
        Size = new(1440, 940); MinimumSize = new(950, 650); StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Controls.Add(web);
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataRoot, "webview"));
            if (IsDisposed) return;
            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ResourceRequested;
            core.NavigationStarting += (_, e) => { if (!IsLocal(new Uri(e.Uri))) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.Navigate(Origin + "/");
        }
        catch (Exception e)
        {
            Log.Write("[FAIL] " + Log.Error("session_window", e));
            if (!IsDisposed) MessageBox.Show(this, SessionText.Pick("Unable to open sessions. Install Microsoft Edge WebView2 Runtime.\n\n",
                "Не удалось открыть сессии. Установите Microsoft Edge WebView2 Runtime.\n\n",
                "No se pueden abrir las sesiones. Instala Microsoft Edge WebView2 Runtime.\n\n") + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    static bool IsLocal(Uri url) => url.Scheme == "https" && url.Host == "cdxswapper.local" && url.IsDefaultPort;

    async void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        using var deferral = e.GetDeferral();
        int status = 200; string kind = "application/json; charset=utf-8"; byte[] bytes;
        try
        {
            var url = new Uri(e.Request.Uri);
            if (!IsLocal(url)) throw new UnauthorizedAccessException("Invalid origin.");
            if (url.AbsolutePath.StartsWith("/api/"))
            {
                if (!e.Request.Headers.Contains("X-Session-Token") || e.Request.Headers.GetHeader("X-Session-Token") != token)
                    throw new UnauthorizedAccessException("Invalid token.");
                var body = "";
                if (e.Request.Content != null)
                {
                    using var reader = new StreamReader(e.Request.Content, Encoding.UTF8);
                    var limit=url.AbsolutePath=="/api/delete-batch"?131072:4096;
                    var chars = new char[limit+1]; var length = reader.ReadBlock(chars, 0, chars.Length);
                    if (length > limit) throw new ArgumentException("Invalid request size.");
                    body = new string(chars, 0, length);
                }
                bytes = Encoding.UTF8.GetBytes(SessionJson.Serialize(await api.RequestAsync(e.Request.Method, url, body)));
            }
            else
            {
                if (e.Request.Method != "GET") throw new ArgumentException("Invalid method.");
                var path = url.AbsolutePath.TrimStart('/'); if (path == "") path = "index.html";
                kind = Path.GetExtension(path) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8", ".woff2" => "font/woff2", ".png" => "image/png", _ => throw new FileNotFoundException(path) };
                bytes = path == "index.html" ? Encoding.UTF8.GetBytes(WebAssets.ReadText(path)
                    .Replace("__TOKEN__", token).Replace("__LANG__", Localization.Language)
                    .Replace("__LOCALE__", System.Globalization.CultureInfo.CurrentCulture.Name)) : WebAssets.Read(path);
            }
        }
        catch (Exception error)
        {
            status = error is UnauthorizedAccessException ? 403 : error is FileNotFoundException or KeyNotFoundException ? 404 : 400;
            bytes = Encoding.UTF8.GetBytes(SessionJson.Serialize(new { error = error.Message }));
            Log.Write("[warn] " + Log.Error("session_api", error));
        }
        if (IsDisposed || web.CoreWebView2 == null) return;
        var headers = "Content-Type: " + kind + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";
        if (kind.StartsWith("text/html")) headers += "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-src 'none'\r\n";
        e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes), status, status == 200 ? "OK" : "Error", headers);
    }

    public void ShowMain()
    {
        Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
    public void Shutdown() { exiting = true; Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; web.Dispose(); api.Dispose(); }
        base.Dispose(disposing);
    }
}
