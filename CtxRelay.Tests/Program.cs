using CtxRelay.DesktopApps;
using System.IO.Compression;
using System.Security.Cryptography;

if(args.Contains("--hold")) {Console.WriteLine("holding");await Task.Delay(30000);return;}

var passed = 0;
void Test(string name, Action body) { body(); Console.WriteLine("PASS " + name); passed++; }
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Reject(Action body) { try { body(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection"); }
var root = Path.Combine(Path.GetTempPath(), "CtxRelay-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Test("archive traversal and encoded traversal rejected", () => {
        foreach (var name in new[]{"../escape", "app/../../escape", "C:/escape", "/escape", "app/%2e%2e/%2e%2e/escape", "app/%2fescape", "app/test:stream", "app/CON"})
            Reject(() => AppPackages.EntryPath(root, name));
        Assert(AppPackages.EntryPath(root,"app/resources/%40oai/test.js").EndsWith("@oai\\test.js"));
    });
    Test("Codex selects requested architecture and bound checksum", () => {
        var json = """{"schemaVersion":3,"sources":{"windows":{"architectures":{"x64":{"version":"1.2.3.0","appVersion":"1.2.3","packageMoniker":"OpenAI.Codex_1.2.3.0_x64__abc","downloadable":true},"arm64":{"downloadable":false}}}},"derived":{"latestChecksums":{"OpenAI.Codex_1.2.3.0_x64__abc.Msix":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}} """;
        var release = AppReleases.ParseCodex(json,"x64");
        Assert(release.PackageVersion=="1.2.3.0" && release.Sha256==new string('a',64));
        Reject(() => AppReleases.ParseCodex(json,"arm64"));
    });
    Test("hash mismatch cannot pass verification", () => {
        var path=Path.Combine(root,"hash");File.WriteAllText(path,"payload");
        Reject(()=>AppPackages.CheckHash(path,new string('a',64)));
        AppPackages.CheckHash(path,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    });
    Test("swap failure restores old tree", () => {
        var current=Path.Combine(root,"current");var stage=Path.Combine(root,"stage");
        Directory.CreateDirectory(current);File.WriteAllText(Path.Combine(current,"old"),"old");
        Directory.CreateDirectory(stage);File.WriteAllText(Path.Combine(stage,"new"),"new");
        try { AppPackages.CommitDirectory(stage,current,()=>throw new IOException("injected failure")); } catch(IOException) { }
        Assert(File.Exists(Path.Combine(current,"old")) && !File.Exists(Path.Combine(current,"new")));
    });
    Test("file symlink is rejected before deleting or extracting",()=>{
        var real=Path.Combine(root,"real");File.WriteAllText(real,"keep");
        var link=Path.Combine(root,"link");File.CreateSymbolicLink(link,real);
        Reject(()=>AppPackages.NoReparsePoints(link));
    });
    Test("archive extraction preserves percent encoded node modules",()=>{
        var file=Path.Combine(root,"archive.zip");using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))
        {using var writer=new StreamWriter(zip.CreateEntry("app/node_modules/%40oai/file.js").Open());writer.Write("ok");}
        AppPackages.Extract(file,Path.Combine(root,"extracted"),default);
        Assert(File.ReadAllText(Path.Combine(root,"extracted","app","node_modules","@oai","file.js"))=="ok");
    });
    Test("Claude official host is required",()=>{
        var json="""{"sources":{"windows":{"x64":{"version":"4.29.0","url":"https://evil.test/releases/win32/x64/4.29.0/Claude.msix","sha256":"a","contentLength":10}}}}""";
        Reject(()=>AppReleases.ParseClaude(json,"x64"));
    });
    Test("Codex launch uses payload CLI without registered core",()=>{
        var payload=Path.Combine(root,"launch");Directory.CreateDirectory(Path.Combine(payload,"resources"));
        File.WriteAllText(Path.Combine(payload,"Codex.exe"),"");File.WriteAllText(Path.Combine(payload,"resources","app.asar"),"");
        File.WriteAllText(Path.Combine(payload,"resources","codex.exe"),"");
        var start=AppLauncher.StartInfo("codex",Path.Combine(payload,"Codex.exe"),"codex://threads/abc");
        Assert(!start.Environment.ContainsKey("CODEX_WINDOWS_REGISTERED_CORE") && start.Environment["CODEX_SPARKLE_ENABLED"]=="false");
        Assert(start.ArgumentList.Single()=="codex://threads/abc");
        Reject(()=>AppPackages.ValidateIdentity(System.Xml.Linq.XElement.Parse("<Identity Name='Claude' Version='1.2.3.0' ProcessorArchitecture='x64' Publisher='CN=Evil'/>"),new("claude","1.2.3","1.2.3.0","x64","","",0)));
    });
    Test("interrupted swap recovers the last good installation",()=>{
        var current=Path.Combine(root,"recover");var backup=current+".rollback";Directory.CreateDirectory(backup);
        var app=new InstalledApp("id","codex","portable",current,Path.Combine(current,"app","Codex.exe"),"1",Managed:true);
        File.WriteAllText(Path.Combine(backup,".ctxrelay-app.json"),System.Text.Json.JsonSerializer.Serialize(app));
        File.WriteAllText(Path.Combine(backup,"keep"),"last good");
        AppManager.RecoverInstallation(current,"codex");
        Assert(File.Exists(Path.Combine(current,"keep"))&&!Directory.Exists(backup));
    });
    Test("running client blocks changes without being terminated",()=>{
        var alias=Path.Combine(AppContext.BaseDirectory,"Codex.exe");File.Copy(Environment.ProcessPath!,alias,true);
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(alias,"--hold") {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true })!;
        try
        {
            Assert(process.StandardOutput.ReadLine()=="holding");
            var blocked=false;try{AppLauncher.EnsureStopped("codex",AppContext.BaseDirectory);}catch(InvalidOperationException){blocked=true;}
            Assert(blocked && !process.HasExited);
        }
        finally {if(!process.HasExited){process.Kill();process.WaitForExit();}File.Delete(alias);}
    });
    Test("restart detection recovers installation and refreshes marker version",()=>{
        var store=Path.Combine(root,"restart-store");var current=Path.Combine(root,"restart-app");var backup=current+".rollback";
        Directory.CreateDirectory(backup);Directory.CreateDirectory(Path.Combine(store,".desktop-apps"));
        var app=new InstalledApp("restart","codex","portable",current,Path.Combine(current,"Codex.exe"),"2",Managed:true);
        File.WriteAllText(Path.Combine(backup,".ctxrelay-app.json"),System.Text.Json.JsonSerializer.Serialize(app));
        File.WriteAllText(Path.Combine(backup,"Codex.exe"),"last good");
        File.WriteAllText(Path.Combine(store,".desktop-apps","installations.json"),System.Text.Json.JsonSerializer.Serialize(new[]{app with {Version="1"}}));
        AppLauncher.Initialize(store);var manager=new AppManager(store);
        manager.GetState();
        string json="";
        for(var attempt=0;attempt<200;attempt++)
        {
            json=System.Text.Json.JsonSerializer.Serialize(manager.State());
            if(json.Contains("\"busy\":false"))break;
            Thread.Sleep(50);
        }
        Assert(File.Exists(app.Executable) && json.Contains("\"Version\":\"2\""));
    });
    Test("removing an unselected portable preserves selected installation shortcut",()=>{
        var store=Path.Combine(root,"shortcut-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var a=new InstalledApp("a","codex","portable","a","a.exe","1",Managed:true);
        var b=new InstalledApp("b","codex","portable","b","b.exe","1",Managed:true);AppLauncher.Select(b);
        Assert(!AppManager.ShouldRemoveShortcut(a,new[]{b}));
        Assert(AppManager.ShouldRemoveShortcut(b,Array.Empty<InstalledApp>()));
    });
    Test("updates compare package versions for MSIX and app versions for portable",()=>{
        var release=new AppRelease("codex","26.1027.30824","26.1027.3384.0","x64","","",0);
        Assert(!AppReleases.IsNewer(release,new("id","codex","msix","","","26.1027.3384.0")));
        Assert(AppReleases.IsNewer(release,new("id","codex","portable","","","26.1027.30000")));
        Assert(!AppReleases.IsNewer(release,new("id","codex","portable","","","26.1101.1")));
    });
    Test("copy installed Claude to managed portable without changing source",()=>{
        var store=Path.Combine(root,"claude-copy-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var source=Path.Combine(root,"claude-installed");Directory.CreateDirectory(Path.Combine(source,"app","resources"));
        File.WriteAllText(Path.Combine(source,"app","Claude.exe"),"original binary");
        File.WriteAllText(Path.Combine(source,"app","resources","app.asar"),"original resources");
        var target=Path.Combine(root,"claude-portable");
        var app=new InstalledApp("claude-source","claude","msix",source,Path.Combine(source,"app","Claude.exe"),"4.29.0.0");
        var manager=new AppManager(store);manager.CopyInstalledAsync(app,target,false).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(source,"app","Claude.exe"))=="original binary");
        Assert(File.ReadAllText(Path.Combine(target,"app","Claude.exe"))=="original binary");
        Assert(File.Exists(Path.Combine(target,".ctxrelay-app.json")));
        Assert(AppLauncher.Selected("claude")?.Mode=="portable");
    });
    Test("cancel after copying never commits or selects portable installation",()=>{
        var store=Path.Combine(root,"cancel-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var target=Path.Combine(root,"cancel-target");using var cancellation=new CancellationTokenSource();
        var manager=new AppManager(store);var cancelled=false;
        try {manager.CreatePortableAsync("claude","4.29.0",target,stage=> {
            Directory.CreateDirectory(Path.Combine(stage,"app","resources"));
            File.WriteAllText(Path.Combine(stage,"app","Claude.exe"),"binary");
            File.WriteAllText(Path.Combine(stage,"app","resources","app.asar"),"resource");
            cancellation.Cancel();return Task.CompletedTask;
        },false,cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
        Assert(cancelled&&!Directory.Exists(target)&&AppLauncher.Selected("claude")==null);
    });
    Test("Codex version comes from ASAR rather than Chromium or launcher",()=>{
        var folder=Path.Combine(root,"versioned-codex");Directory.CreateDirectory(Path.Combine(folder,"resources"));
        File.WriteAllText(Path.Combine(folder,"Codex.exe"),"launcher");File.WriteAllText(Path.Combine(folder,"resources","codex.exe"),"cli");
        var package=System.Text.Encoding.UTF8.GetBytes("{\"name\":\"openai-codex-electron\",\"version\":\"26.928.21956\"}");
        var header=System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new{files=new Dictionary<string,object>{{"package.json",new{size=package.Length,offset="0"}}}}));
        using(var writer=new BinaryWriter(File.Create(Path.Combine(folder,"resources","app.asar"))))
        {writer.Write(4);writer.Write(header.Length+8);writer.Write(header.Length+4);writer.Write(header.Length);writer.Write(header);writer.Write(package);}
        var app=AppVersions.Refresh(new("app","codex","external",folder,Path.Combine(folder,"Codex.exe"),"154.0.8037.57"));
        Assert(app.Version=="26.928.21956");
        var release=new AppRelease("codex","26.1027.30824","26.1027.3384.0","x64","","",0);
        Assert(AppReleases.UpdateState(release,app)=="available");
        Assert(AppReleases.UpdateState(release,app with {Version="26.1027.30824"})=="current");
        Assert(AppReleases.UpdateState(release,app with {Version=""})=="unknown");
        Assert(AppReleases.UpdateState(release,app with {Version="26.1101.1"})=="newer");
        var store=Path.Combine(root,"folder-selection");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var connected=new AppManager(store).ConnectFolder("codex",folder);
        Assert(connected.Path==folder&&connected.Version=="26.928.21956"&&AppLauncher.Selected("codex")?.Path==connected.Executable);
    });
    Test("updating connected folder preserves original contents for rollback",()=>{
        var store=Path.Combine(root,"adopt-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var folder=Path.Combine(root,"adopt-claude");Directory.CreateDirectory(Path.Combine(folder,"resources"));
        File.WriteAllText(Path.Combine(folder,"Claude.exe"),"old binary");File.WriteAllText(Path.Combine(folder,"resources","app.asar"),"old resources");
        File.WriteAllText(Path.Combine(folder,"custom-config"),"keep");
        var original=new InstalledApp(Path.Combine(folder,"Claude.exe"),"claude","external",folder,Path.Combine(folder,"Claude.exe"),"4.28.0");
        new AppManager(store).CreatePortableAsync("claude","4.29.0",folder,stage=> {
            Directory.CreateDirectory(Path.Combine(stage,"resources"));File.WriteAllText(Path.Combine(stage,"Claude.exe"),"new binary");
            File.WriteAllText(Path.Combine(stage,"resources","app.asar"),"new resources");return Task.CompletedTask;
        },false,original:original).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(folder,"Claude.exe"))=="new binary");
        Assert(File.ReadAllText(Path.Combine(folder+".rollback","custom-config"))=="keep");
        Assert(File.ReadAllText(Path.Combine(folder+".rollback","Claude.exe"))=="old binary");
        Reject(()=>AppManager.ValidatePortableTarget(folder,"codex",original));
    });
    Test("interrupted adoption is recovered on normal restart",()=>{
        var store=Path.Combine(root,"adopt-restart-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var folder=Path.Combine(root,"adopt-restart-app");Directory.CreateDirectory(Path.Combine(folder,"resources"));
        File.WriteAllText(Path.Combine(folder,"Claude.exe"),"original");File.WriteAllText(Path.Combine(folder,"resources","app.asar"),"resources");
        var original=new InstalledApp(Path.Combine(folder,"Claude.exe"),"claude","external",folder,Path.Combine(folder,"Claude.exe"),"4.28.0");
        var manager=new AppManager(store);manager.PrepareAdoption(original);
        Directory.Move(folder,folder+".rollback");
        var recovered=AppManager.RecoverKnown(store).Single();
        Assert(recovered.Managed&&File.Exists(recovered.Executable)&&File.ReadAllText(recovered.Executable)=="original");
    });
    var inspectIndex=Array.IndexOf(args,"--inspect-folder");
    if(inspectIndex>=0)
    {
        var store=Path.Combine(root,"inspect-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var manager=new AppManager(store);var connected=manager.ConnectFolder("codex",args[inspectIndex+1]);
        await manager.RequestAsync("POST","{\"provider\":\"codex\",\"action\":\"check\"}");
        string json="";
        for(var attempt=0;attempt<1200;attempt++) {json=CtxRelay.Sessions.SessionJson.Serialize(manager.State());if(json.Contains("\"busy\":false"))break;await Task.Delay(50);}
        Directory.CreateDirectory("CtxRelay.Tests/artifacts");File.WriteAllText("CtxRelay.Tests/artifacts/apps-live-state.json",json);
        Console.WriteLine("LIVE FOLDER "+connected.Path+" version="+connected.Version);Console.WriteLine(json);
    }
    if(args.Contains("--copy-installed-claude"))
    {
        var installed=(await WindowsPackages.ListAsync()).Single(x=>x.Provider=="claude");
        var store=Path.Combine(root,"live-copy-store");Directory.CreateDirectory(store);AppLauncher.Initialize(store);
        var target=Path.Combine(root,"live-claude-portable");
        await new AppManager(store).CopyInstalledAsync(installed,target,false);
        Assert(AppLauncher.Payload("claude",target).Length>0);
        Console.WriteLine("COPIED INSTALLED CLAUDE "+target);
    }
    if(args.Contains("--live"))
    {
        foreach(var provider in new[]{"codex","claude"})
        {
            if(args.Contains("--claude-only")&&provider!="claude")continue;
            if(args.Contains("--codex-only")&&provider!="codex")continue;
            var release=await AppReleases.FetchAsync(provider,default);
            Console.WriteLine($"LIVE {provider}: {release.Version}, {release.Architecture}, {release.Size}, hash={release.Sha256.Length}");
            Assert(release.Sha256.Length==64 && release.Size>0);
            if(args.Contains("--packages"))
            {
                var file=Path.Combine(root,provider+".msix");
                var reuse=Array.IndexOf(args,"--package-dir");
                if(reuse>=0)file=Path.Combine(args[reuse+1],provider+".msix");
                else
                    using(var response=await AppReleases.Http.GetAsync(release.Url,System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                    {response.EnsureSuccessStatusCode();await using var output=File.Create(file);await response.Content.CopyToAsync(output);}
                await WindowsPackages.VerifyAsync(file,release,default);
                Console.WriteLine("VERIFIED signed package "+provider);
                var dest=Path.Combine(root,"real-"+provider+"-payload");AppPackages.Extract(file,dest,default);
                Assert(AppLauncher.Payload(provider,dest).Length>0);Console.WriteLine("VERIFIED portable payload layout "+provider);
            }
        }
        var installed=await WindowsPackages.ListAsync();
        foreach(var app in installed)Console.WriteLine($"DETECTED {app.Provider} {app.Mode} {app.Version}");
    }
    Console.WriteLine($"{passed} checks passed");
}
catch(Exception e) {Console.Error.WriteLine(e);throw;}
finally
{
    // Large signed payloads are retained for diagnosis and to avoid rescanning on repeated checks.
    if(args.Contains("--packages"))Console.WriteLine("ARTIFACTS "+root);
    else Directory.Delete(root,true);
}
