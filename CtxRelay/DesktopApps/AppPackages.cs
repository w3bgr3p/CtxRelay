using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CtxRelay.DesktopApps;

static class AppPackages
{
    public static string EntryPath(string root, string entry)
    {
        var normalized = entry.Replace('\\','/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) throw new InvalidDataException("Absolute archive path.");
        var parts = normalized.Split('/',StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if(parts.Length==0 || parts.Any(p=>p is "." or ".." || p.IndexOfAny(['/', '\\', ':'])>=0 ||
            p.Any(c=>char.IsControl(c)||"<>\"|?*".Contains(c)) || p.EndsWith('.') || p.EndsWith(' ') ||
            Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe archive path: " + entry);
        var path=Path.GetFullPath(Path.Combine([root,..parts]));
        if(!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive path escaped destination.");
        return path;
    }
    public static void CheckHash(string file, string expected)
    {
        if(!Regex.IsMatch(expected,"^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Missing SHA-256 checksum.");
        using var stream=File.OpenRead(file);
        if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 checksum mismatch. Download discarded.");
    }
    public static void NoReparsePoints(string path)
    {
        for(var dir=new DirectoryInfo(Path.GetFullPath(path));dir!=null;dir=dir.Parent)
            if((Directory.Exists(dir.FullName)||File.Exists(dir.FullName)) && (File.GetAttributes(dir.FullName) & FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Installation path contains a reparse point.");
    }
    public static void Extract(string file, string destination, CancellationToken token)
    {
        NoReparsePoints(destination);
        using var archive=ZipFile.OpenRead(file);
        long expanded=0;
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var target=EntryPath(destination,entry.FullName);
            if(!paths.Add(target)) throw new InvalidDataException("Duplicate archive path.");
            if(((entry.ExternalAttributes>>16)&0xF000)==0xA000) throw new InvalidDataException("Archive symlink.");
            expanded=checked(expanded+entry.Length);
            if(expanded>12L*1024*1024*1024) throw new InvalidDataException("Archive too large.");
            NoReparsePoints(target);
            if(entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input=entry.Open(); using var output=new FileStream(target,FileMode.CreateNew);
            input.CopyTo(output);
        }
    }
    public static XElement Identity(string file)
    {
        using var archive=ZipFile.OpenRead(file);
        var entry=archive.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException("Missing AppxManifest.xml.");
        using var input=entry.Open();
        var settings=new System.Xml.XmlReaderSettings { DtdProcessing=System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument=1024*1024 };
        using var reader=System.Xml.XmlReader.Create(input,settings);
        return XDocument.Load(reader).Root?.Elements().Single(e=>e.Name.LocalName=="Identity")
            ?? throw new InvalidDataException("Missing package identity.");
    }
    public static void ValidateIdentity(XElement identity, AppRelease release)
    {
        if((string?)identity.Attribute("Name")!=AppProviders.Identity(release.Provider) ||
            (string?)identity.Attribute("ProcessorArchitecture")!=release.Architecture ||
            !Version.TryParse((string?)identity.Attribute("Version"),out var actual) ||
            !Version.TryParse(release.PackageVersion,out var expected) || Normalize(actual)!=Normalize(expected))
            throw new InvalidDataException("Unexpected package identity, version or architecture.");
        var publisher=(string?)identity.Attribute("Publisher") ?? "";
        if(release.Provider=="codex" ? !publisher.Equals("CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",StringComparison.OrdinalIgnoreCase)
            : !publisher.StartsWith("CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\",",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected package publisher.");
    }
    static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
    public static void CommitDirectory(string staged, string target, Action? verify = null)
    {
        NoReparsePoints(staged); NoReparsePoints(target);
        var backup=target+".rollback";
        if(Directory.Exists(backup)) throw new IOException("Previous rollback exists: " + backup);
        var existed=Directory.Exists(target);
        if(existed) Directory.Move(target,backup);
        try { Directory.Move(staged,target); verify?.Invoke(); }
        catch { if(Directory.Exists(target)) Directory.Move(target,staged); if(existed) Directory.Move(backup,target); throw; }
        // Preserve the last good version for explicit rollback; never remove it in the swap transaction.
    }
}
