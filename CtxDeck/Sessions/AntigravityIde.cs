using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CtxDeck.Sessions;

static class AntigravityIde
{
    public static async Task<JsonElement> OpenAsync(string id,string path,bool import,string title="",bool useIde=true)
    {
        if(!Guid.TryParse(id,out _))throw new ArgumentException("Invalid Antigravity IDE session ID.");
        var source=useIde?"ide":"app";
        var exe=AntigravityStore.Executable(new SessionItem { Source=source });
        var running=false;
        foreach(var process in Process.GetProcessesByName(useIde?"Antigravity IDE":"Antigravity"))
            using(process)try { running|=string.Equals(process.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase); } catch(System.ComponentModel.Win32Exception) { }
        if(!running)
        {
            var launch=new ProcessStartInfo(exe) { UseShellExecute=true };
            if(useIde)launch.ArgumentList.Add("--reuse-window");
            Process.Start(launch)?.Dispose();
        }
        using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(30) };
        string? endpoint=null;
        for(int attempt=0;attempt<30 && endpoint==null;attempt++)
        {
            var start=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-Command");
            var appDir=useIde?"antigravity-ide":"antigravity";
            start.ArgumentList.Add("$p=Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*language_server*' -and $_.CommandLine -match '--app_data_dir "+appDir+"(?:\\s|$)' -and $_.CommandLine -notmatch '--enable_lsp' } | Select-Object -First 1; if($p){ @{ command=$p.CommandLine; ports=@(Get-NetTCPConnection -OwningProcess $p.ProcessId -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort) } | ConvertTo-Json -Compress }");
            using var process=Process.Start(start)!;
            var output=await process.StandardOutput.ReadToEndAsync();await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
            if(output.Trim().Length>0)
            {
                using var doc=JsonDocument.Parse(output);var token=Regex.Match(doc.RootElement.Str("command"),@"--csrf_token\s+(\S+)").Groups[1].Value;
                client.DefaultRequestHeaders.Remove("x-codeium-csrf-token");client.DefaultRequestHeaders.Add("x-codeium-csrf-token",token);
                foreach(var port in doc.RootElement.Get("ports").EnumerateArray())
                {
                    var url=$"http://127.0.0.1:{port.GetInt32()}/exa.language_server_pb.LanguageServerService/";
                    try { using var response=await client.PostAsJsonAsync(url+"LoadSharedTrajectory",new { path="cdx-endpoint-probe" });
                        var body=await response.Content.ReadAsStringAsync();if(body.TrimStart().StartsWith('{')) { endpoint=url;break; } }
                    catch(HttpRequestException) { }
                    catch(TaskCanceledException) { }
                }
            }
            if(endpoint==null)await Task.Delay(1000);
        }
        if(endpoint==null)throw new InvalidOperationException("Antigravity IDE language server was not ready.");
        async Task<JsonElement> Request(string method,object data)
        {
            using var response=await client.PostAsJsonAsync(endpoint+method,data);var body=await response.Content.ReadAsStringAsync();
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Antigravity IDE {method}: {body}");
            using var doc=JsonDocument.Parse(body);return doc.RootElement.Clone();
        }
        if(import)
        {
            var imported=await Request("LoadSharedTrajectory",new { path });
            if(imported.Str("cascadeId")!=id)throw new InvalidOperationException("Antigravity IDE imported a different conversation.");
        }
        await Request("LoadTrajectory",new { cascadeId=id });
        var read=await Request("GetCascadeTrajectory",new { cascadeId=id,trajectoryVerbosity=2 });
        var trajectory=read.Get("trajectory");
        if(trajectory.Str("cascadeId")!=id || trajectory.Get("steps").ValueKind!=JsonValueKind.Array || trajectory.Get("steps").GetArrayLength()==0)
            throw new InvalidOperationException("Antigravity IDE returned no imported conversation history.");
        if(import && title.Length>0)await Request("UpdateConversationAnnotations",new { cascadeId=id,annotations=new { title },mergeAnnotations=true });
        if(useIde)await Request("SmartFocusConversation",new { cascadeId=id });
        else await AntigravityAppWindow.OpenAsync(id,title);
        return read;
    }
}
