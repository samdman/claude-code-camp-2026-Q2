using Boukensha.Core;
using Xunit;

namespace Boukensha.Core.Tests;

public class AgentHooksTests
{
    [Fact]
    public async Task RaiseBeforeAgentCall_InvokesAllSubscribersInOrder()
    {
        var hooks = new AgentHooks();
        var calls = new List<int>();
        hooks.OnBeforeAgentCall((_, _) => { calls.Add(1); return Task.CompletedTask; });
        hooks.OnBeforeAgentCall((_, _) => { calls.Add(2); return Task.CompletedTask; });

        await hooks.RaiseBeforeAgentCall(null!, CancellationToken.None);

        Assert.Equal([1, 2], calls);
    }

    [Fact]
    public async Task RaiseAfterToolCall_PassesNameArgsResultAndOk()
    {
        var hooks = new AgentHooks();
        string? capturedName = null;
        string? capturedResult = null;
        bool? capturedOk = null;
        hooks.OnAfterToolCall((name, args, result, ok, _) =>
        {
            capturedName = name;
            capturedResult = result;
            capturedOk = ok;
            return Task.CompletedTask;
        });

        await hooks.RaiseAfterToolCall("move", new Dictionary<string, object?> { ["direction"] = "south" }, "You walk south.", true, CancellationToken.None);

        Assert.Equal("move", capturedName);
        Assert.Equal("You walk south.", capturedResult);
        Assert.True(capturedOk);
    }

    [Fact]
    public async Task RaiseNarration_InvokesAllSubscribersWithText()
    {
        var hooks = new AgentHooks();
        var captured = new List<string>();
        hooks.OnNarration((text, _) => { captured.Add(text); return Task.CompletedTask; });

        await hooks.RaiseNarration("Still looking for the bakery...", CancellationToken.None);

        Assert.Equal(["Still looking for the bakery..."], captured);
    }

    [Fact]
    public void DefaultAgentHooks_HasNoSubscribersAndDoesNotThrowWhenRaised()
    {
        var hooks = new AgentHooks();
        var task = hooks.RaiseBeforeToolCall("look", new Dictionary<string, object?>(), CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
    }
}
