using System.Text.Json.Nodes;
using Boukensha.Core.Mcp;
using Xunit;

namespace Boukensha.Core.Tests.Mcp;

public class JsonRpcTests
{
    [Fact]
    public void BuildRequest_ProducesJsonRpc20Envelope()
    {
        var json = JsonRpc.BuildRequest(1, "tools/list", new JsonObject());
        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"id\":1", json);
        Assert.Contains("\"method\":\"tools/list\"", json);
    }

    [Fact]
    public void TryParseResponse_MatchesOnExpectedId()
    {
        var ok = JsonRpc.TryParseResponse("""{"jsonrpc":"2.0","id":3,"result":{}}""", 3, out var message);
        Assert.True(ok);
        Assert.NotNull(message);
    }

    [Fact]
    public void TryParseResponse_IgnoresMismatchedId()
    {
        var ok = JsonRpc.TryParseResponse("""{"jsonrpc":"2.0","id":4,"result":{}}""", 3, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParseResponse_IgnoresMalformedJson()
    {
        var ok = JsonRpc.TryParseResponse("not json", 3, out _);
        Assert.False(ok);
    }

    [Fact]
    public void ExtractToolText_ConcatenatesTextBlocks()
    {
        var result = JsonNode.Parse(
            """{"content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}]}""")!.AsObject();
        Assert.Equal("hello world", JsonRpc.ExtractToolText(result));
    }

    [Fact]
    public void IsToolError_ReadsIsErrorFlag()
    {
        var result = JsonNode.Parse("""{"isError":true}""")!.AsObject();
        Assert.True(JsonRpc.IsToolError(result));
    }
}
