using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CtxRelay.DesktopApps;

static class WindowsPackages
{
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivationManager
    {
        void ActivateApplication([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string arguments,uint options,out uint pid);
    }
    public static void Activate(string id,string arguments)
    {
        var type=Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"),true)!;
        var manager=(IActivationManager)Activator.CreateInstance(type)!;
        try { manager.ActivateApplication(id,arguments,0,out _); }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(manager); }
    }
    public static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
    public static async Task<string> RunAsync(string script,CancellationToken token=default)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
        // An inherited PowerShell 7 module path can load incompatible type data in Windows PowerShell.
        start.Environment["PSModulePath"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","Modules");
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;"+script)));
        using var process=Process.Start(start) ?? throw new IOException("Could not start Windows package service.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { if(!process.HasExited)process.Kill(true); } catch(InvalidOperationException) { } throw; }
        var output=await stdout;var error=await stderr;
        if(process.ExitCode!=0)throw new IOException(error.Trim().Length>0?error.Trim():"Windows package command failed: "+process.ExitCode);
        return output.Trim();
    }
    public static async Task<List<InstalledApp>> ListAsync(CancellationToken token=default)
    {
        var json=await RunAsync("@('OpenAI.Codex','Claude') | ForEach-Object { Get-AppxPackage -Name $_ } | ForEach-Object { "+
            "$p=$_;$m=Get-AppxPackageManifest -Package $p;$a=@($m.Package.Applications.Application)[0];"+
            "[pscustomobject]@{name=$p.Name;version=[string]$p.Version;path=$p.InstallLocation;full=$p.PackageFullName;"+
            "appId=$p.PackageFamilyName+'!'+$a.Id;exe=[IO.Path]::Combine($p.InstallLocation,$a.Executable)} } | ConvertTo-Json -Compress",token);
        if(json.Length==0)return [];
        using var doc=JsonDocument.Parse(json);
        var elements=doc.RootElement.ValueKind==JsonValueKind.Array?doc.RootElement.EnumerateArray().ToArray():[doc.RootElement];
        return elements.Select(e=>new InstalledApp(e.GetProperty("full").GetString()!,e.GetProperty("name").GetString()=="Claude"?"claude":"codex",
            "msix",e.GetProperty("path").GetString()!,e.GetProperty("exe").GetString()!,e.GetProperty("version").GetString()!,
            e.GetProperty("full").GetString()!,e.GetProperty("appId").GetString()!)).ToList();
    }
    public static async Task VerifyAsync(string file,AppRelease release,CancellationToken token)
    {
        AppPackages.CheckHash(file,release.Sha256);
        var identity=AppPackages.Identity(file);AppPackages.ValidateIdentity(identity,release);
        var publisher=(string)identity.Attribute("Publisher")!;
        var script="$s=Get-AuthenticodeSignature -LiteralPath "+Quote(file)+";"+
            "if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -ne "+Quote(publisher)+") { throw ('Package signature rejected: '+$s.Status+' '+$s.StatusMessage) }";
        await RunAsync(script,token);
    }
    public static Task<string> InstallAsync(string file)=>RunAsync("Add-AppxPackage -Path "+Quote(file)+" -ErrorAction Stop");
    public static Task<string> RemoveAsync(string package)=>RunAsync("Remove-AppxPackage -Package "+Quote(package)+" -ErrorAction Stop");
}
