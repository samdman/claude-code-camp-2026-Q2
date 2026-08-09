using System.Text.Json;
using System.Text.Json.Nodes;

namespace Boukensha.Core.Mcp;

public static class JsonRpc
{
    public static string BuildRequest(int id, string method, JsonNode @params) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }.ToJsonString();

    public static string BuildNotification(string method, JsonNode @params) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }.ToJsonString();

    public static bool TryParseResponse(string line, int expectedId, out JsonObject? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed?["id"] is null) return false;
        if (parsed["id"]!.GetValue<int>() != expectedId) return false;

        message = parsed;
        return true;
    }

    public static string ExtractToolText(JsonObject result) =>
        string.Concat((result["content"] as JsonArray ?? [])
            .Where(block => block?["type"]?.GetValue<string>() == "text")
            .Select(block => block!["text"]!.GetValue<string>()));

    public static bool IsToolError(JsonObject result) => result["isError"]?.GetValue<bool>() == true;
}
