using Boukensha.Core.Knowledge;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class RoomGraphTests
{
    [Fact]
    public void ExitConfidence_ExactHintMatch_ReturnsOnePointZero()
    {
        var exit = new ExitRecord("east", "frontier", null, "Bakery", null);
        Assert.Equal(1.0, RoomGraph.ExitConfidence(exit, "Bakery"));
    }

    [Fact]
    public void ExitConfidence_ExactHintMatch_IsCaseInsensitive()
    {
        var exit = new ExitRecord("east", "frontier", null, "bakery", null);
        Assert.Equal(1.0, RoomGraph.ExitConfidence(exit, "Bakery"));
    }

    [Fact]
    public void ExitConfidence_HintContainsQuery_ReturnsZeroPointSix()
    {
        var exit = new ExitRecord("east", "frontier", null, "Old Bakery District", null);
        Assert.Equal(0.6, RoomGraph.ExitConfidence(exit, "Bakery"));
    }

    [Fact]
    public void ExitConfidence_QueryContainsHint_ReturnsZeroPointSix()
    {
        var exit = new ExitRecord("east", "frontier", null, "Bakery", null);
        Assert.Equal(0.6, RoomGraph.ExitConfidence(exit, "The Old Bakery"));
    }

    [Fact]
    public void ExitConfidence_NoHint_ReturnsZeroPointTwo()
    {
        var exit = new ExitRecord("east", "frontier", null, null, null);
        Assert.Equal(0.2, RoomGraph.ExitConfidence(exit, "Bakery"));
    }

    [Fact]
    public void ExitConfidence_NonMatchingHint_ReturnsSameAsNoHint()
    {
        // Regression for the fix made while writing this plan: a hint only ever
        // names the immediate next room, never a multi-hop destination further
        // beyond it, so "doesn't match yet" must not score below "no hint at all"
        // -- otherwise it blocks exploring the first hop of any multi-hop route.
        var exit = new ExitRecord("east", "frontier", null, "Cave", null);
        Assert.Equal(0.2, RoomGraph.ExitConfidence(exit, "Bakery"));
    }
}
