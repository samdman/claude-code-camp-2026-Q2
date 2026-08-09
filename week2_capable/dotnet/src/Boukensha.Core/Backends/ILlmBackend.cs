using System.Text.Json.Nodes;

namespace Boukensha.Core.Backends;

public sealed record ParsedResponse(string StopReason, IReadOnlyList<ContentBlock> Content);

public interface ILlmBackend
{
    string Model { get; }
    int ContextWindow { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    string Url { get; }
    JsonArray ToMessages(IReadOnlyList<Message> messages);
    JsonArray ToTools(IReadOnlyDictionary<string, ToolDefinition> tools);
    JsonObject ToPayload(Context context, int maxOutputTokens, JsonArray? toolsOverride = null);
    ParsedResponse ParseResponse(JsonNode response);
    double? EstimateCost(int inputTokens, int outputTokens);
}
