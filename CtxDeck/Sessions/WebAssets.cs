using System.Reflection;
using System.Text;

namespace CtxDeck.Sessions;

static class WebAssets
{
    public static byte[] Read(string name)
    {
        var assembly = typeof(WebAssets).Assembly;
        var resource = "CtxDeck.Sessions." + (name == "session_import.md" ? "" : "Web.") + name.Replace('/', '.');
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    public static string ReadText(string name) => Encoding.UTF8.GetString(Read(name));
}
