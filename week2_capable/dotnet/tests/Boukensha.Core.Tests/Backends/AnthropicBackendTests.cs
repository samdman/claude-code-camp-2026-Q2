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
    public void ToMessages_PlainTextAssistantMessage_SerializesAsTextBlockNotBareString()
    {
        // Matches how Agent.cs actually ends a turn with no tool call: AddMessage("assistant", text)
        // via the plain-string overload, not the ContentBlock-list overload. The Anthropic API
        // requires every content array entry to be an object -- a bare string here produces
        // "messages.N.content.0: Input should be an object" on the next API call once the
        // conversation continues past this message.
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), contextWindow: backend.ContextWindow);
        context.AddMessage("assistant", "I found the bakery.");

        var messages = backend.ToMessages(context.Messages);
        var content = messages[0]!["content"]!.AsArray();

        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("I found the bakery.", content[0]!["text"]!.GetValue<string>());
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
