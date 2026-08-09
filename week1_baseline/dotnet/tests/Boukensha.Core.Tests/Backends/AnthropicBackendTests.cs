using System.Text.Json.Nodes;
using Boukensha.Core;
using Boukensha.Core.Backends;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests.Backends;

public class AnthropicBackendTests
{
    [Fact]
    public void Constructor_RejectsUnsupportedModel()
    {
        Assert.Throws<UnsupportedModelException>(() => new AnthropicBackend("key", "not-a-real-model"));
    }

    [Fact]
    public void ToPayload_IncludesSystemModelAndMessages()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), "be helpful", backend.ContextWindow);
        context.AddMessage("user", "hello");

        var payload = backend.ToPayload(context, 512);

        Assert.Equal("claude-haiku-4-5", payload["model"]!.GetValue<string>());
        Assert.Equal("be helpful", payload["system"]!.GetValue<string>());
        Assert.Equal(512, payload["max_tokens"]!.GetValue<int>());
        Assert.Single(payload["messages"]!.AsArray());
    }

    [Fact]
    public void ParseResponse_NormalizesThinkingBlockToReasoningBlock()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var response = JsonNode.Parse(
            """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"pondering","signature":"sig-1"}]}""")!;

        var parsed = backend.ParseResponse(response);

        var reasoning = Assert.IsType<ReasoningBlock>(Assert.Single(parsed.Content));
        Assert.Equal("pondering", reasoning.Text);
        Assert.False(reasoning.Redacted);
        Assert.Equal("sig-1", reasoning.Signature);
    }

    [Fact]
    public void ParseResponse_ToolUseSetsStopReason()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var response = JsonNode.Parse(
            """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"look","input":{}}]}""")!;

        var parsed = backend.ParseResponse(response);

        Assert.Equal("tool_use", parsed.StopReason);
        Assert.IsType<ToolUseBlock>(Assert.Single(parsed.Content));
    }

    [Fact]
    public void AssistantContentRoundTrip_PreservesThinkingSignature()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), contextWindow: backend.ContextWindow);
        context.AddMessage("assistant", new ContentBlock[]
        {
            new ReasoningBlock("pondering", false, "sig-1"),
            new TextBlock("done"),
        });

        var messages = backend.ToMessages(context.Messages);
        var content = messages[0]!["content"]!.AsArray();

        Assert.Equal("thinking", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("sig-1", content[0]!["signature"]!.GetValue<string>());
        Assert.Equal("pondering", content[0]!["thinking"]!.GetValue<string>());
    }
}
