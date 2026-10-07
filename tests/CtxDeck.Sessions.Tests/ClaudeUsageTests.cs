using CtxDeck;

static class ClaudeUsageTests
{
    public static void Run(bool live=false)
    {
        var info=new UsageInfo { Name="Claude",Email="" };
        ClaudeUsage.Parse(info,"{\"five_hour\":{\"utilization\":25,\"resets_at\":\"2026-10-06T18:00:00Z\"},\"seven_day\":{\"utilization\":80,\"resets_at\":null}}");
        if(info.Primary?.Left!=75||info.Secondary?.Left!=20||info.Left!=20||info.Primary.ResetAt==null||info.LimitReached!=false)
            throw new Exception("Claude limits must show remaining percentage and reset time.");
        var missing=new UsageInfo { Name="Claude",Email="" };
        ClaudeUsage.Parse(missing,"{\"five_hour\":null,\"seven_day\":{}}");
        if(missing.Primary!=null||missing.Secondary!=null||missing.Error==null)throw new Exception("Missing Claude limits became false 100% remaining.");
        var reached=new UsageInfo { Name="Claude",Email="" };
        ClaudeUsage.Parse(reached,"{\"five_hour\":{\"utilization\":100},\"seven_day\":null}");
        if(reached.Left!=0||reached.LimitReached!=true)throw new Exception("Claude exhausted limit not detected.");
        Console.WriteLine("PASS: Claude remaining percentages, reset time, absent windows, exhausted window.");
        if(live)
        {
            var result=ClaudeUsage.FetchAsync().GetAwaiter().GetResult();
            if(result==null)throw new Exception("Claude is not detected as installed.");
            if(result.Error!=null)throw new Exception(result.Error);
            Console.WriteLine($"LIVE Claude usage: 5h remaining={result.Primary?.Left:0}% reset={result.Primary?.ResetAt:g}; week remaining={result.Secondary?.Left:0}% reset={result.Secondary?.ResetAt:g}");
        }
    }
}
