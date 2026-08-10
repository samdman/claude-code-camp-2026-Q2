using Boukensha.Core;
using Boukensha.Core.Knowledge;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class ExplorationPlannerTests
{
    private static KnowledgeStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("boukensha_exploration_planner_test").FullName, "knowledge.db"));

    private static Logger NewLogger() =>
        new(Directory.CreateTempSubdirectory("boukensha_exploration_planner_test_log").FullName, sessionId: "test");

    private static (Registry Registry, AgentHooks Hooks, Logger Logger) BuildFakeMud(
        KnowledgeStore store,
        string startRoomName,
        Dictionary<(string From, string Direction), Func<string>> moveResponses,
        Dictionary<string, string> exitsResponses,
        Dictionary<string, Func<string>>? sendRawResponses = null)
    {
        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);
        var registry = new Registry(context);
        var logger = NewLogger();

        var currentName = startRoomName;

        registry.Tool("move", "move", null, args =>
        {
            var direction = (string)args["direction"]!;
            var text = moveResponses.TryGetValue((currentName, direction), out var factory)
                ? factory()
                : "Alas, you cannot go that way.";
            if (text.Contains("[ Exits:")) currentName = text.Split('\n')[0].Trim();
            return Task.FromResult(text);
        });

        registry.Tool("check", "check", null, args =>
        {
            var kind = (string)args["kind"]!;
            return Task.FromResult(kind == "exits" && exitsResponses.TryGetValue(currentName, out var text) ? text : "");
        });

        registry.Tool("send_raw", "send_raw", null, args =>
        {
            var command = (string)args["command"]!;
            var text = sendRawResponses is not null && sendRawResponses.TryGetValue(command, out var factory)
                ? factory()
                : "Huh?!?";
            if (text.Contains("[ Exits:")) currentName = text.Split('\n')[0].Trim();
            return Task.FromResult(text);
        });

        return (registry, hooks, logger);
    }

    [Fact]
    public async Task ExploreTowardsAsync_FindsMultiHopTarget()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Hallway" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Hallway\nA narrow hallway.\n[ Exits: e ]",
                [("Hallway", "east")] = () => "Bakery\nSmells of fresh bread.\n[ Exits: w ]",
            },
            exitsResponses: new()
            {
                ["Hallway"] = "east - Bakery",
                ["Bakery"] = "west - Hallway",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 10, confidenceThreshold: 0.2);

        Assert.True(result.Found);
        Assert.Equal("Bakery", result.DestinationRoomName);
        Assert.Equal(["north", "east"], result.Directions);
        Assert.Contains("Discovered 2 new room", result.Message);
    }

    [Fact]
    public async Task ExploreTowardsAsync_ExhaustsClosedMapWithNoMatch()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Closet" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Closet\nA tiny closet, no other exits.\n[ Exits: s ]",
            },
            exitsResponses: new(),
            sendRawResponses: new()
            {
                ["recall"] = () => "Start\nthe starting room\n[ Exits: n ]",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Nonexistent", maxSteps: 10, confidenceThreshold: 0.2);

        Assert.False(result.Found);
        Assert.Contains("Explored the full known map (1 new room found)", result.Message);
        Assert.Contains("Nonexistent", result.Message);
        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id); // exhausted map still retreats via recall
    }

    [Fact]
    public async Task ExploreTowardsAsync_RespectsStepBudget_ReturnsStillExploringStatus()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Hallway" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Hallway\nA narrow hallway.\n[ Exits: e ]",
            },
            exitsResponses: new()
            {
                ["Hallway"] = "east - Bakery",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 1, confidenceThreshold: 0.2);

        Assert.False(result.Found);
        Assert.Contains("Still exploring for 'Bakery'", result.Message);
        Assert.Contains("1 new room found", result.Message);
        Assert.Contains("1 frontier", result.Message);
        Assert.Contains("Call plan_route again", result.Message);
    }

    [Fact]
    public async Task ExploreTowardsAsync_StepBudgetHit_DoesNotRetreat()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Bakery Cellar" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Hallway\nA narrow hallway.\n[ Exits: e ]",
            },
            exitsResponses: new()
            {
                ["Hallway"] = "east - Bakery",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 1, confidenceThreshold: 0.5);

        Assert.False(result.Found);
        var current = store.GetCurrentRoom();
        Assert.NotNull(current);
        Assert.Equal("Hallway", current!.Name); // stayed put -- no retreat on step-budget-hit
    }

    [Fact]
    public async Task ExploreTowardsAsync_UnresolvedExit_StopsCleanlyWithoutGuessingPosition_RetriedAfterPositionRecovered()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["east"] = "Garden" });

        var attempts = 0;
        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "east")] = () =>
                {
                    attempts++;
                    // First attempt: an unparseable result (matches both "Alas, you cannot
                    // go that way" and "It is pitch black..." in real CircleMUD -- neither
                    // has an [ Exits: ] line, so KnowledgeHooks clears position rather than
                    // guess which one it was). Second attempt succeeds normally.
                    return attempts == 1 ? "Alas, you cannot go that way." : "Garden\nA green garden.\n[ Exits: w ]";
                },
            },
            exitsResponses: new()
            {
                ["Garden"] = "west - Start",
            });

        var planner = new ExplorationPlanner(store, registry, hooks, logger);

        var firstAttempt = await planner.ExploreTowardsAsync("Garden", maxSteps: 5, confidenceThreshold: 0.2);
        Assert.False(firstAttempt.Found);
        Assert.Contains("Lost track of position", firstAttempt.Message);
        Assert.Contains("0 new room", firstAttempt.Message);
        Assert.Contains("1 frontier", firstAttempt.Message);
        Assert.Null(store.GetCurrentRoom()); // position genuinely unknown -- recall/retrace couldn't recover it either

        // A real agent recovers via a normal `look` call at this point; simulate that here
        // rather than depending on a live MUD round trip just to re-establish position.
        store.SetCurrentRoom(start.Id);

        var secondAttempt = await planner.ExploreTowardsAsync("Garden", maxSteps: 5, confidenceThreshold: 0.2);
        Assert.True(secondAttempt.Found);
        Assert.Equal("Garden", secondAttempt.DestinationRoomName);
        Assert.Equal(["east"], secondAttempt.Directions);
    }

    [Fact]
    public async Task ExploreTowardsAsync_BacktracksAcrossBranches_ViaNearestFrontierRankingAlone()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["east"] = "Storage", ["north"] = "Hallway" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "east")] = () => "Storage\nA cramped storage room.\n[ Exits: w ]",
                [("Storage", "west")] = () => "Start\nthe starting room\n[ Exits: n e ]",
                [("Start", "north")] = () => "Hallway\nA narrow hallway.\n[ Exits: e ]",
                [("Hallway", "east")] = () => "Bakery\nSmells of fresh bread.\n[ Exits: w ]",
            },
            exitsResponses: new()
            {
                ["Storage"] = "west - Start",
                ["Start"] = "east - Storage\nnorth - Hallway",
                ["Hallway"] = "east - Bakery",
                ["Bakery"] = "west - Hallway",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 10, confidenceThreshold: 0.2);

        // Storage is a dead end for forward progress (its only exit leads back to Start),
        // so the walk must return to Start and go explore Hallway instead. Both of Start's
        // original exits carry an equally-unproven (0.2) hint, so this is still driven by
        // "which reachable frontier is nearest" among ties, exactly as before -- confidence
        // ranking only overrides distance when confidences actually differ.
        Assert.True(result.Found);
        Assert.Equal("Bakery", result.DestinationRoomName);
        Assert.Equal(["north", "east"], result.Directions);
        Assert.Contains("Discovered 3 new room", result.Message);
    }

    [Fact]
    public async Task ExploreTowardsAsync_WalksMultiStepApproachPathToADistantFrontier()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "room a");
        var b = store.UpsertRoom("B", "room b");
        store.LinkExit(a.Id, "north", b.Id);
        store.LinkExit(b.Id, "south", a.Id);
        store.RecordExits(a.Id, new Dictionary<string, string?> { ["east"] = "Bakery" });
        store.SetCurrentRoom(b.Id); // standing in B, which has no frontier of its own

        var (registry, hooks, logger) = BuildFakeMud(store, "B",
            moveResponses: new()
            {
                [("B", "south")] = () => "A\nroom a\n[ Exits: n e ]",
                [("A", "east")] = () => "Bakery\nSmells of fresh bread.\n[ Exits: w ]",
            },
            exitsResponses: new()
            {
                ["Bakery"] = "west - A",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 10, confidenceThreshold: 0.2);

        // Reaching the nearest frontier (at A) takes one approach move (B -> south -> A)
        // before the frontier-crossing move (A -> east -> Bakery) itself.
        Assert.True(result.Found);
        Assert.Equal(["south", "east"], result.Directions);
        Assert.Contains("Discovered 1 new room", result.Message);
    }

    [Fact]
    public async Task ExploreTowardsAsync_PrefersHigherConfidenceCandidateOverNearerUnprovenOne()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        var hallway = store.UpsertRoom("Hallway", "a narrow hallway");
        store.LinkExit(start.Id, "north", hallway.Id);
        store.LinkExit(hallway.Id, "south", start.Id);
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["east"] = null }); // unproven, distance 0
        store.RecordExits(hallway.Id, new Dictionary<string, string?> { ["east"] = "Bakery" }); // exact match, distance 1

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Hallway\na narrow hallway\n[ Exits: s e ]",
                [("Hallway", "east")] = () => "Bakery\nSmells of fresh bread.\n[ Exits: w ]",
            },
            exitsResponses: new()
            {
                ["Bakery"] = "west - Hallway",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 10, confidenceThreshold: 0.5);

        // The farther, exact-hint-matching candidate (Hallway's east exit, confidence 1.0)
        // must win over the nearer, unproven one (Start's own east exit, confidence 0.2) --
        // confidence outranks distance now, not the other way around.
        Assert.True(result.Found);
        Assert.Equal(["north", "east"], result.Directions);
    }

    [Fact]
    public async Task ExploreTowardsAsync_NoConfidentCandidate_RetreatsViaRecall()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["east"] = "Cave" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new(),
            exitsResponses: new(),
            sendRawResponses: new()
            {
                ["recall"] = () => "Start\nthe starting room\n[ Exits: e ]",
            });

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 10, confidenceThreshold: 0.5);

        Assert.False(result.Found);
        Assert.Contains("No promising leads for 'Bakery'", result.Message);
        Assert.Contains("Recalled back to 'Start'", result.Message);
        Assert.Contains("0 new room", result.Message);
        Assert.Contains("1 frontier", result.Message);
        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id);
    }

    [Fact]
    public async Task ExploreTowardsAsync_RecallFails_FallsBackToRetracingOwnStepsInReverse()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Bakery Cellar" });

        var (registry, hooks, logger) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Hallway\nA narrow hallway.\n[ Exits: e ]",
                [("Hallway", "south")] = () => "Start\nthe starting room\n[ Exits: n ]",
            },
            exitsResponses: new()
            {
                ["Hallway"] = "east - Cave",
            });
            // No "recall" entry in sendRawResponses -- falls through to the fake's
            // default unparseable response, forcing the retrace-own-steps fallback.

        var result = await new ExplorationPlanner(store, registry, hooks, logger)
            .ExploreTowardsAsync("Bakery", maxSteps: 10, confidenceThreshold: 0.5);

        // "Bakery Cellar" substring-matches "Bakery" (0.6, clears the 0.5 threshold),
        // so the walk crosses north into Hallway for real. There, "Cave" doesn't match
        // (0.2, below threshold) -- stuck. Recall fails to parse, so the fallback must
        // retrace [north] in reverse ([south]) to get back to Start.
        Assert.False(result.Found);
        Assert.Contains("No promising leads for 'Bakery'", result.Message);
        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id);
    }
}
