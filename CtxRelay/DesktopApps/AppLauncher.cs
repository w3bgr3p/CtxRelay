using System.Diagnostics;
using System.Text.Json;

namespace CtxRelay.DesktopApps;

static class AppLauncher
{
    public static string DataRoot { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"CtxRelay");
    public static void Initialize(string root)=>DataRoot=root;
    public static string PreferencesPath=>Path.Combine(DataRoot,"desktop-apps.json");
    public static List<AppSelection> Selections()
    {
        if(!File.Exists(PreferencesPath))return [];
        return JsonSerializer.Deserialize<List<AppSelection>>(File.ReadAllText(PreferencesPath)) ?? [];
    }
    public static AppSelection? Selected(string provider)=>Selections().FirstOrDefault(x=>x.Provider==provider);
    public static void Select(InstalledApp app)
    {
        var values=Selections();values.RemoveAll(x=>x.Provider==app.Provider);
        values.Add(new(app.Provider,app.Mode,app.Mode=="msix"?app.AppId:app.Executable));
        Store.AtomicWrite(PreferencesPath,JsonSerializer.SerializeToUtf8Bytes(values));
    }
    public static string Payload(string provider,string root)
    {
        foreach(var name in AppProviders.Processes(provider))
            foreach(var folder in new[]{root,Path.Combine(root,"app")})
            {
                var exe=Path.Combine(folder,name+".exe");
                if(File.Exists(exe)&&File.Exists(Path.Combine(folder,"resources","app.asar")))
                {
                    if(provider=="codex" && !File.Exists(Path.Combine(folder,"resources","codex.exe")) &&
                        !File.Exists(Path.Combine(folder,"resources","bin","codex.exe")))continue;
                    return exe;
                }
            }
        return "";
    }
    public static string RunningExecutable(string provider)
    {
        foreach(var name in AppProviders.Processes(provider))foreach(var process in Process.GetProcessesByName(name))
            using(process)try
            {
                var path=process.MainModule?.FileName;
                if(path!=null && Payload(provider,Path.GetDirectoryName(path)!)==path)return path;
            }
            catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return "";
    }
    public static ProcessStartInfo StartInfo(string provider,string executable,string? url=null)
    {
        var root=Path.GetDirectoryName(Path.GetFullPath(executable))!;
        if(Payload(provider,root)!=Path.GetFullPath(executable))throw new InvalidDataException("Selected executable is not a supported desktop client.");
        var start=new ProcessStartInfo(executable) { UseShellExecute=false,WorkingDirectory=root };
        if(url!=null)
        {
            if(!url.StartsWith(provider+"://",StringComparison.OrdinalIgnoreCase)||url.Any(char.IsControl))throw new ArgumentException("Invalid client URL.");
            start.ArgumentList.Add(url);
        }
        if(provider=="codex")
        {
            start.Environment.Remove("CODEX_WINDOWS_REGISTERED_CORE");start.Environment["CODEX_SPARKLE_ENABLED"]="false";
            if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_CLI_PATH")))
                start.Environment["CODEX_CLI_PATH"]=new[]{Path.Combine(root,"resources","codex.exe"),Path.Combine(root,"resources","bin","codex.exe")}.First(File.Exists);
        }
        return start;
    }
    public static void Launch(string provider,string? url=null,string? fallback=null)
    {
        AppProviders.Check(provider);
        AppManager.RecoverKnown(DataRoot);
        var selection=Selected(provider);
        if(selection?.Mode=="msix") { ActivatePackage(provider,selection.Path,url);return; }
        var exe=selection?.Path ?? RunningExecutable(provider);
        if(exe.Length==0 && fallback!=null && File.Exists(fallback))exe=fallback;
        if(exe.Length==0)
        {
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach(var root in new[]{Path.Combine(local,"Programs",provider=="codex"?"Codex":"Claude"),Path.Combine(local,"AnthropicClaude")})
                if(Payload(provider,root) is { Length:>0 } candidate){exe=candidate;break;}
        }
        if(exe.Length>0 && !exe.Contains("\\WindowsApps\\",StringComparison.OrdinalIgnoreCase))
        { Process.Start(StartInfo(provider,exe,url))?.Dispose();return; }
        var installed=WindowsPackages.ListAsync().GetAwaiter().GetResult().FirstOrDefault(x=>x.Provider==provider);
        if(installed!=null) { ActivatePackage(provider,installed.AppId,url);return; }
        if(selection!=null)throw new FileNotFoundException("Selected application is missing.",selection.Path);
        Process.Start(new ProcessStartInfo(url??provider+"://") { UseShellExecute=true })?.Dispose();
    }
    static void ActivatePackage(string provider,string id,string? url)
    {
        if(provider=="claude" && url!=null)
        {
            var alias=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps","claude-desktop.exe");
            if(File.Exists(alias))
            {
                var start=new ProcessStartInfo(alias) {UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add(url);
                Process.Start(start)?.Dispose();return;
            }
        }
        WindowsPackages.Activate(id,url??"");
    }
    public static void EnsureStopped(string provider,string? root=null)
    {
        foreach(var name in AppProviders.Processes(provider))foreach(var process in Process.GetProcessesByName(name))
            using(process)
            {
                if(root==null)throw new InvalidOperationException("Close "+provider+" before applying or removing the application.");
                try { var exe=process.MainModule?.FileName;
                    if(exe==null || exe.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close "+provider+" before applying or removing the application."); }
                catch(System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Cannot check running "+provider+" process. Close it first."); }
            }
    }
}
