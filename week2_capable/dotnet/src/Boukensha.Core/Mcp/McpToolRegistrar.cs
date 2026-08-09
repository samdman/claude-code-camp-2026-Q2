using System.Text.Json.Nodes;

namespace Boukensha.Core.Mcp;

public static class McpToolRegistrar
{
    public static async Task RegisterAsync(Registry registry, McpClient client, string? prefix, CancellationToken cancellationToken = default)
    {
        var tools = await client.ToolsListAsync(cancellationToken);
        foreach (var tool in tools)
        {
            if (tool is not JsonObject obj) continue;
            var rawName = obj["name"]!.GetValue<string>();
            var toolName = string.IsNullOrEmpty(prefix) ? rawName : $"{prefix}_{rawName}";

            if (registry.Registered(toolName))
            {
                throw new ArgumentException(
                    $"tool name collision: '{toolName}' from MCP server '{client.Name}' is already registered. " +
                    "Configure a different 'prefix' for this server in settings.yaml.");
            }

            var parameters = new Dictionary<string, ToolParameter>();
            if (obj["inputSchema"]?["properties"] is JsonObject properties)
            {
                foreach (var (paramName, schema) in properties)
                {
                    parameters[paramName] = new ToolParameter(
                        schema?["type"]?.GetValue<string>() ?? "string",
                        schema?["description"]?.GetValue<string>());
                }
            }

            var description = obj["description"]?.GetValue<string>() ?? string.Empty;
            registry.Tool(toolName, description, parameters, args => client.ToolsCallAsync(rawName, args));
        }
    }
}
