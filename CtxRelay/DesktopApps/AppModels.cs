namespace CtxRelay.DesktopApps;

sealed record AppRelease(string Provider, string Version, string PackageVersion,
    string Architecture, string Url, string Sha256, long Size);
sealed record InstalledApp(string Id, string Provider, string Mode, string Path,
    string Executable, string Version, string PackageFullName = "", string AppId = "", bool Managed = false,string PackageVersion="");
sealed record AppSelection(string Provider, string Mode, string Path);
sealed record AppOperation(string Provider, string Action, string State, long Received = 0,
    long Total = 0, string Error = "");

static class AppProviders
{
    public static string Check(string provider) => provider is "codex" or "claude"
        ? provider : throw new ArgumentException("Unknown desktop application.");
    public static string Identity(string provider) => Check(provider) == "codex" ? "OpenAI.Codex" : "Claude";
    public static string[] Processes(string provider) => Check(provider) == "codex" ? ["Codex", "ChatGPT"] : ["Claude"];
    public static string Architecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
        == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
}
