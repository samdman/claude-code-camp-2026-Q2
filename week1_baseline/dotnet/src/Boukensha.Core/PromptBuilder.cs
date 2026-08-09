using System.Text.Json.Nodes;
using Boukensha.Core.Backends;

namespace Boukensha.Core;

public sealed class PromptBuilder(Context context, ILlmBackend backend)
{
    public ILlmBackend Backend { get; } = backend;

    public JsonArray ToMessages() => Backend.ToMessages(context.Messages);

    public JsonArray ToTools() => Backend.ToTools(context.Tools);

    public JsonObject ToApiPayload(int maxOutputTokens = 1024, JsonArray? tools = null) =>
        Backend.ToPayload(context, maxOutputTokens, tools);

    public ParsedResponse ParseResponse(JsonNode response) => Backend.ParseResponse(response);

    public IReadOnlyDictionary<string, string> Headers => Backend.Headers;

    public string Url => Backend.Url;
}
