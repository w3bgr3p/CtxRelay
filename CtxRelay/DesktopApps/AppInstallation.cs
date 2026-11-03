using System.Net.Http;
using System.Text.Json;

namespace CtxRelay.DesktopApps;

sealed partial class AppManager
{
    async Task DownloadAsync(string provider,string mode,string target,InstalledApp? updating=null)
    {
        if(Pending(provider)!=null)throw new InvalidOperationException("Apply or discard the staged update first.");
        var token=cancellation!.Token;
        var release=await AppReleases.FetchAsync(provider,token);lock(sync)releases[provider]=release;
        if(updating!=null)
        {
            var state=AppReleases.UpdateState(release,updating);
            if(state=="unknown")throw new InvalidDataException("Cannot compare the installed version. Reconnect the application folder.");
            if(state!="available") {Progress(provider,"update","current");return;}
        }
        var file=Path.Combine(root,provider+"-"+Guid.NewGuid().ToString("N")+".msix");
        var saved=false;
        try
        {
            Progress(provider,"install","downloading",total:release.Size);
            using(var response=await AppReleases.Http.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,token))
            {
                response.EnsureSuccessStatusCode();
                await using var source=await response.Content.ReadAsStreamAsync(token);
                await using var destination=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true);
                var buffer=new byte[131072];long received=0;var report=System.Diagnostics.Stopwatch.StartNew();
                while(true)
                {
                    using var idle=CancellationTokenSource.CreateLinkedTokenSource(token);idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var count=await source.ReadAsync(buffer,idle.Token);if(count==0)break;
                    await destination.WriteAsync(buffer.AsMemory(0,count),token);received+=count;
                    if(received>4L*1024*1024*1024 || release.Size>0 && received>release.Size)throw new InvalidDataException("Unexpected download size.");
                    if(report.ElapsedMilliseconds>200) { Progress(provider,"install","downloading",received,release.Size);report.Restart(); }
                }
                if(release.Size>0 && received!=release.Size)throw new InvalidDataException("Incomplete package download.");
            }
            token.ThrowIfCancellationRequested();Progress(provider,"install","verifying");
            await WindowsPackages.VerifyAsync(file,release,token);
            Store.AtomicWrite(PendingPath(provider),JsonSerializer.SerializeToUtf8Bytes(new PendingApp(release,file,mode,target,updating?.Mode=="external"?updating:null)));
            saved=true;
            await ApplyAsync(provider);
        }
        finally { if(!saved && File.Exists(file))File.Delete(file); }
    }
    Task InstallPortableAsync(PendingApp pending) => CreatePortableAsync(pending.Release.Provider,pending.Release.Version,pending.Target,
        stage=>Task.Run(()=>AppPackages.Extract(pending.Package,stage,CancellationToken.None)),original:pending.Original);

    internal Task CopyInstalledAsync(InstalledApp source,string target,bool createShortcut=true)
    {
        var origin=Path.GetFullPath(source.Path);
        var destination=Path.GetFullPath(target);
        if(destination.Equals(origin,StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(origin.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith(destination.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source and portable destination must be separate folders.");
        if(AppLauncher.Payload(source.Provider,origin).Length==0)throw new InvalidDataException("Source desktop payload is missing.");
        var token=cancellation?.Token??default;
        return CreatePortableAsync(source.Provider,source.Version,destination,stage=>Task.Run(()=> {
            Progress(source.Provider,"copy","copying");CopyProfile(origin,stage,token);
        }),createShortcut,token);
    }
    internal async Task CreatePortableAsync(string provider,string version,string destination,Func<string,Task> populate,bool createShortcut=true,CancellationToken token=default,InstalledApp? original=null)
    {
        var target=Path.GetFullPath(destination);
        RecoverInstallation(target,provider);
        ValidatePortableTarget(target,provider,original);
        var stage=target+".staging-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            Progress(provider,"apply","extracting");
            await populate(stage);
            token.ThrowIfCancellationRequested();
            var payload=AppLauncher.Payload(provider,stage);
            if(payload.Length==0)throw new InvalidDataException("The folder does not contain a supported desktop payload.");
            var relative=Path.GetRelativePath(stage,payload);
            var exe=Path.Combine(target,relative);
            var app=new InstalledApp(exe,provider,"portable",target,exe,version,Managed:true);
            File.WriteAllBytes(Path.Combine(stage,".ctxrelay-app.json"),JsonSerializer.SerializeToUtf8Bytes(app));
            AppLauncher.EnsureStopped(provider,target);ValidatePortableTarget(target,provider,original);
            if(Directory.Exists(target+".rollback"))
            {
                // A rollback tree records the original target path in its marker.
                ValidateBackup(target,provider);SafeDeleteTree(target+".rollback");
            }
            if(Directory.Exists(target)&&!Directory.EnumerateFileSystemEntries(target).Any())Directory.Delete(target);
            Progress(provider,"apply","installing");
            token.ThrowIfCancellationRequested();
            if(original!=null)PrepareAdoption(original);
            AppPackages.CommitDirectory(stage,target,()=> { if(AppLauncher.Payload(provider,target).Length==0)throw new IOException("Installed payload failed validation."); });
            Remember(app);AppLauncher.Select(app);
            try { if(createShortcut)await ShortcutAsync(provider,false); }
            catch(Exception e) { Log.Write("[apps] shortcut: "+e.Message); }
        }
        finally { if(Directory.Exists(stage))SafeDeleteTree(stage); }
    }
    internal static void ValidatePortableTarget(string target,string provider,InstalledApp? original)
    {
        if(original==null || File.Exists(Path.Combine(target,".ctxrelay-app.json"))) {ValidateTarget(target,provider);return;}
        AppPackages.NoReparsePoints(target);
        if(original.Provider!=provider || !Path.GetFullPath(original.Path).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase) ||
            !AppLauncher.Payload(provider,target).Equals(original.Executable,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The connected installation changed. Reconnect the application folder.");
        ValidateTree(target);
    }
    internal void PrepareAdoption(InstalledApp original)
    {
        ValidatePortableTarget(original.Path,original.Provider,original);
        var adopted=original with {Managed=true,Mode="portable"};
        Store.AtomicWrite(Path.Combine(original.Path,".ctxrelay-app.json"),JsonSerializer.SerializeToUtf8Bytes(adopted));
        // Recovery must know this is managed before the first rename can remove its executable.
        Remember(adopted);
    }
    static void ValidateBackup(string target,string provider)
    {
        var backup=target+".rollback";AppPackages.NoReparsePoints(backup);
        var marker=Path.Combine(backup,".ctxrelay-app.json");
        var app=File.Exists(marker)?JsonSerializer.Deserialize<InstalledApp>(File.ReadAllText(marker)):null;
        if(app==null || !app.Managed || app.Provider!=provider || !app.Path.Equals(target,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unknown rollback directory. Preserve it and choose another destination.");
    }
    internal static void RecoverInstallation(string target,string provider)
    {
        AppPackages.NoReparsePoints(target);
        if(!Directory.Exists(target)&&Directory.Exists(target+".rollback"))
        {ValidateBackup(target,provider);Directory.Move(target+".rollback",target);}
    }
    internal static void SafeDeleteTree(string path)
    {
        ValidateTree(path);
        Directory.Delete(path,true);
    }
    static void ValidateTree(string path)
    {
        AppPackages.NoReparsePoints(path);
        foreach(var entry in Directory.EnumerateFileSystemEntries(path))
        {
            AppPackages.NoReparsePoints(entry);
            if(Directory.Exists(entry))ValidateTree(entry);
        }
    }
    static void CopyProfile(string source,string destination,CancellationToken token=default)
    {
        AppPackages.NoReparsePoints(source);Directory.CreateDirectory(destination);
        foreach(var entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            AppPackages.NoReparsePoints(entry);
            var target=Path.Combine(destination,Path.GetFileName(entry));
            if(Directory.Exists(entry))CopyProfile(entry,target,token);
            else
            {
                using var input=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read);
                using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write);
                var buffer=new byte[131072];
                while(true) {token.ThrowIfCancellationRequested();var count=input.Read(buffer);if(count==0)break;output.Write(buffer,0,count);}
            }
        }
    }
    async Task RollbackAsync(InstalledApp app)
    {
        ReadManaged(app.Path,app.Provider);ValidateBackup(app.Path,app.Provider);AppLauncher.EnsureStopped(app.Provider,app.Path);
        var temporary=app.Path+".swap-"+Guid.NewGuid().ToString("N");
        Directory.Move(app.Path,temporary);
        try { Directory.Move(app.Path+".rollback",app.Path); }
        catch { Directory.Move(temporary,app.Path);throw; }
        // If this last rename fails, leave the previous current version at its named temporary path.
        Directory.Move(temporary,app.Path+".rollback");
        var restored=ReadManaged(app.Path,app.Provider);Remember(restored);AppLauncher.Select(restored);await DetectAsync();
    }
    async Task RemoveAsync(InstalledApp app)
    {
        if(app.Mode=="external")throw new InvalidOperationException("Use the original installer to remove an external application.");
        AppLauncher.EnsureStopped(app.Provider,app.Mode=="portable"?app.Path:null);
        if(app.Mode=="portable")
        {
            ReadManaged(app.Path,app.Provider);
            if(Directory.Exists(app.Path+".rollback"))ValidateBackup(app.Path,app.Provider);
            SafeDeleteTree(app.Path);
            if(Directory.Exists(app.Path+".rollback"))SafeDeleteTree(app.Path+".rollback");
            if(ShouldRemoveShortcut(app,Known().Where(x=>x.Id!=app.Id&&File.Exists(x.Executable))))await ShortcutAsync(app.Provider,true);
        }
        else
        {
            var family=app.AppId.Split('!')[0];
            if(family.Length==0 || family.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidDataException("Invalid package family.");
            var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages",family);
            if(Directory.Exists(profile))
            {
                var backup=Path.Combine(root,"backups",app.Provider+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
                await Task.Run(()=>CopyProfile(profile,backup));
                Log.Write("[apps] preserved profile: "+backup);
                lock(sync)profileBackups[app.Provider]=backup;
            }
            AppLauncher.EnsureStopped(app.Provider);
            await WindowsPackages.RemoveAsync(app.PackageFullName);
        }
        lock(sync)
        {
            var known=Known();known.RemoveAll(x=>x.Id==app.Id);Store.AtomicWrite(KnownPath,JsonSerializer.SerializeToUtf8Bytes(known));
            var selected=AppLauncher.Selected(app.Provider);
            if(selected?.Path==(app.Mode=="msix"?app.AppId:app.Executable))
                Store.AtomicWrite(AppLauncher.PreferencesPath,JsonSerializer.SerializeToUtf8Bytes(AppLauncher.Selections().Where(x=>x.Provider!=app.Provider)));
        }
        await DetectAsync();
        if(AppLauncher.Selected(app.Provider)==null && installations.FirstOrDefault(x=>x.Provider==app.Provider) is {} alternative)
            AppLauncher.Select(alternative);
    }
    Task<string> ShortcutAsync(string provider,bool remove)
    {
        var name=(provider=="codex"?"Codex":"Claude")+" (CtxRelay).lnk";
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),name);
        var q=WindowsPackages.Quote(path);
        if(remove)return WindowsPackages.RunAsync("if(Test-Path -LiteralPath "+q+"){Remove-Item -LiteralPath "+q+"}");
        return WindowsPackages.RunAsync("$s=(New-Object -ComObject WScript.Shell).CreateShortcut("+q+");$s.TargetPath="+
            WindowsPackages.Quote(Environment.ProcessPath!)+";$s.Arguments='--launch-app "+provider+"';$s.Save()");
    }
    internal static bool ShouldRemoveShortcut(InstalledApp removed,IEnumerable<InstalledApp> remaining)
        => !remaining.Any(x=>x.Provider==removed.Provider&&x.Mode=="portable");
    async Task ApplyAsync(string provider)
    {
        var pending=Pending(provider)??throw new InvalidOperationException("No staged package.");
        var target=pending.Target;
        try { AppLauncher.EnsureStopped(provider,pending.Mode=="portable"?target:null); }
        catch(InvalidOperationException) { Progress(provider,"apply","waiting");return; }
        Progress(provider,"apply","verifying");
        await WindowsPackages.VerifyAsync(pending.Package,pending.Release,CancellationToken.None);
        if(pending.Mode=="portable")await InstallPortableAsync(pending);
        else
        {
            AppLauncher.EnsureStopped(provider);Progress(provider,"apply","installing");
            await WindowsPackages.InstallAsync(pending.Package);
            await DetectAsync();
            var installed=installations.FirstOrDefault(x=>x.Provider==provider&&x.Mode=="msix")??throw new IOException("Installed package not found after installation.");
            AppLauncher.Select(installed);
        }
        File.Delete(PendingPath(provider));File.Delete(pending.Package);await DetectAsync();
    }
}
