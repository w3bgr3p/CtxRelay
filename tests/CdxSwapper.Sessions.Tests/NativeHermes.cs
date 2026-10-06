using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CdxSwapper.Sessions;

static class NativeHermes
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(),"cdx-native-hermes-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var home=Path.Combine(root,"hermes");var source=Path.Combine(root,"claude.jsonl");
            File.WriteAllText(source,JsonSerializer.Serialize(new { type="user",sessionId="fixture",cwd=root,
                message=new {content="native-csharp-hermes-marker-492 Ω"} })+"\n"+
                JsonSerializer.Serialize(new{type="assistant",message=new{content=new[]{new{type="tool_use",id="native-call",name="terminal",input=new{command="DO NOT EXECUTE"}}}}})+"\n"+
                JsonSerializer.Serialize(new{type="user",message=new{content=new[]{new{type="tool_result",tool_use_id="native-call",content="recorded-result"}}}})+"\n",new UTF8Encoding(false));
            var result = new SessionConverter(Path.Combine(root,"codex"),Path.Combine(root,"claude"),Path.Combine(root,"exports"),home)
                .Convert(new SessionItem {Provider="claude",Id="fixture",Path=source,Cwd=root,Title="Native check"},"hermes");
            var output=Path.Combine(root,"native-export.jsonl");
            var start=new ProcessStartInfo("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardOutput=true,RedirectStandardError=true};
            var script="$env:HERMES_HOME = "+SessionActions.Quote(home)+"; & hermes --profile default sessions export "+
                SessionActions.Quote(output)+" --session-id "+SessionActions.Quote(result.Id)+" --format jsonl; exit $LASTEXITCODE";
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
            finally { if(!process.HasExited) {process.Kill(true);await process.WaitForExitAsync();} }
            if(process.ExitCode!=0 || !File.Exists(output))throw new Exception("Native Hermes export failed: "+await stderr+await stdout);
            var text=File.ReadAllText(output);
            if(!text.Contains("native-csharp-hermes-marker-492") || !text.Contains(result.Id))throw new Exception("Hermes did not load the C# imported session");
            if(!text.Contains("native-call")||!text.Contains("tool_call_id")||!text.Contains("tool_calls")||text.Contains("[Historical tool call / result]"))throw new Exception("Native Hermes export lost tool linkage");
            var catalog=new SessionCatalog(Path.Combine(root,"codex"),Path.Combine(root,"claude"),hermesHome:home);
            var item=catalog.Scan().Sessions.Single(s=>s.Id==result.Id);
            var cx = new SessionConverter(Path.Combine(root,"codex"),Path.Combine(root,"claude"),Path.Combine(root,"exports"),home).Convert(item,"codex");
            if(!File.ReadAllText(cx.Path).Contains("native-csharp-hermes-marker-492"))throw new Exception("Native Hermes to Codex history missing");
            Console.WriteLine("PASS: installed Hermes initialized native schema and exported the C# imported session and history; round trip to Codex; no inference.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}
