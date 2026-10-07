using System.Diagnostics;
using System.Windows.Automation;

static class AntigravityWindowCheck
{
    public static void Run(string id)
    {
        for(int attempt=0;;attempt++)
            try { Check(id);return; }
            catch(ElementNotAvailableException) when(attempt<5) { Thread.Sleep(300); }
    }
    static void Check(string id)
    {
        using var process=Process.GetProcessesByName("Antigravity").First(p=>p.MainWindowHandle!=IntPtr.Zero);
        var root=AutomationElement.FromHandle(process.MainWindowHandle);
        Thread.Sleep(400);
        var document=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document))
            .Cast<AutomationElement>().Single(e=>e.TryGetCurrentPattern(ValuePattern.Pattern,out var p) &&
                Uri.TryCreate(((ValuePattern)p).Current.Value,UriKind.Absolute,out var url) && url.AbsolutePath=="/c/"+id && !e.Current.IsOffscreen);
        void Expand(string prefix)
        {
            bool found=false;
            foreach(AutomationElement element in document.FindAll(TreeScope.Descendants,Condition.TrueCondition))
                if(element.Current.Name.StartsWith(prefix) && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var pattern))
                { ((ExpandCollapsePattern)pattern).Expand();Thread.Sleep(200);found=true; }
            if(!found)throw new Exception("Visible Antigravity tool section missing: "+prefix);
        }
        Expand("Worked for");Expand("Explored");
        var tool=document.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"MCP Tool: codex / Read"));
        if(tool==null)throw new Exception("Native tool was not rendered in Antigravity");
        ((InvokePattern)tool.GetCurrentPattern(InvokePattern.Pattern)).Invoke();Thread.Sleep(200);
        var text=string.Join("\n",document.FindAll(TreeScope.Descendants,Condition.TrueCondition).Cast<AutomationElement>().Select(e=>e.Current.Name));
        foreach(var marker in new[] {"IDE native import marker 927","Imported answer marker 927","sample.txt","IDE native tool result marker 927"})
            if(!text.Contains(marker))throw new Exception("Visible imported content missing: "+marker);
        var historical=document.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"MCP Tool: codex / historical_context"));
        if(historical==null)throw new Exception("Historical tool was not rendered as a native tool");
        ((InvokePattern)historical.GetCurrentPattern(InvokePattern.Pattern)).Invoke();Thread.Sleep(200);
        text=string.Join("\n",document.FindAll(TreeScope.Descendants,Condition.TrueCondition).Cast<AutomationElement>().Select(e=>e.Current.Name));
        if(!text.Contains("historical-marker-927.exe") || text.Contains("Failed to render LaTeX"))throw new Exception("Historical PowerShell tool rendering failed");
        Console.WriteLine("PASS: actual Antigravity window shows matching chat URL, user/assistant messages, native tool name, arguments and output.");
    }
}
