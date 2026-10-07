using System.Net;
using System.Text.Json.Nodes;
using CtxDeck.Sessions;

namespace CtxDeck;

static class ClaudeUsage
{
    static readonly HttpClient Http=new(new HttpClientHandler { AutomaticDecompression=DecompressionMethods.All })
        { Timeout=TimeSpan.FromSeconds(20) };
    public static async Task<UsageInfo?> FetchAsync()
    {
        if(!ClaudeCredentials.Installed)return null;
        var info=new UsageInfo { Name="Claude",Email="" };
        try
        {
            var token=await Task.Run(ClaudeCredentials.Token);
            if(token==null)
            {
                info.Error=SessionText.Pick("Sign in to Claude to read limits.","Войдите в Claude для получения лимитов.","Inicia sesión en Claude para consultar los límites.");return info;
            }
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization=new("Bearer",token);
            request.Headers.TryAddWithoutValidation("anthropic-beta","oauth-2025-04-20");
            using var response=await Http.SendAsync(request);
            if(!response.IsSuccessStatusCode)
            { info.Error="Claude usage: HTTP "+(int)response.StatusCode;return info; }
            Parse(info,await response.Content.ReadAsStringAsync());
        }
        catch(Exception e) { info.Error="Claude usage: "+e.GetType().Name; }
        return info;
    }
    internal static void Parse(UsageInfo info,string json)
    {
        var data=JsonNode.Parse(json);
        info.Primary=Window(data?["five_hour"]);info.Secondary=Window(data?["seven_day"]);
        if(info.Primary==null&&info.Secondary==null)
            info.Error=SessionText.Pick("Claude limits are unavailable for this account.","Лимиты Claude для этого аккаунта недоступны.","Los límites de Claude no están disponibles para esta cuenta.");
        else info.LimitReached=info.Left<=0;
    }
    static UsageWindow? Window(JsonNode? node)
    {
        if(node?["utilization"] is not JsonValue value || !value.TryGetValue<double>(out var used)||!double.IsFinite(used))return null;
        DateTime? reset=DateTimeOffset.TryParse(node["resets_at"]?.GetValue<string>(),out var time)?time.LocalDateTime:null;
        return new(Math.Clamp(used,0,100),reset);
    }
}
