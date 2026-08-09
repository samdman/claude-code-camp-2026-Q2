using Boukensha.Core;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests;

public class RegistryTests
{
    private static (Context, Registry) Build()
    {
        var context = new Context(new PlayerTask(), contextWindow: 100);
        return (context, new Registry(context));
    }

    [Fact]
    public async Task DispatchAsync_InvokesRegisteredToolHandler()
    {
        var (_, registry) = Build();
        registry.Tool("echo", "echoes input", new Dictionary<string, ToolParameter> { ["text"] = new("string") },
            args => Task.FromResult((string)args["text"]!));

        var result = await registry.DispatchAsync("echo", new Dictionary<string, object?> { ["text"] = "hi" });

        Assert.Equal("hi", result);
    }

    [Fact]
    public async Task DispatchAsync_UnknownTool_ThrowsUnknownToolException()
    {
        var (_, registry) = Build();

        await Assert.ThrowsAsync<UnknownToolException>(() => registry.DispatchAsync("missing"));
    }

    [Fact]
    public void Registered_ReflectsToolRegistration()
    {
        var (_, registry) = Build();

        Assert.False(registry.Registered("echo"));
        registry.Tool("echo", "echoes input", null, _ => Task.FromResult(""));
        Assert.True(registry.Registered("echo"));
    }
}
