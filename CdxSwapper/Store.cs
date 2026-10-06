using System.Text.RegularExpressions;

namespace CdxSwapper;

/// <summary>
/// Хранилище: &lt;parent of .codex&gt;\cdxSwapper\&lt;name&gt;\auth.json.
/// Активная авторизация: %CODEX_HOME%\auth.json (по умолчанию ~\.codex\auth.json).
/// </summary>
sealed class Store
{
    static readonly object AuthGate = new();
    public string CodexHome { get; }
    public string Root { get; }
    public string ActivePath => System.IO.Path.Combine(CodexHome, "auth.json");

    public Store()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        CodexHome = !string.IsNullOrWhiteSpace(env)
            ? env
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var parent = Directory.GetParent(System.IO.Path.GetFullPath(CodexHome).TrimEnd('\\'))?.FullName
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Root = System.IO.Path.Combine(parent, "cdxSwapper");
        Directory.CreateDirectory(Root);
    }

    public string PathFor(string name) => System.IO.Path.Combine(Root, name, "auth.json");

    /// <summary>name -> AuthFile по папкам с auth.json (папки на "." пропускаются).</summary>
    public SortedDictionary<string, AuthFile> Accounts()
    {
        var res = new SortedDictionary<string, AuthFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            if (AuthFile.TryLoad(PathFor(name)) is { } a) res[name] = a;
        }
        return res;
    }

    public AuthFile? Active() => AuthFile.TryLoad(ActivePath);

    /// <summary>Папка с тем же account_id, иначе новое имя из email.</summary>
    public string NameFor(AuthFile auth, IDictionary<string, AuthFile> accounts)
    {
        foreach (var (n, a) in accounts)
            if (a.AccountId == auth.AccountId) return n;
        var local = auth.Email.Split('@')[0];
        var baseName = Regex.Replace(local, @"[^\w.\-]+", "_");
        if (baseName.Length == 0) baseName = auth.AccountId;
        var name = baseName;
        if (accounts.ContainsKey(name) || Directory.Exists(System.IO.Path.Combine(Root, name)))
            name = $"{baseName}_{auth.AccountId[..Math.Min(8, auth.AccountId.Length)]}";
        return name;
    }

    /// <summary>
    /// Копирует активный auth.json в папку его аккаунта, если байты отличаются и
    /// last_refresh не старше сохранённого. Возвращает имя активного аккаунта (или null).
    /// </summary>
    public string? SyncActive()
    {
        lock (AuthGate) return SyncActiveCore();
    }

    string? SyncActiveCore()
    {
        var active = Active();
        if (active == null) return null;
        var accounts = Accounts();
        var name = NameFor(active, accounts);
        accounts.TryGetValue(name, out var saved);
        if (saved != null && saved.Raw.AsSpan().SequenceEqual(active.Raw)) return name;
        if (saved?.LastRefresh is { } s && active.LastRefresh is { } a && a < s)
        {
            Log.Write($"[sync] {name}: активный last_refresh={active.LastRefreshRaw} старше сохранённого {saved.LastRefreshRaw} — не перезаписываю");
            return name;
        }
        AtomicWrite(PathFor(name), active.Raw);
        Log.Write($"[sync] {name} ({active.Email}) <- {ActivePath} last_refresh={active.LastRefreshRaw}" + (saved == null ? " [новый аккаунт]" : ""));
        return name;
    }

    public string ConfigPath => System.IO.Path.Combine(CodexHome, "config.toml");

    public AuthFile ReloadForRefresh(AuthFile previous)
    {
        lock (AuthGate)
        {
            SyncActiveCore();
            var current = new AuthFile(previous.Path);
            if (current.AccountId != previous.AccountId)
                throw new InvalidOperationException(Localization.Text(Message.RefreshAccountChanged));
            return current;
        }
    }

    public AuthFile SaveRefreshed(AuthFile previous, byte[] updated)
    {
        lock (AuthGate)
        {
            // Codex may have rotated credentials while the HTTP request was in flight.
            SyncActiveCore();
            var current = new AuthFile(previous.Path);
            if (current.AccountId != previous.AccountId)
                throw new InvalidOperationException(Localization.Text(Message.RefreshAccountChanged));
            if (!current.Raw.AsSpan().SequenceEqual(previous.Raw)) return current;
            var active = Active();
            AtomicWrite(previous.Path, updated);
            if (active?.AccountId == previous.AccountId && active.Raw.AsSpan().SequenceEqual(previous.Raw))
                AtomicWrite(ActivePath, updated);
            return new AuthFile(previous.Path);
        }
    }

    static readonly Regex KeyLine = new(@"^\s*cli_auth_credentials_store\s*=\s*(.*?)\s*(#.*)?$");
    static readonly Regex TableHeader = new(@"^\s*\[\[?\s*[\w.""'\:\- ]+\s*\]\]?\s*(#.*)?$");

    /// <summary>
    /// Гарантирует cli_auth_credentials_store = "file" на верхнем уровне config.toml
    /// (ключ верхнего уровня обязан стоять до первой [таблицы]). Возвращает true, если файл изменён.
    /// Codex читает конфиг при запуске — после изменения его нужно перезапустить.
    /// </summary>
    public bool EnsureFileCredentialStore()
    {
        const string want = "cli_auth_credentials_store = \"file\"";
        var text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        int firstTable = lines.FindIndex(l => TableHeader.IsMatch(l));
        var top = firstTable < 0 ? lines.Count : firstTable;
        for (var i = 0; i < top; i++)
        {
            var m = KeyLine.Match(lines[i]);
            if (!m.Success) continue;
            if (m.Groups[1].Value.Trim() is "\"file\"" or "'file'") return false;
            lines[i] = want;                         // ключ есть, но значение другое
            WriteConfig(lines, nl);
            Log.Write($"[config] {ConfigPath}: cli_auth_credentials_store был {m.Groups[1].Value} -> \"file\"");
            return true;
        }
        lines.Insert(top, want);
        if (top < lines.Count - 1 && lines[top + 1].Length > 0 && TableHeader.IsMatch(lines[top + 1])) lines.Insert(top + 1, "");
        WriteConfig(lines, nl);
        Log.Write($"[config] {ConfigPath}: добавлено {want}");
        return true;
    }

    void WriteConfig(List<string> lines, string nl)
    {
        if (File.Exists(ConfigPath))
            AtomicWrite(System.IO.Path.Combine(Root, ".backup", $"config_{DateTime.Now:yyyyMMdd_HHmmss}.toml"), File.ReadAllBytes(ConfigPath));
        AtomicWrite(ConfigPath, new System.Text.UTF8Encoding(false).GetBytes(string.Join(nl, lines)));
    }

    public static void AtomicWrite(string path, byte[] data)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".cdx_tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
