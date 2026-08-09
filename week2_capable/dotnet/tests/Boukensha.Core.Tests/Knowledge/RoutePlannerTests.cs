using Boukensha.Core.Knowledge;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class RoutePlannerTests
{
    private static KnowledgeStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("boukensha_route_planner_test").FullName, "knowledge.db"));

    [Fact]
    public void FindRoute_ReturnsStepByStepPathForReachableDestination()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        var b = store.UpsertRoom("B", "b");
        var c = store.UpsertRoom("C", "c");
        store.LinkExit(a.Id, "south", b.Id);
        store.LinkExit(b.Id, "east", c.Id);
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("C");

        Assert.True(result.Found);
        Assert.Equal("C", result.DestinationRoomName);
        Assert.Equal(["south", "east"], result.Directions);
    }

    [Fact]
    public void FindRoute_KnownButUnreachableDestination_SuggestsFrontierExits()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        store.UpsertRoom("Isolated", "far away, not linked from here");
        store.RecordExits(a.Id, new Dictionary<string, string?> { ["north"] = null });
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("Isolated");

        Assert.False(result.Found);
        Assert.Equal("Isolated", result.DestinationRoomName);
        Assert.Contains("north", result.Message);
    }

    [Fact]
    public void FindRoute_UnknownDestinationName_SuggestsFrontierExits()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        store.RecordExits(a.Id, new Dictionary<string, string?> { ["east"] = "Somewhere" });
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("Nowhere");

        Assert.False(result.Found);
        Assert.Null(result.DestinationRoomName);
        Assert.Contains("east", result.Message);
    }

    [Fact]
    public void FindRoute_DestinationIsCurrentRoom_ReturnsZeroStepRoute()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("A");

        Assert.True(result.Found);
        Assert.Empty(result.Directions);
    }

    [Fact]
    public void FindRoute_CurrentLocationUnknown_ReturnsNotFoundWithoutQuerying()
    {
        using var store = NewStore();
        store.UpsertRoom("A", "a");

        var result = new RoutePlanner(store).FindRoute("A");

        Assert.False(result.Found);
        Assert.Contains("look around", result.Message);
    }

    [Fact]
    public void FindRoute_MatchesDestinationCaseInsensitivelyAndBySubstring()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("The Grand Sewer", "a");
        var b = store.UpsertRoom("B", "b");
        store.LinkExit(a.Id, "south", b.Id);
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("grand sewer");

        Assert.True(result.Found);
        Assert.Equal("The Grand Sewer", result.DestinationRoomName);
    }

    [Fact]
    public void FindRoute_WithExplicitFrom_PlansFromThatRoomInsteadOfCurrent()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        var b = store.UpsertRoom("B", "b");
        var c = store.UpsertRoom("C", "c");
        store.LinkExit(a.Id, "south", b.Id);
        store.LinkExit(b.Id, "east", c.Id);
        store.SetCurrentRoom(c.Id); // agent is at C, but asks to plan a route starting from A

        var result = new RoutePlanner(store).FindRoute("C", fromQuery: "A");

        Assert.True(result.Found);
        Assert.Equal(["south", "east"], result.Directions);
    }

    [Fact]
    public void FindRoute_ExplicitFromDoesNotResolve_ReturnsNotFound()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("A", fromQuery: "Nonexistent");

        Assert.False(result.Found);
        Assert.Contains("Nonexistent", result.Message);
    }

    [Fact]
    public void FindRoute_OmittingFrom_StillUsesCurrentRoom()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        var b = store.UpsertRoom("B", "b");
        store.LinkExit(a.Id, "south", b.Id);
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("B");

        Assert.True(result.Found);
        Assert.Equal(["south"], result.Directions);
    }
}
