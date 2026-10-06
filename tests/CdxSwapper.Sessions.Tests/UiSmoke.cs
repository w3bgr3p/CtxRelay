using System.Text.Json;
using System.Text;
using CdxSwapper;
using Microsoft.Web.WebView2.WinForms;

static class UiSmoke
{
    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.Combine(Path.GetTempPath(), "cdx-webview-test-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "codex", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "claude", "projects"));
        var source = Path.Combine(root, "codex", "sessions", "ui.jsonl");
        var rows = new List<object> { new { type = "session_meta", payload = new { id = "ui-session", cwd = root } } };
        for (int i = 0; i < 70; i++) rows.Add(new { type = "response_item", payload = new { type = "message", role = "user",
            content = new[] { new { type = "input_text", text = (i == 0 ? "early-match Ω <script>window.pwned=true</script>" : i.ToString()) + new string('я', 12000) } } } });
        File.WriteAllLines(source, rows.Select(r => JsonSerializer.Serialize(r)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "claude", "projects", "claude.jsonl"), JsonSerializer.Serialize(new {
            type = "user", sessionId = "ui-claude", cwd = root, message = new { content = "Claude UI" } }) + "\n");
        File.AppendAllLines(Path.Combine(root,"claude","projects","claude.jsonl"),new object[] {
            new { type="assistant",sessionId="ui-claude",message=new { content=new object[] {
                new { type="text",text="Readable Claude reply" },new { type="tool_use",id="toolu_ui_check",name="Read",input=new { file_path="sample.txt" } }
            } } },
            new { type="user",sessionId="ui-claude",message=new { content=new[] { new { type="tool_result",tool_use_id="toolu_ui_check",content="Readable tool output" } } } }
        }.Select(r=>JsonSerializer.Serialize(r)));
        int code = 1;
        var hermes = Path.Combine(root,"hermes"); HermesTests.Fixture(hermes);
        using (var db = CdxSwapper.Sessions.HermesStore.Open(Path.Combine(hermes,"state.db"),write:true))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO messages(session_id,role,content,timestamp) VALUES('hermes-one','user',$text,1700000004)";
            var p = cmd.Parameters.AddWithValue("$text","");
            for(int i=0;i<65;i++) { p.Value="Hermes UI entry-"+i;cmd.ExecuteNonQuery(); }
        }
        var gemini=Path.Combine(root,"gemini");AntigravityTests.Fixture(gemini);
        var window = new SessionWindow(Path.Combine(root, "codex"), Path.Combine(root, "claude"), Path.Combine(root, "data"),hermes,gemini);
        var timeout = new System.Windows.Forms.Timer { Interval = 45000 };
        timeout.Tick += (_, _) => { Console.WriteLine("FAIL: WebView smoke timed out"); window.Shutdown(); };
        timeout.Start();
        window.Shown += async (_, _) =>
        {
            try
            {
                var web = window.Controls.OfType<WebView2>().Single();
                for (int i = 0; i < 150 && web.CoreWebView2 == null; i++) await Task.Delay(100);
                var core = web.CoreWebView2 ?? throw new Exception("WebView2 failed to initialize");
                for (int i = 0; i < 150; i++)
                {
                    if (await core.ExecuteScriptAsync("typeof sessions!=='undefined' && sessions.length===8 && !indexState.busy") == "true") break;
                    await Task.Delay(100);
                }
                var oldClipboard = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                await core.ExecuteScriptAsync(Script);
                string result = "";
                for (int i = 0; i < 200; i++)
                {
                    result = await core.ExecuteScriptAsync("window.smokeResult || ''");
                    if (result != "\"\"") break;
                    await Task.Delay(100);
                }
                Console.WriteLine(JsonSerializer.Deserialize<string>(result));
                if (Clipboard.GetText() != "ui-session") throw new Exception("Native clipboard action failed");
                if (oldClipboard != null) Clipboard.SetText(oldClipboard); else Clipboard.Clear();
                if (!result.Contains("PASS:")) throw new Exception("WebView assertions failed");
                await core.ExecuteScriptAsync("$('#search').value='ПЕРВЫЙ HERMES Ω';searchContent();");
                for(int i=0;i<60;i++)
                {
                    if(await core.ExecuteScriptAsync("!!$('#matched-message')") == "true")break;
                    await Task.Delay(100);
                }
                using(var db=CdxSwapper.Sessions.HermesStore.Open(Path.Combine(hermes,"state.db"),write:true))
                using(var cmd=db.CreateCommand())
                {
                    cmd.CommandText="UPDATE messages SET content='Замена Hermes Ω' WHERE id=1";cmd.ExecuteNonQuery();
                }
                await core.ExecuteScriptAsync("""
                    window.updateSmokeResult='';(async()=>{
                      try{
                        await refresh();
                        for(let i=0;i<100;i++){const data=await api('/api/index');if(!data.index.busy)break;await new Promise(r=>setTimeout(r,100));}
                        await searchContent();
                        const h=sessions.find(s=>s.provider==='hermes');
                        if(matches[h.key] || $('#matched-message'))throw new Error('stale Hermes match remained after same-length edit');
                        $('#search').value='';await searchContent();await selectSession(h.key);
                        window.updateSmokeResult='PASS: live WebView search results refresh after same-length SQLite/WAL edit.';
                      }catch(e){window.updateSmokeResult='FAIL: '+e.message;}
                    })();
                    """);
                string updateResult="";
                for(int i=0;i<120;i++) {updateResult=await core.ExecuteScriptAsync("window.updateSmokeResult||''");if(updateResult!="\"\"")break;await Task.Delay(100);}
                Console.WriteLine(JsonSerializer.Deserialize<string>(updateResult));
                if(!updateResult.Contains("PASS:"))throw new Exception("Live search refresh failed");
                window.Close();
                if (window.IsDisposed || window.Visible) throw new Exception("Close must hide, retain window");
                window.ShowMain(); if (!window.Visible) throw new Exception("Cannot reopen main window");
                using (var png = File.Create(Path.Combine(AppContext.BaseDirectory, "sessions-ui.png")))
                    await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, png);
                Console.WriteLine("PASS: hide/reopen; screenshot=" + Path.Combine(AppContext.BaseDirectory, "sessions-ui.png"));
                code = 0;
            }
            catch (Exception e) { Console.WriteLine("FAIL: " + e); }
            finally { timeout.Stop(); window.Shutdown(); }
        };
        Application.Run(window);
        // WebView subprocesses can hold the profile briefly; the test profile is isolated in temp.
        return code;
    }
    const string Script = """
        window.smokeResult='';
        (async()=>{
          const check=(value,message)=>{if(!value)throw new Error(message);};
          try {
            check(sessions.length===8,'four providers and both Antigravity stores');
            const gemini=sessions.find(s=>s.provider==='antigravity'&&s.source==='ide'&&!s.metadata_only);
            $('#providers [data-provider="antigravity"]').click();check($('#list').querySelectorAll('.card').length===5,'Gemini filter');
            await selectSession(gemini.key);
            check($('#detail [data-action="open-client"]').textContent.includes('Antigravity IDE'),'IDE launcher label');
            check($('#detail [data-action="continue-codex"]')&&$('#detail [data-action="continue-claude"]')&&$('#detail [data-action="continue-hermes"]'),'Antigravity export targets');
            check(!$('#messages').querySelector('script')&&!window.pwned,'Antigravity content escaped');
            $('#search').value='GEMINI Ω';await searchContent();
            check($('#matched-message').textContent.includes('Gemini Ω'),'Antigravity search opens matching record');
            $('#search').value='';await searchContent();
            check(document.querySelector('h1').textContent===t('sessions'),'localized heading');
            check((await fetch('/api/sessions')).status===403,'token required');
            check((await fetch('/api/action',{method:'POST',headers:{'X-Session-Token':token,'Content-Type':'application/json'},body:JSON.stringify({key:'../../secret',action:'copy-path'})})).status===404,'catalog key required');
            $('#providers [data-provider="claude"]').click();check($('#list').querySelectorAll('.card').length===1,'provider filter');
            await selectSession(sessions.find(s=>s.provider==='claude').key);
            check($('#messages').querySelectorAll('.message.tool').length===2,'Claude tools are separate collapsed tool records');
            check(!$('#messages').textContent.includes('tool_use_id')&&!$('#messages').textContent.includes('toolu_ui_check'),'no raw Claude tool envelopes');
            check($('#messages .message.user').textContent.includes('Claude UI'),'user prose retained');
            $('#providers [data-provider="all"]').click();
            const h=sessions.find(s=>s.provider==='hermes');
            $('#providers [data-provider="hermes"]').click();check($('#list').querySelectorAll('.card').length===1,'Hermes filter');
            await selectSession(h.key);
            check($('#detail [data-action="resume"]').textContent.includes('Hermes'),'resume Hermes');
            check($('#detail [data-action="continue-codex"]') && $('#detail [data-action="continue-claude"]'),'both Hermes conversion actions');
            check(!$('#older').hidden,'Hermes paging');
            $('#search').value='ПЕРВЫЙ HERMES Ω';await searchContent();
            check($('#matched-message').textContent.includes('Первый Hermes Ω'),'Hermes old message ID navigation');
            $('#search').value='';await searchContent();$('#providers [data-provider="all"]').click();
            const s=sessions.find(s=>s.id==='ui-session');
            await selectSession(s.key);
            check($('#detail [data-action="resume"]').textContent.includes('Codex Desktop'),'imported legacy Codex opens desktop application');
            check($('#detail [data-action="continue-hermes"]')!==null,'Codex to Hermes action');
            check(!$('#older').hidden,'older records available');
            check(!$('#messages').textContent.includes('early-match'),'old result outside tail');
            await older();check($('#messages').querySelectorAll('.message').length>10,'pagination');
            $('#search').value='EARLY-MATCH Ω';await searchContent();
            check(matches[s.key].count===1,'content match');
            check($('#matched-message').textContent.includes('early-match Ω'),'opens old record');
            check($('#matched-message').querySelector('mark')!==null,'highlights Cyrillic/Greek');
            check(!window.pwned && !$('#messages').querySelector('script'),'source text escaped');
            for(const theme of ['dark','light','hyper','graphite']){
              $('#theme').value=theme;$('#theme').dispatchEvent(new Event('change'));
              check(document.documentElement.dataset.theme===theme,'theme '+theme);
            }
            $('#theme').value='dark';$('#theme').dispatchEvent(new Event('change'));
            await api('/api/action',{key:s.key,action:'copy-id'});
            await document.fonts.ready;
            check(document.fonts.check('12px "JetBrains Mono"'),'embedded font');
            check((await api('/api/session?key='+s.key)).bytesRead>0,'page contract');
            $('#search').value='';await searchContent();await selectSession(s.key);
            check($('#detail [data-action="delete"]')!==null,'delete action visible');
            const originalConfirm=window.confirm;let asked=false;
            window.confirm=()=>{asked=true;return false;};$('#detail [data-action="delete"]').click();
            check(asked&&sessions.some(x=>x.key===s.key),'cancel leaves session intact');
            window.confirm=()=>true;$('#detail [data-action="delete"]').click();
            for(let i=0;i<100&&sessions.some(x=>x.key===s.key);i++)await new Promise(r=>setTimeout(r,50));
            window.confirm=originalConfirm;check(!sessions.some(x=>x.key===s.key),'confirmed deletion updates list');
            $('#providers [data-provider="hermes"]').click();await selectSession(h.key);
            window.smokeResult='PASS: real WebView2 four-client list, both Antigravity sources/filter/search/actions, Hermes paging/search, localized heading, token/key checks, old search hit, escaping, themes, font, native clipboard.';
          }catch(error){window.smokeResult='FAIL: '+error.message;}
        })();
        """;
}
