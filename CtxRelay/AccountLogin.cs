using System.Diagnostics;

namespace CtxRelay;

/// <summary>Browser sign-in in an isolated home; never logs out the running client.</summary>
sealed class AccountLogin(Store store, Func<string, CancellationToken, Task<int>>? run = null)
{
    public async Task<(string Name, bool Added)> AddAsync(CancellationToken cancellationToken)
    {
        var home = Path.Combine(store.Root, ".login", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var exit = await (run ?? RunAsync)(home, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (exit != 0) throw new InvalidOperationException(Localization.Text(Message.LoginFailed, exit));
            var path = Path.Combine(home, "auth.json");
            if (!File.Exists(path)) throw new InvalidDataException(Localization.Text(Message.LoginInvalid));
            var auth = new AuthFile(path);
            if (string.IsNullOrWhiteSpace(auth.AccessToken) || string.IsNullOrWhiteSpace(auth.RefreshToken))
                throw new InvalidDataException(Localization.Text(Message.LoginInvalid));
            return store.ImportAccount(auth);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); }
            catch (Exception e) { Log.Write("[warn] " + Log.Error("login_cleanup", e)); }
        }
    }

    static async Task<int> RunAsync(string home, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(FindCli(), home) };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        // Drain both pipes without retaining URLs, authorization codes or tokens.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    internal static string FindCli()
    {
        foreach (var process in Process.GetProcessesByName("Codex").Concat(Process.GetProcessesByName("ChatGPT")))
            using (process)
                try
                {
                    if (process.MainModule?.FileName is { } path)
                    {
                        var bundled = Path.Combine(Path.GetDirectoryName(path)!, "resources", "codex.exe");
                        if (File.Exists(bundled)) return bundled;
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "resources")))
        {
            var executable = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(executable)) return executable;
            // npm's shim is a .cmd file; launch its native binary directly, without a shell.
            var vendor = Path.Combine(directory.Trim('"'), "node_modules", "@openai", "codex", "vendor");
            var native = Path.Combine(vendor, "x86_64-pc-windows-msvc", "codex", "codex.exe");
            if (File.Exists(native)) return native;
            native = Path.Combine(directory.Trim('"'), "node_modules", "@openai", "codex", "node_modules", "@openai",
                "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
            if (File.Exists(native)) return native;
            native = Path.Combine(directory.Trim('"'), "node_modules", "@openai", "codex-win32-x64",
                "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
            if (File.Exists(native)) return native;
        }
        throw new InvalidOperationException(Localization.Text(Message.LoginCliMissing));
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string home)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["CODEX_HOME"] = home;
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        start.ArgumentList.Add("login");
        return start;
    }
}
