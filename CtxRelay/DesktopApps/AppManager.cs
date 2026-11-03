using System.Diagnostics;
using System.Text.Json;

namespace CtxRelay.DesktopApps;

sealed record PendingApp(AppRelease Release,string Package,string Mode,string Target,InstalledApp? Original=null);

sealed partial class AppManager
{
    readonly string root;
    readonly object sync=new();
    readonly Dictionary<string,AppRelease> releases=new();
    readonly Dictionary<string,AppOperation> operations=new();
    readonly Dictionary<string,string> profileBackups=new();
    readonly Dictionary<string,DateTimeOffset> checkedAt=new();
    readonly Dictionary<string,string> checkErrors=new();
    List<InstalledApp> installations=[];
    CancellationTokenSource? cancellation;
    int busy;
    bool detected;
    public AppManager(string storeRoot) { root=Path.Combine(storeRoot,".desktop-apps");Directory.CreateDirectory(root); }
    string PendingPath(string provider)=>Path.Combine(root,"pending-"+AppProviders.Check(provider)+".json");
    PendingApp? Pending(string provider)=>File.Exists(PendingPath(provider))?JsonSerializer.Deserialize<PendingApp>(File.ReadAllText(PendingPath(provider))):null;
    public object State()
    {
        lock(sync)return new { busy=busy!=0,apps=new[]{"codex","claude"}.Select(provider=>new {
            provider,architecture=AppProviders.Architecture,release=releases.GetValueOrDefault(provider),
            installations=installations.Where(x=>x.Provider==provider).Select(x=>new { x.Id,x.Provider,x.Mode,x.Path,x.Executable,x.Version,x.PackageVersion,x.AppId,x.Managed,
                updateState=AppReleases.UpdateState(releases.GetValueOrDefault(provider),x),canRollback=x.Managed&&Directory.Exists(x.Path+".rollback") }).ToArray(),selected=AppLauncher.Selected(provider),
            operation=operations.GetValueOrDefault(provider),pending=Pending(provider)?.Release,backup=profileBackups.GetValueOrDefault(provider),
            checkedAt=checkedAt.GetValueOrDefault(provider),checkError=checkErrors.GetValueOrDefault(provider),
            defaultDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","CtxRelay",provider=="codex"?"Codex":"Claude") }).ToArray() };
    }
    public object GetState()
    {
        if(!detected && Volatile.Read(ref busy)==0)Start("codex","detect",()=>DetectAsync());
        return State();
    }
    void Progress(string provider,string action,string state,long received=0,long total=0,string error="")
    { lock(sync)operations[provider]=new(provider,action,state,received,total,error); }
    void Start(string provider,string action,Func<Task> work)
    {
        if(Interlocked.CompareExchange(ref busy,1,0)!=0)throw new InvalidOperationException("Another application operation is running.");
        var source=new CancellationTokenSource();cancellation=source;Progress(provider,action,"working");
        _=Task.Run(async()=> {
            try { await work(); lock(sync)if(operations.GetValueOrDefault(provider)?.State is not ("waiting" or "current"))operations[provider]=new(provider,action,"done"); }
            catch(OperationCanceledException) { Progress(provider,action,"cancelled"); }
            catch(Exception e) { Progress(provider,action,"error",error:e.Message);Log.Write("[apps] "+Log.Error(action,e)); }
            finally { source.Dispose();Interlocked.Exchange(ref busy,0); }
        });
    }
    string KnownPath=>Path.Combine(root,"installations.json");
    List<InstalledApp> Known()=>File.Exists(KnownPath)?JsonSerializer.Deserialize<List<InstalledApp>>(File.ReadAllText(KnownPath))??[]:[];
    void Remember(InstalledApp app)
    {
        lock(sync)
        {
            var known=Known();known.RemoveAll(x=>x.Id==app.Id);known.Add(app);
            Store.AtomicWrite(KnownPath,JsonSerializer.SerializeToUtf8Bytes(known));
            installations.RemoveAll(x=>x.Id==app.Id);installations.Add(app);
        }
    }
    async Task DetectAsync()
    {
        var known=RecoverKnown(AppLauncher.DataRoot);
        var found=await WindowsPackages.ListAsync(cancellation?.Token??default);
        found.AddRange(known.Where(x=>File.Exists(x.Executable)));
        foreach(var provider in new[]{"codex","claude"})
        {
            var paths=new[]{AppLauncher.RunningExecutable(provider),AppLauncher.Selected(provider)?.Path??""};
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            paths=paths.Concat(new[]{AppLauncher.Payload(provider,Path.Combine(local,"Programs",provider=="codex"?"Codex":"Claude")),
                AppLauncher.Payload(provider,Path.Combine(local,"AnthropicClaude"))}).ToArray();
            foreach(var exe in paths.Where(p=>p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)&&File.Exists(p)))
                if(!found.Any(x=>x.Executable.Equals(exe,StringComparison.OrdinalIgnoreCase)) && !exe.Contains("\\WindowsApps\\",StringComparison.OrdinalIgnoreCase))
                    found.Add(new(exe,provider,"external",Path.GetDirectoryName(exe)!,exe,FileVersionInfo.GetVersionInfo(exe).ProductVersion??""));
        }
        found=found.Select(AppVersions.Refresh).ToList();
        lock(sync) { installations=found;detected=true; }
    }
    async Task CheckAsync(string provider)
    {
        await DetectAsync();
        try
        {
            var release=await AppReleases.FetchAsync(provider,cancellation!.Token);
            lock(sync) {releases[provider]=release;checkedAt[provider]=DateTimeOffset.UtcNow;checkErrors.Remove(provider);}
        }
        catch(Exception e){lock(sync)checkErrors[provider]=e.Message;throw;}
    }
    internal InstalledApp ConnectFolder(string provider,string folder)
    {
        AppProviders.Check(provider);var path=Path.GetFullPath(folder);var exe=AppLauncher.Payload(provider,path);
        if(exe.Length==0)throw new InvalidDataException("This folder does not contain "+provider+" desktop and its resources.");
        var packaged=installations.FirstOrDefault(x=>x.Mode=="msix"&&x.Executable.Equals(exe,StringComparison.OrdinalIgnoreCase));
        if(packaged!=null){AppLauncher.Select(packaged);return packaged;}
        if(exe.Contains("\\WindowsApps\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Use the detected MSIX installation.");
        var existing=installations.FirstOrDefault(x=>x.Executable.Equals(exe,StringComparison.OrdinalIgnoreCase));
        var app=AppVersions.Refresh(existing??new(exe,provider,"external",path,exe,""));
        Remember(app);AppLauncher.Select(app);return app;
    }
    internal static List<InstalledApp> RecoverKnown(string storeRoot)
    {
        var file=Path.Combine(storeRoot,".desktop-apps","installations.json");
        var known=File.Exists(file)?JsonSerializer.Deserialize<List<InstalledApp>>(File.ReadAllText(file))??[]:[];
        for(var i=0;i<known.Count;i++)
        {
            var app=known[i];if(!app.Managed)continue;
            if(!Directory.Exists(app.Path)&&Directory.Exists(app.Path+".rollback"))
            {
                AppLauncher.EnsureStopped(app.Provider,app.Path+".rollback");
                RecoverInstallation(app.Path,app.Provider);
            }
            if(Directory.Exists(app.Path))known[i]=ReadManaged(app.Path,app.Provider);
        }
        if(File.Exists(file))Store.AtomicWrite(file,JsonSerializer.SerializeToUtf8Bytes(known));
        return known;
    }
    static void ValidateTarget(string target,string provider)
    {
        AppPackages.NoReparsePoints(target);
        if(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())ReadManaged(target,provider);
        if(Path.GetPathRoot(target)==target)throw new InvalidDataException("Choose an application subfolder.");
    }
    static InstalledApp ReadManaged(string path,string provider)
    {
        AppPackages.NoReparsePoints(path);
        var marker=Path.Combine(path,".ctxrelay-app.json");
        var app=File.Exists(marker)?JsonSerializer.Deserialize<InstalledApp>(File.ReadAllText(marker)):null;
        if(app==null || !app.Managed || app.Provider!=provider || !Path.GetFullPath(app.Path).Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This folder is not a CtxRelay-managed installation. Choose an empty folder.");
        return app;
    }
    object Dispatch(string provider,string action,string id,string mode,bool confirmed)
    {
        InstalledApp Find()=>installations.SingleOrDefault(x=>x.Provider==provider && x.Id==id) ?? throw new ArgumentException("Installation no longer exists. Refresh the list.");
        switch(action)
        {
            case "detect": Start(provider,action,DetectAsync);break;
            case "check": Start(provider,action,()=>CheckAsync(provider));break;
            case "select": AppLauncher.Select(Find());break;
            case "launch": if(id.Length>0)AppLauncher.Select(Find());AppLauncher.Launch(provider);break;
            case "discard":
                var pending=Pending(provider);
                if(pending!=null)
                {
                    var package=Path.GetFullPath(pending.Package);AppPackages.NoReparsePoints(package);
                    if(!package.StartsWith(Path.GetFullPath(root)+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected staging path.");
                    File.Delete(package);File.Delete(PendingPath(provider));Progress(provider,action,"done");
                }
                break;
            case "folder":
                Process.Start(new ProcessStartInfo(Find().Path){UseShellExecute=true})?.Dispose();break;
            case "connect":
                using(var picker=new FolderBrowserDialog { Description=Sessions.SessionText.Pick("Choose the installed application folder","Выберите папку установленного приложения","Elige la carpeta de la aplicación instalada"),UseDescriptionForTitle=true })
                    if(picker.ShowDialog()==DialogResult.OK)
                    {
                        ConnectFolder(provider,picker.SelectedPath);Start(provider,"check",()=>CheckAsync(provider));
                    }
                break;
            case "copy":
                var source=Find();
                using(var picker=new FolderBrowserDialog { Description=Sessions.SessionText.Pick("Choose an empty portable folder","Выберите пустую папку для portable-копии","Elige una carpeta vacía para la copia portable"),UseDescriptionForTitle=true,
                    SelectedPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","CtxRelay",provider=="codex"?"Codex":"Claude") })
                {
                    if(picker.ShowDialog()!=DialogResult.OK)return State();
                    var destination=Path.GetFullPath(picker.SelectedPath);ValidateTarget(destination,provider);
                    Start(provider,action,()=>CopyInstalledAsync(source,destination));
                }
                break;
            case "install":
                if(mode is not ("portable" or "msix"))throw new ArgumentException("Unsupported installation mode.");
                var target="";
                if(mode=="portable")
                {
                    using var picker=new FolderBrowserDialog { Description=Sessions.SessionText.Pick("Choose an empty portable folder","Выберите пустую папку для portable-установки","Elige una carpeta vacía para la instalación portable"),UseDescriptionForTitle=true,
                        SelectedPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","CtxRelay",provider=="codex"?"Codex":"Claude") };
                    if(picker.ShowDialog()!=DialogResult.OK)return State();
                    target=Path.GetFullPath(picker.SelectedPath);ValidateTarget(target,provider);
                }
                Start(provider,action,()=>DownloadAsync(provider,mode,target));break;
            case "update":
                var installed=AppVersions.Refresh(Find());
                Start(provider,action,()=>DownloadAsync(provider,installed.Mode=="external"?"portable":installed.Mode,installed.Path,installed));break;
            case "apply": Start(provider,action,()=>ApplyAsync(provider));break;
            case "rollback":
                var rollback=Find();if(!rollback.Managed)throw new InvalidOperationException("Not a managed installation.");
                Start(provider,action,()=>RollbackAsync(rollback));break;
            case "remove":
                if(!confirmed)throw new ArgumentException("Confirm application removal.");
                var remove=Find();Start(provider,action,()=>RemoveAsync(remove));break;
            default: throw new ArgumentException("Unknown application action.");
        }
        return State();
    }
    public Task<object> RequestAsync(string method,string body)
    {
        if(method=="GET")return Task.FromResult(GetState());
        if(method!="POST" || body.Length is 0 or >4096)throw new ArgumentException("Invalid application request.");
        using var doc=JsonDocument.Parse(body);var request=doc.RootElement;
        string Value(string key)=>request.TryGetProperty(key,out var p)?p.GetString()??"":"";
        var provider=AppProviders.Check(Value("provider"));var action=Value("action");
        if(action=="cancel") { lock(sync)if(operations.Values.Any(o=>o.State is "downloading" or "copying"))cancellation?.Cancel(); return Task.FromResult(State()); }
        if(Volatile.Read(ref busy)!=0)throw new InvalidOperationException("Another application operation is running.");
        return Task.FromResult(Dispatch(provider,action,Value("id"),Value("mode"),request.TryGetProperty("confirmed",out var confirmed)&&confirmed.ValueKind==JsonValueKind.True));
    }
}
