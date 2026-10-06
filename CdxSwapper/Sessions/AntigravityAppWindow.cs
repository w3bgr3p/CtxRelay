using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CdxSwapper.Sessions;

static class AntigravityAppWindow
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
    public static Task OpenAsync(string id,string title)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try { Open(id,title);completion.SetResult(); }
            catch(Exception error) { completion.SetException(error); }
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    static bool IsChat(AutomationElement root,string id)
    {
        foreach(AutomationElement element in root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document)))
            if(!element.Current.IsOffscreen && element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern) &&
                Uri.TryCreate(((ValuePattern)pattern).Current.Value,UriKind.Absolute,out var url) && url.AbsolutePath=="/c/"+id) return true;
        return false;
    }
    static bool InvokeLink(AutomationElement root,string id)
    {
        foreach(AutomationElement element in root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Hyperlink)))
            if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var value) &&
                Uri.TryCreate(((ValuePattern)value).Current.Value,UriKind.Absolute,out var url) && url.AbsolutePath=="/c/"+id &&
                element.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke))
            { ((InvokePattern)invoke).Invoke();return true; }
        return false;
    }
    static void Open(string id,string title)
    {
        var watch=Stopwatch.StartNew();bool historyOpened=false,searchSet=false;
        while(watch.Elapsed<TimeSpan.FromSeconds(35))
        {
            try
            {
            var window=IntPtr.Zero;
            foreach(var process in Process.GetProcessesByName("Antigravity"))
                using(process) if(process.MainWindowHandle!=IntPtr.Zero) { window=process.MainWindowHandle;break; }
            if(window==IntPtr.Zero) { Thread.Sleep(200);continue; }
            ShowWindow(window,9);SetForegroundWindow(window);
            var root=AutomationElement.FromHandle(window);
            if(IsChat(root,id))return;
            if(InvokeLink(root,id)) { Thread.Sleep(200);continue; }
            if(!historyOpened)
            {
                var history=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"Conversation History"));
                if(history!=null && history.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke))
                { ((InvokePattern)invoke).Invoke();historyOpened=true; }
            }
            if(historyOpened && !searchSet && title.Length>0)
            {
                var search=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"Search conversations..."));
                if(search!=null && search.TryGetCurrentPattern(ValuePattern.Pattern,out var value))
                { ((ValuePattern)value).SetValue(title);searchSet=true; }
            }
            Thread.Sleep(250);
            }
            catch(ElementNotAvailableException) { Thread.Sleep(250); }
        }
        throw new InvalidOperationException("Antigravity did not display the requested chat. No message was sent.");
    }
}
