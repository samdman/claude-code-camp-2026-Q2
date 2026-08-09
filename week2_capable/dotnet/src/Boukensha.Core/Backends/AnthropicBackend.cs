using System.Text.Json.Nodes;

namespace Boukensha.Core.Backends;

public sealed record AnthropicModelInfo(int ContextWindow, double? InputCostPerMillion, double? OutputCostPerMillion);

public sealed class AnthropicBackend : ILlmBackend
{
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";

    private static readonly IReadOnlyDictionary<string, AnthropicModelInfo> ModelCatalog = new Dictionary<string, AnthropicModelInfo>
    {
        ["claude-haiku-4-5"] = new(200_000, 1.0, 5.0),
        ["claude-sonnet-4-6"] = new(1_000_000, 3.0, 15.0),
        ["claude-opus-4-8"] = new(1_000_000, 5.0, 25.0),
    };

    private readonly string _apiKey;
    private readonly AnthropicModelInfo _modelInfo;

    public AnthropicBackend(string apiKey, string model)
    {
        _apiKey = apiKey;
        if (!ModelCatalog.ContainsKey(model))
        {
            throw new UnsupportedModelException(
                $"unsupported model '{model}'. Supported: {string.Join(", ", ModelCatalog.Keys.OrderBy(m => m))}");
        }
        Model = model;
        _modelInfo = ModelCatalog[Model];
    }

    public string Model { get; }
    public int ContextWindow => _modelInfo.ContextWindow;

    public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>
    {
        ["Content-Type"] = "application/json",
        ["x-api-key"] = _apiKey,
        ["anthropic-version"] = "2023-06-01",
    };

    public string Url => BaseUrl;

    public JsonArray ToMessages(IReadOnlyList<Message> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(message.Role switch
            {
                "tool_result" => new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolUseId,
                        ["content"] = message.Content.Text,
                    }),
                },
                "assistant" => new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = AssistantContent(message.Content),
                },
                _ => new JsonObject
                {
                    ["role"] = message.Role,
                    ["content"] = message.Content.Text,
                },
            });
        }
        return array;
    }

    public JsonArray ToTools(IReadOnlyDictionary<string, ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools.Values)
        {
            var properties = new JsonObject();
            foreach (var (paramName, parameter) in tool.Parameters)
            {
                properties[paramName] = new JsonObject
                {
                    ["type"] = parameter.Type,
                    ["description"] = parameter.Description,
                };
            }
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(tool.Parameters.Keys.Select(k => (JsonNode)k).ToArray()),
                },
            });
        }
        return array;
    }

    public JsonObject ToPayload(Context context, int maxOutputTokens, JsonArray? toolsOverride = null) => new()
    {
        ["model"] = Model,
        ["system"] = context.System,
        ["max_tokens"] = maxOutputTokens,
        ["tools"] = toolsOverride ?? ToTools(context.Tools),
        ["messages"] = ToMessages(context.Messages),
    };

    public ParsedResponse ParseResponse(JsonNode response)
    {
        var stopReason = response["stop_reason"]?.GetValue<string>() == "tool_use" ? "tool_use" : "end_turn";
        var blocks = (response["content"] as JsonArray ?? []).Select(NormalizeBlock).ToList();
        return new ParsedResponse(stopReason, blocks);
    }

    public double? EstimateCost(int inputTokens, int outputTokens)
    {
        if (_modelInfo.InputCostPerMillion is null || _modelInfo.OutputCostPerMillion is null) return null;
        return (inputTokens * _modelInfo.InputCostPerMillion.Value + outputTokens * _modelInfo.OutputCostPerMillion.Value) / 1_000_000.0;
    }

    private static ContentBlock NormalizeBlock(JsonNode? node)
    {
        var type = node?["type"]?.GetValue<string>();
        return type switch
        {
            "thinking" => new ReasoningBlock(node!["thinking"]!.GetValue<string>(), false, node["signature"]?.GetValue<string>()),
            "redacted_thinking" => new ReasoningBlock(string.Empty, true, node!["data"]?.GetValue<string>()),
            "tool_use" => new ToolUseBlock(
                node!["id"]!.GetValue<string>(),
                node["name"]!.GetValue<string>(),
                JsonUtil.ToObject(node["input"]) as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>()),
            _ => new TextBlock(node?["text"]?.GetValue<string>() ?? string.Empty),
        };
    }

    private static JsonArray AssistantContent(MessageContent content)
    {
        if (content.IsText) return new JsonArray(content.Text);

        var array = new JsonArray();
        foreach (var block in content.Blocks!) array.Add(DenormalizeBlock(block));
        return array;
    }

    private static JsonNode DenormalizeBlock(ContentBlock block) => block switch
    {
        ReasoningBlock { Redacted: true } r => new JsonObject { ["type"] = "redacted_thinking", ["data"] = r.Signature },
        ReasoningBlock r => new JsonObject { ["type"] = "thinking", ["thinking"] = r.Text, ["signature"] = r.Signature },
        ToolUseBlock t => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["input"] = JsonUtil.ToJsonNode(t.Input),
        },
        TextBlock t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        _ => throw new NotSupportedException($"cannot serialize block of type {block.GetType()}"),
    };
}
