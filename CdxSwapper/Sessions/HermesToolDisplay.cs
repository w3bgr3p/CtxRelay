using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CdxSwapper.Sessions;

static class HermesToolDisplay
{
    public static (string Name, string Arguments) Call(string name, string arguments)
    {
        JsonObject args;
        try { args = JsonNode.Parse(arguments) as JsonObject ?? new() { ["input"] = arguments }; }
        catch (JsonException) { args = new() { ["input"] = arguments }; }
        if (args.ContainsKey("cdx_source_tool")) return (name, args.ToJsonString());
        var original = args.DeepClone();
        args["cdx_source_tool"] = name;
        args["cdx_source_arguments"] = original;
        if (name is "exec" or "functions.exec")
        {
            var code = args["input"]?.ToString() ?? args["code"]?.ToString() ?? arguments;
            var methods = Regex.Matches(code, @"\btools\.([A-Za-z_][A-Za-z_0-9]*)\s*\(")
                .Select(m => "tools." + m.Groups[1].Value).Distinct().Take(6).ToList();
            args["code"] = code;
            args["command"] = methods.Count > 0 ? string.Join(" / ", methods) : "JavaScript orchestration (" + name + ")";
            name = "execute_code";
        }
        else if (name is "Bash" or "exec_command" or "functions.exec_command")
        {
            args["command"] ??= args["cmd"]?.DeepClone();
            name = "terminal";
        }
        else args["context"] = "Arguments:\n" + original.ToJsonString(new() { WriteIndented = true });
        return (name, args.ToJsonString());
    }

    public static string Result(string arguments, string output)
    {
        var args = JsonNode.Parse(arguments)!.AsObject();
        var original = args["cdx_source_arguments"] ?? args;
        var input = original is JsonObject obj && obj.Count == 1 && obj["input"] != null
            ? obj["input"]!.ToString() : original.ToJsonString(new() { WriteIndented = true });
        var detail = "Source tool: " + args["cdx_source_tool"] + "\n\nArguments:\n" + input + "\n\nResult:\n" + output;
        return new JsonObject { ["output"] = detail, ["context"] = detail, ["cdx_source_result"] = output }.ToJsonString();
    }
}
