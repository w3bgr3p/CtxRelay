using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CtxRelay.Sessions;

namespace CtxRelay;

static class ClaudeCredentials
{
    public static bool Installed
    {
        get
        {
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if(File.Exists(Path.Combine(local,"Microsoft","WindowsApps","claude-desktop.exe")) ||
                File.Exists(Path.Combine(roaming,"npm","claude.cmd")) ||
                File.Exists(Path.Combine(local,"Programs","Claude","Claude.exe")) ||
                File.Exists(Path.Combine(local,"Claude","Claude.exe")))return true;
            var root=Path.Combine(local,"Claude");
            return Directory.Exists(root)&&Directory.EnumerateDirectories(root,"app-*").Any(p=>File.Exists(Path.Combine(p,"Claude.exe")));
        }
    }

    public static string? Token()
    {
        foreach(var sessions in DesktopSessions.Roots())
        {
            var root=Path.GetDirectoryName(sessions)!;var configPath=Path.Combine(root,"config.json");
            if(!File.Exists(configPath))continue;
            try
            {
                var config=JsonNode.Parse(File.ReadAllText(configPath))!;
                var account=config["lastKnownAccountUuid"]?.GetValue<string>();
                var encrypted=config["oauth:tokenCacheV2"]?.GetValue<string>();
                if(account==null||encrypted==null)continue;
                var state=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"Local State")))!;
                var encryptedKey=Convert.FromBase64String(state["os_crypt"]!["encrypted_key"]!.GetValue<string>());
                if(!encryptedKey.AsSpan().StartsWith("DPAPI"u8))continue;
                var key=Unprotect(encryptedKey[5..]);var bytes=Convert.FromBase64String(encrypted);
                try
                {
                    if(bytes.Length<31||!bytes.AsSpan().StartsWith("v10"u8))continue;
                    var plain=new byte[bytes.Length-31];
                    try
                    {
                        using var aes=new AesGcm(key,16);
                        aes.Decrypt(bytes.AsSpan(3,12),bytes.AsSpan(15,bytes.Length-31),bytes.AsSpan(bytes.Length-16),plain);
                        var cache=JsonNode.Parse(plain)!.AsObject();
                        var entry=cache.Where(p=>p.Key.StartsWith("acct:"+account+"|",StringComparison.Ordinal)&&p.Key.Contains("https://api.anthropic.com:")&&p.Key.Contains("user:profile"))
                            .Where(p=>p.Value?["expiresAt"]?.GetValue<long>()>DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds())
                            .OrderByDescending(p=>p.Key.Contains("user:sessions:claude_code"))
                            .ThenByDescending(p=>p.Value!["expiresAt"]!.GetValue<long>()).FirstOrDefault();
                        if(entry.Value?["token"]?.GetValue<string>() is { Length: > 0 } token)return token;
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            catch(Exception e) when(e is IOException or JsonException or CryptographicException or InvalidOperationException or FormatException) { }
        }
        var home=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
        var path=Path.Combine(home,".credentials.json");
        if(!File.Exists(path))return null;
        try
        {
            var oauth=JsonNode.Parse(File.ReadAllText(path))?["claudeAiOauth"];
            return oauth?["expiresAt"]?.GetValue<long>()>DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
                ? oauth["accessToken"]?.GetValue<string>() : null;
        }
        catch(Exception e) when(e is IOException or JsonException or InvalidOperationException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Unprotect(byte[] encrypted)
    {
        var input=new Blob { Size=encrypted.Length,Data=Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted,0,input.Data,encrypted.Length);
            if(!CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out var output))throw new CryptographicException("Claude credential decryption unavailable.");
            try { var clear=new byte[output.Size];Marshal.Copy(output.Data,clear,0,clear.Length);return clear; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
