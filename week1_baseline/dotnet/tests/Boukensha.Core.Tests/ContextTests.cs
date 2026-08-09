using Boukensha.Core;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests;

public class ContextTests
{
    private static Context NewContext(int contextWindow = 100) =>
        new(new PlayerTask(), "system prompt", contextWindow);

    [Fact]
    public void UsageFraction_ReflectsCurrentTokensOverContextWindow()
    {
        var context = NewContext(contextWindow: 200);
        context.UpdateTokens(50);
        Assert.Equal(0.25, context.UsageFraction, 3);
        Assert.Equal(25, context.UsagePct);
    }

    [Fact]
    public void NeedsCompaction_TrueAtOrAboveThreshold()
    {
        var context = NewContext(contextWindow: 100);
        context.UpdateTokens(85);
        Assert.True(context.NeedsCompaction());
    }

    [Fact]
    public void CompactMessages_DropsOldest40PercentAndResetsCurrentTokens()
    {
        var context = NewContext();
        for (var i = 0; i < 10; i++) context.AddMessage("user", $"message {i}");
        context.UpdateTokens(999);

        var dropped = context.CompactMessages();

        Assert.Equal(4, dropped); // ceil(10 * 0.40) = 4
        Assert.Equal(6, context.Messages.Count);
        Assert.Equal("message 4", context.Messages[0].Content.Text);
        Assert.Equal(0, context.CurrentTokens);
    }

    [Fact]
    public void CompactMessages_AlwaysKeepsAtLeastTwoMessages()
    {
        var context = NewContext();
        context.AddMessage("user", "one");
        context.AddMessage("assistant", "two");
        context.AddMessage("user", "three");

        var dropped = context.CompactMessages();

        Assert.True(context.Messages.Count >= 2);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void AddTurnTokens_AccumulatesSeparatelyFromCurrentTokens()
    {
        var context = NewContext();
        context.UpdateTokens(40);
        context.AddTurnTokens(10, 5);
        context.AddTurnTokens(10, 5);

        Assert.Equal(40, context.CurrentTokens);
        Assert.Equal(30, context.TurnTokens);
    }
}
