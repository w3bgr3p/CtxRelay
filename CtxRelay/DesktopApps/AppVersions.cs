using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace CtxRelay.DesktopApps;

static class AppVersions
{
    public static InstalledApp Refresh(InstalledApp app)
    {
        var folder=Path.GetDirectoryName(app.Executable)!;
        var version=ReadAsar(Path.Combine(folder,"resources","app.asar"));
        if(version.Length==0 && app.Managed)version=app.Version;
        if(version.Length==0 && app.Provider=="claude")version=FileVersionInfo.GetVersionInfo(app.Executable).ProductVersion??"";
        var package=app.PackageVersion.Length>0?app.PackageVersion:app.Mode=="msix"?app.Version:"";
        foreach(var file in new[]{Path.Combine(app.Path,"AppxManifest.xml"),Path.Combine(folder,"AppxManifest.xml")})
            if(File.Exists(file))
            {
                try { package=XDocument.Load(file).Root?.Elements().FirstOrDefault(e=>e.Name.LocalName=="Identity")?.Attribute("Version")?.Value??package; }
                catch(Exception e) when(e is IOException or System.Xml.XmlException) { }
                break;
            }
        return app with {Version=version,PackageVersion=package};
    }
    internal static string ReadAsar(string file)
    {
        if(!File.Exists(file))return "";
        try
        {
            using var stream=File.OpenRead(file);using var reader=new BinaryReader(stream);
            if(reader.ReadUInt32()!=4)return "";
            var headerSize=reader.ReadUInt32();reader.ReadUInt32();var jsonSize=reader.ReadUInt32();
            if(jsonSize>32*1024*1024 || headerSize<jsonSize+8 || (long)headerSize+8>stream.Length)return "";
            using var header=JsonDocument.Parse(reader.ReadBytes((int)jsonSize));
            var entry=header.RootElement.GetProperty("files").GetProperty("package.json");
            var size=entry.GetProperty("size").GetInt32();
            if(size is <1 or >1024*1024 || !long.TryParse(entry.GetProperty("offset").GetString(),out var offset)||offset<0)return "";
            var start=checked(8L+headerSize+offset);if(start+size>stream.Length)return "";
            stream.Position=start;using var package=JsonDocument.Parse(reader.ReadBytes(size));
            var version=package.RootElement.GetProperty("version").GetString()??"";
            return Version.TryParse(version.Split('+')[0],out _)?version:"";
        }
        catch(Exception e) when(e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException) {return "";}
    }
}
