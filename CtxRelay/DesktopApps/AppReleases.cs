using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CtxRelay.DesktopApps;

static class AppReleases
{
    internal static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var client=new HttpClient { Timeout=TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CtxRelay/1.0 desktop-manager");
        return client;
    }
    static string Str(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()!:"";
    public static AppRelease ParseCodex(string json,string architecture)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        var windows=root.GetProperty("sources").GetProperty("windows");
        var source=windows;
        if(windows.TryGetProperty("architectures",out var architectures) && !architectures.TryGetProperty(architecture,out source))
            throw new InvalidDataException("Codex is unavailable for " + architecture);
        if(source.TryGetProperty("downloadable",out var available) && available.ValueKind==JsonValueKind.False)
            throw new InvalidDataException("Codex is unavailable for " + architecture);
        var moniker=Str(source,"packageMoniker");
        if(!Regex.IsMatch(moniker,@"^OpenAI\.Codex_[\d.]+_"+architecture+@"__[a-z0-9]+$"))
            throw new InvalidDataException("Invalid Codex package moniker.");
        var checks=root.GetProperty("derived").GetProperty("latestChecksums");
        var hash=checks.EnumerateObject().SingleOrDefault(p=>p.Name.Equals(moniker+".msix",StringComparison.OrdinalIgnoreCase)).Value;
        if(hash.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Missing Codex package checksum.");
        var version=Str(source,"appVersion");if(version.Length==0)version=Str(root,"codexVersion");
        return new("codex",version,Str(source,"version"),architecture,
            "https://codexapp.agentsmirror.com/latest/win-"+architecture,hash.GetString()!,
            source.TryGetProperty("contentLength",out var size)?size.GetInt64():0);
    }
    public static AppRelease ParseClaude(string json,string architecture)
    {
        using var doc=JsonDocument.Parse(json);
        var source=doc.RootElement.GetProperty("sources").GetProperty("windows").GetProperty(architecture);
        var url=Str(source,"url");var uri=new Uri(url);
        if(uri.Scheme!="https" || uri.Host!="downloads.claude.ai" || !uri.AbsolutePath.StartsWith("/releases/win32/"+architecture+"/",StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected Claude download URL.");
        var version=Str(source,"version");
        return new("claude",version,version,architecture,url,Str(source,"sha256"),source.GetProperty("contentLength").GetInt64());
    }
    public static async Task<AppRelease> FetchAsync(string provider,CancellationToken token)
    {
        var origin=AppProviders.Check(provider)=="codex"?"codexapp":"claudeapp";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var json=await Http.GetStringAsync("https://"+origin+".agentsmirror.com/latest/manifest",deadline.Token);
        return provider=="codex"?ParseCodex(json,AppProviders.Architecture):ParseClaude(json,AppProviders.Architecture);
    }
    public static bool IsNewer(AppRelease release,InstalledApp installed)
        =>UpdateState(release,installed)=="available";
    public static string UpdateState(AppRelease? release,InstalledApp? installed)
    {
        if(installed==null)return "missing";
        if(release==null)return "unchecked";
        static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
        var usePackage=installed.Mode=="msix" || installed.Version.Length==0&&installed.PackageVersion.Length>0;
        var available=usePackage?release.PackageVersion:release.Version;
        var actual=usePackage&&installed.PackageVersion.Length>0?installed.PackageVersion:installed.Version;
        if(!Version.TryParse(available,out var latest)||!Version.TryParse(actual.Split('+')[0],out var current))return "unknown";
        var comparison=Normalize(latest).CompareTo(Normalize(current));
        return comparison>0?"available":comparison==0?"current":"newer";
    }
}
