# Frontier Exploration Confidence-Ranking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent.
>
> Spec: `docs/plans/week_2/specs/frontier_exploration_confidence.md`. Amends `docs/plans/week_2/specs/frontier_exploration.md` (implemented in `d6780bf`) — read both before starting.

**Goal:** Change `ExplorationPlanner`'s frontier-ranked walk from pure nearest-distance ranking to confidence-ranked ("most likely to lead to the destination, based on captured exit-name hints"), log the reasoning per step, stop chasing once nothing looks promising, and retreat back to where the walk started (via a `recall` MUD command, falling back to retracing the call's own moves in reverse) instead of leaving the character stranded.

**Architecture:** A new pure `RoomGraph.ExitConfidence` scoring function drives candidate ranking inside `ExplorationPlanner`. A new `MudTextParser.OppositeDirection` helper supports the walk-back fallback. `KnowledgeHooks` gains a `send_raw`/`recall` case (deliberately *not* copying `flee`'s clear-on-parse-failure behavior — a rejected recall is unambiguous, unlike a dark room). `ExplorationPlanner` gains a `Logger` dependency for step-by-step reasoning logs, and its stopping logic changes from "always keep going until budget/exhaustion" to "stop as soon as the best candidate isn't good enough, then retreat."

**Tech Stack:** No new dependencies — pure C#/.NET additions to the existing `Boukensha.Core` project, following this session's established fixture/test conventions (fake `Registry`/`AgentHooks` pairs with real `KnowledgeHooks` wired in, scripted MUD text).

## Global Constraints

- All 9 existing `RoutePlannerTests` and the 5 existing `ExplorationPlannerTests` must continue to pass (the latter with updated method signatures only — no behavior regression for their scenarios; each was traced by hand against the new confidence math before this plan was written).
- Confidence scoring is 3-tier, not 4: exact hint match (1.0), substring match either direction (0.6), no hint or a non-matching hint (0.2 — collapsed into one tier; see spec's "Confidence scoring" section for why a non-matching hint must not score below no-hint).
- `agent.exploration_confidence_threshold` defaults to **0.5**, following the exact `Config.Dig("agent", ...)` pattern `AgentExplorationMaxSteps` already uses.
- Retreat (recall, falling back to retracing this call's own moves in reverse) applies whenever exploration stops without finding the target for the **stuck**, **exhausted**, or **unresolved-position** reasons — never for the step-budget-hit ("still exploring, call again") case.
- A rejected/unavailable `recall` must **not** clear the character's known current room in `KnowledgeStore` — unlike `move`/`flee`, a failed recall is unambiguous (the character never left).
- The walk-back fallback retraces the directions `ExplorationPlanner` itself dispatched this call, in reverse, using each direction's opposite — it must not use `RoomGraph.FindPath`, which only follows exits recorded walked in the direction they were walked and would routinely fail to find a route back.
- `ExplorationPlanner` never calls `Context.AddMessage` (unchanged from the base spec) — logging goes to `Logger`, not the LLM conversation.

---

## Task 1: `RoomGraph.ExitConfidence`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoomGraph.cs`
- Test: Create `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/RoomGraphTests.cs`

**Interfaces:**
- Produces: `RoomGraph.ExitConfidence(ExitRecord exit, string query) -> double`. Task 5 (`ExplorationPlanner`) consumes this.

- [x] **Step 1: Write the failing tests**

Create `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/RoomGraphTests.cs`:

```csharp
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
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoomGraphTests`
Expected: build failure — `RoomGraph.ExitConfidence` doesn't exist yet.

- [x] **Step 3: Implement `ExitConfidence`**

Modify `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoomGraph.cs` — add immediately after `RoomMatchesQuery`:

```csharp
    public static double ExitConfidence(ExitRecord exit, string query)
    {
        if (exit.Hint is null) return 0.2;
        if (exit.Hint.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (exit.Hint.Contains(query, StringComparison.OrdinalIgnoreCase)
            || query.Contains(exit.Hint, StringComparison.OrdinalIgnoreCase)) return 0.6;
        return 0.2;
    }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoomGraphTests`
Expected: all 6 tests pass.

- [x] **Step 5: Commit** — deferred to the final batched commit (Task 6), per this session's established cadence.

---

## Task 2: `MudTextParser.OppositeDirection`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/MudTextParser.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/MudTextParserTests.cs` (existing file — add a new `[Theory]`)

**Interfaces:**
- Produces: `MudTextParser.OppositeDirection(string direction) -> string`. Task 5 (`ExplorationPlanner`) consumes this for the walk-back fallback.

- [x] **Step 1: Write the failing test**

Add to `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/MudTextParserTests.cs`, immediately after the existing `NormalizeDirection_MapsLettersAndPassesThroughFullWords` theory (around line 50):

```csharp
    [Theory]
    [InlineData("north", "south")]
    [InlineData("south", "north")]
    [InlineData("east", "west")]
    [InlineData("west", "east")]
    [InlineData("up", "down")]
    [InlineData("down", "up")]
    public void OppositeDirection_ReturnsReverse(string input, string expected)
    {
        Assert.Equal(expected, MudTextParser.OppositeDirection(input));
    }
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter OppositeDirection`
Expected: build failure — `MudTextParser.OppositeDirection` doesn't exist yet.

- [x] **Step 3: Implement `OppositeDirection`**

Modify `week2_capable/dotnet/src/Boukensha.Core/Knowledge/MudTextParser.cs` — add a new private dictionary and public method immediately after `NormalizeDirection` (after line 31):

```csharp
    private static readonly IReadOnlyDictionary<string, string> OppositeDirections = new Dictionary<string, string>
    {
        ["north"] = "south",
        ["south"] = "north",
        ["east"] = "west",
        ["west"] = "east",
        ["up"] = "down",
        ["down"] = "up",
    };

    public static string OppositeDirection(string direction) =>
        OppositeDirections.TryGetValue(direction, out var opposite) ? opposite : direction;
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter OppositeDirection`
Expected: all 6 cases pass.

- [x] **Step 5: Commit** — deferred to the final batched commit (Task 6).

---

## Task 3: `Config.AgentExplorationConfidenceThreshold` + `Logger.ExplorationStep`/`Logger.ExplorationRetreat`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Config.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Logger.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/LoggerTests.cs` (existing file — add new `[Fact]`s; `Config`'s new property has no dedicated test, matching the untested precedent set by `AgentExplorationMaxSteps`/`AgentCompactionThreshold`)

**Interfaces:**
- Produces: `Config.AgentExplorationConfidenceThreshold -> double` (Task 6 consumes, wiring it into `BoukenshaHost`); `Logger.ExplorationStep(int step, int roomId, string direction, string? hint, double confidence, bool explored) -> void` and `Logger.ExplorationRetreat(string reason, int stepsUsed, int discovered, int frontiersRemaining, bool recalled) -> void` (Task 5's `ExplorationPlanner` consumes both).

- [x] **Step 1: Write the failing tests**

Add to `week2_capable/dotnet/tests/Boukensha.Core.Tests/LoggerTests.cs`, using the file's existing `NewLogger()`/`ReadEvents()` helpers:

```csharp
    [Fact]
    public void ExplorationStep_IncludesConfidenceAndExploredFlag()
    {
        var (logger, path) = NewLogger();
        logger.ExplorationStep(step: 1, roomId: 7, direction: "east", hint: "Bakery", confidence: 1.0, explored: true);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("exploration_step", evt["phase"].GetString());
        Assert.Equal(7, evt["room_id"].GetInt32());
        Assert.Equal("east", evt["direction"].GetString());
        Assert.Equal("Bakery", evt["hint"].GetString());
        Assert.Equal(1.0, evt["confidence"].GetDouble());
        Assert.True(evt["explored"].GetBoolean());
    }

    [Fact]
    public void ExplorationRetreat_IncludesReasonAndRecalledFlag()
    {
        var (logger, path) = NewLogger();
        logger.ExplorationRetreat(reason: "stuck", stepsUsed: 3, discovered: 2, frontiersRemaining: 4, recalled: true);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("exploration_retreat", evt["phase"].GetString());
        Assert.Equal("stuck", evt["reason"].GetString());
        Assert.Equal(3, evt["steps_used"].GetInt32());
        Assert.Equal(2, evt["discovered"].GetInt32());
        Assert.Equal(4, evt["frontiers_remaining"].GetInt32());
        Assert.True(evt["recalled"].GetBoolean());
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter "ExplorationStep_IncludesConfidenceAndExploredFlag|ExplorationRetreat_IncludesReasonAndRecalledFlag"`
Expected: build failure — `Logger.ExplorationStep`/`ExplorationRetreat` don't exist yet.

- [x] **Step 3: Implement the `Config` property and `Logger` methods**

Modify `week2_capable/dotnet/src/Boukensha.Core/Config.cs` — add immediately after `AgentExplorationMaxSteps` (line 44):

```csharp
    public double AgentExplorationConfidenceThreshold => Convert.ToDouble(Dig("agent", "exploration_confidence_threshold") ?? 0.5);
```

Modify `week2_capable/dotnet/src/Boukensha.Core/Logger.cs` — add immediately after `TurnEnd` (line 38):

```csharp
    public void ExplorationStep(int step, int roomId, string direction, string? hint, double confidence, bool explored) =>
        WriteLog(new()
        {
            ["phase"] = "exploration_step",
            ["step"] = step,
            ["room_id"] = roomId,
            ["direction"] = direction,
            ["hint"] = hint,
            ["confidence"] = confidence,
            ["explored"] = explored,
        });

    public void ExplorationRetreat(string reason, int stepsUsed, int discovered, int frontiersRemaining, bool recalled) =>
        WriteLog(new()
        {
            ["phase"] = "exploration_retreat",
            ["reason"] = reason,
            ["steps_used"] = stepsUsed,
            ["discovered"] = discovered,
            ["frontiers_remaining"] = frontiersRemaining,
            ["recalled"] = recalled,
        });
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter "ExplorationStep_IncludesConfidenceAndExploredFlag|ExplorationRetreat_IncludesReasonAndRecalledFlag"`
Expected: both pass.

- [x] **Step 5: Verify:** `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.

- [x] **Step 6: Commit** — deferred to the final batched commit (Task 6).

---

## Task 4: `KnowledgeHooks` — `send_raw`/`recall` case

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeHooks.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/KnowledgeHooksTests.cs` (existing file — add new `[Fact]`s)

**Interfaces:**
- Consumes: `MudTextParser.ParseRoomBlock` (existing), `KnowledgeStore.UpsertRoom`/`SetCurrentRoom`/`GetCurrentRoom` (existing).
- Produces: no new public API — this is a behavior addition inside `KnowledgeHooks.Register`'s existing `OnAfterToolCall` switch. Task 5 (`ExplorationPlanner`) relies on this behavior when it dispatches `send_raw` with `command: "recall"`.

- [x] **Step 1: Write the failing tests**

Add to `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/KnowledgeHooksTests.cs`:

```csharp
    [Fact]
    public async Task Register_AfterToolCall_RecallSuccess_UpdatesCurrentRoom()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);

        var args = new Dictionary<string, object?> { ["command"] = "recall" };
        await hooks.RaiseAfterToolCall("send_raw", args, "Temple\nA sacred hall.\n[ Exits: n ]", true, CancellationToken.None);

        var current = store.GetCurrentRoom();
        Assert.NotNull(current);
        Assert.Equal("Temple", current!.Name);
    }

    [Fact]
    public async Task Register_AfterToolCall_RecallRejected_LeavesCurrentRoomUnchanged()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);

        var args = new Dictionary<string, object?> { ["command"] = "recall" };
        await hooks.RaiseAfterToolCall("send_raw", args, "You have too much on your mind to recall.", true, CancellationToken.None);

        // Unlike a dark room, a rejected recall is unambiguous -- the character
        // never left, so position must stay exactly as it was, not clear to unknown.
        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id);
    }

    [Fact]
    public async Task Register_AfterToolCall_SendRawNonRecallCommand_IsIgnored()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);

        var args = new Dictionary<string, object?> { ["command"] = "who" };
        await hooks.RaiseAfterToolCall("send_raw", args, "Temple\nA sacred hall.\n[ Exits: n ]", true, CancellationToken.None);

        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id);
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter "RecallSuccess_UpdatesCurrentRoom|RecallRejected_LeavesCurrentRoomUnchanged|SendRawNonRecallCommand_IsIgnored"`
Expected: `RecallSuccess`/`SendRawNonRecallCommand` pass already (no-op today, so current room stays `start.Id` — assert against `start.Id` in both, harmlessly true before the change too); `RecallRejected` fails only if the current implementation clears position, which it doesn't attempt to do at all yet since `send_raw` isn't handled — so all three should actually pass trivially before implementation since `send_raw` is unhandled. Re-run after Step 3 and confirm they still pass, with `RecallSuccess` now actually exercising the new code path (its current-room name assertion is what would fail without the new case).

- [x] **Step 3: Implement the `send_raw`/`recall` case**

Modify `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeHooks.cs` — add a new case to the `switch (name)` block, immediately after the `"flee"` case (after line 24) and before the `"check"` case:

```csharp
                case "send_raw" when (args.GetValueOrDefault("command") as string)?.Equals("recall", StringComparison.OrdinalIgnoreCase) == true:
                    UpdateRoomFromRecall(store, result);
                    break;
```

Add a new private method, after `UpdateRoomFromLookOrMove` (after line 71):

```csharp
    private static void UpdateRoomFromRecall(KnowledgeStore store, string result)
    {
        // Unlike move/flee, a rejected or unavailable recall is unambiguous --
        // the character demonstrably never left their current room, so (unlike
        // the dark-room case UpdateRoomFromLookOrMove has to guess about) a
        // parse failure here must NOT clear position.
        var parsed = MudTextParser.ParseRoomBlock(result);
        if (parsed is null) return;

        var room = store.UpsertRoom(parsed.Value.Name, parsed.Value.Description);
        store.SetCurrentRoom(room.Id);
    }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter "RecallSuccess_UpdatesCurrentRoom|RecallRejected_LeavesCurrentRoomUnchanged|SendRawNonRecallCommand_IsIgnored"`
Expected: all three pass.

- [x] **Step 5: Verify the full `KnowledgeHooksTests` suite still passes** (the 3 pre-existing tests are unaffected by this addition):

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeHooksTests`
Expected: 6 tests pass (3 existing + 3 new).

- [x] **Step 6: Commit** — deferred to the final batched commit (Task 6).

---

## Task 5: `ExplorationPlanner` — confidence ranking, threshold-gated stop, and retreat

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/ExplorationPlanner.cs` (full rewrite)
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/ExplorationPlannerTests.cs` (full rewrite — extends the fixture, updates all 5 existing tests' signatures/assertions, adds 5 new tests)

**Interfaces:**
- Consumes: `RoomGraph.ExitConfidence`/`RoomMatchesQuery`/`FindPath` (Task 1, base spec), `MudTextParser.OppositeDirection` (Task 2), `Logger.ExplorationStep`/`ExplorationRetreat` (Task 3), the `send_raw`/`recall` behavior in `KnowledgeHooks` (Task 4), `KnowledgeStore`/`Registry`/`AgentHooks` (existing), `RouteResult` (existing, unchanged shape).
- Produces: `ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks, Logger logger)` with `ExploreTowardsAsync(string destinationQuery, int maxSteps, double confidenceThreshold) -> Task<RouteResult>`. Task 6 (`BoukenshaHost`) consumes both the constructor and the method's new third parameter.

### Step 1: Write the failing tests

Replace `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/ExplorationPlannerTests.cs` in full:

```csharp
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
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ExplorationPlannerTests`
Expected: build failure — `ExplorationPlanner`'s constructor and `ExploreTowardsAsync` signature don't match yet.

### Step 3: Implement the new `ExplorationPlanner`

Replace `week2_capable/dotnet/src/Boukensha.Core/Knowledge/ExplorationPlanner.cs` in full:

```csharp
namespace Boukensha.Core.Knowledge;

public sealed class ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks, Logger logger)
{
    public async Task<RouteResult> ExploreTowardsAsync(string destinationQuery, int maxSteps, double confidenceThreshold)
    {
        var startRoom = store.GetCurrentRoom();
        if (startRoom is null)
        {
            return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
        }

        var discovered = new HashSet<int>();
        var unresolved = new HashSet<(int RoomId, string Direction)>();
        var pathTaken = new List<string>();
        var stepsUsed = 0;
        var stepIndex = 0;

        while (stepsUsed < maxSteps)
        {
            var current = store.GetCurrentRoom();
            if (current is null)
            {
                return await RetreatAsync(startRoom, "unresolved_position", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            var candidate = NextFrontierCandidate(current.Id, destinationQuery, unresolved);
            if (candidate is null)
            {
                return await RetreatAsync(startRoom, "exhausted", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            var (targetRoomId, direction, hint, confidence) = candidate.Value;
            stepIndex++;

            if (confidence < confidenceThreshold)
            {
                logger.ExplorationStep(stepIndex, targetRoomId, direction, hint, confidence, explored: false);
                return await RetreatAsync(startRoom, "stuck", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            logger.ExplorationStep(stepIndex, targetRoomId, direction, hint, confidence, explored: true);

            if (targetRoomId != current.Id)
            {
                var approach = RoomGraph.FindPath(store, current.Id, targetRoomId);
                if (approach is null)
                {
                    unresolved.Add((targetRoomId, direction));
                    continue;
                }

                var ranOutOfBudget = false;
                foreach (var stepDirection in approach)
                {
                    if (stepsUsed >= maxSteps) { ranOutOfBudget = true; break; }
                    await DispatchAsync("move", new Dictionary<string, object?> { ["direction"] = stepDirection });
                    stepsUsed++;
                    pathTaken.Add(stepDirection);
                }
                if (ranOutOfBudget) break;
            }

            if (stepsUsed >= maxSteps) break;

            // After any approach-walking above, we're now standing in targetRoomId's
            // room regardless of where `current` was at the top of this iteration.
            var beforeMoveRoomId = targetRoomId;
            await DispatchAsync("move", new Dictionary<string, object?> { ["direction"] = direction });
            stepsUsed++;

            var afterMove = store.GetCurrentRoom();
            if (afterMove is null || afterMove.Id == beforeMoveRoomId)
            {
                // The exit didn't resolve to a recognizable new room. KnowledgeHooks
                // cannot tell "genuinely rejected, stayed put" apart from "did move,
                // into an unlit room" -- both fail to parse as a room block -- so it
                // has already (correctly) cleared position to unknown rather than
                // guess. Retreat still tries recall (which doesn't depend on knowing
                // current position at all) before giving up.
                return await RetreatAsync(startRoom, "unresolved_position", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            pathTaken.Add(direction);
            if (afterMove.VisitCount == 1) discovered.Add(afterMove.Id);

            await DispatchAsync("check", new Dictionary<string, object?> { ["kind"] = "exits" });

            if (RoomGraph.RoomMatchesQuery(afterMove, destinationQuery))
            {
                var route = RoomGraph.FindPath(store, startRoom.Id, afterMove.Id) ?? [];
                return new RouteResult(true, afterMove.Name, route,
                    $"Route to '{afterMove.Name}': {string.Join(", ", route)} ({route.Count} step{(route.Count == 1 ? "" : "s")}). " +
                    $"Discovered {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} while exploring.");
            }
        }

        var frontiersRemaining = CountFrontiers();
        return new RouteResult(false, null, [],
            $"Still exploring for '{destinationQuery}': {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found, " +
            $"{frontiersRemaining} frontier{(frontiersRemaining == 1 ? "" : "s")} remaining. Call plan_route again to continue.");
    }

    private (int RoomId, string Direction, string? Hint, double Confidence)? NextFrontierCandidate(
        int currentRoomId, string destinationQuery, HashSet<(int RoomId, string Direction)> unresolved)
    {
        (int RoomId, string Direction, string? Hint, double Confidence, int Distance)? best = null;

        foreach (var room in store.ListRooms())
        {
            foreach (var exit in store.ListExits(room.Id).Where(e => e.State == "frontier"))
            {
                if (unresolved.Contains((room.Id, exit.Direction))) continue;

                int distance;
                if (room.Id == currentRoomId)
                {
                    distance = 0;
                }
                else
                {
                    var path = RoomGraph.FindPath(store, currentRoomId, room.Id);
                    if (path is null) continue;
                    distance = path.Count;
                }

                var confidence = RoomGraph.ExitConfidence(exit, destinationQuery);

                if (best is null
                    || confidence > best.Value.Confidence
                    || (confidence == best.Value.Confidence && distance < best.Value.Distance))
                {
                    best = (room.Id, exit.Direction, exit.Hint, confidence, distance);
                }
            }
        }

        return best is null ? null : (best.Value.RoomId, best.Value.Direction, best.Value.Hint, best.Value.Confidence);
    }

    private async Task<RouteResult> RetreatAsync(
        RoomRecord startRoom, string reason, List<string> pathTaken, HashSet<int> discovered,
        int stepsUsed, string destinationQuery, double confidenceThreshold)
    {
        var recalled = await TryRecallAsync(startRoom, pathTaken);
        var frontiersRemaining = CountFrontiers();

        logger.ExplorationRetreat(reason, stepsUsed, discovered.Count, frontiersRemaining, recalled);

        if (reason == "exhausted")
        {
            return new RouteResult(false, null, [],
                $"Explored the full known map ({discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found) -- no room matching '{destinationQuery}' exists.");
        }

        var leadSentence = reason == "stuck"
            ? $"No promising leads for '{destinationQuery}' (best candidate confidence below {confidenceThreshold:0.0})."
            : "Lost track of position after an unresolved move.";
        var recalledClause = recalled ? $" Recalled back to '{startRoom.Name}'." : "";

        return new RouteResult(false, null, [],
            $"{leadSentence}{recalledClause} {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found, " +
            $"{frontiersRemaining} frontier{(frontiersRemaining == 1 ? "" : "s")} remain unexplored. " +
            "Call plan_route again to keep exploring, or try a different name for the destination.");
    }

    private async Task<bool> TryRecallAsync(RoomRecord startRoom, List<string> pathTaken)
    {
        await DispatchAsync("send_raw", new Dictionary<string, object?> { ["command"] = "recall" });

        var afterRecall = store.GetCurrentRoom();
        if (afterRecall is not null && afterRecall.Id == startRoom.Id) return true;
        if (afterRecall is null) return false; // position was already/still unknown -- nothing to retrace from

        // Recall didn't land back at the origin (unavailable, on cooldown, an
        // unparseable response, or it teleported somewhere other than startRoom)
        // -- retrace this call's own moves in reverse instead of consulting the
        // graph. See the spec for why RoomGraph.FindPath is not used here.
        for (var i = pathTaken.Count - 1; i >= 0; i--)
        {
            await DispatchAsync("move", new Dictionary<string, object?> { ["direction"] = MudTextParser.OppositeDirection(pathTaken[i]) });
        }

        return store.GetCurrentRoom()?.Id == startRoom.Id;
    }

    private int CountFrontiers() =>
        store.ListRooms().Sum(room => store.ListExits(room.Id).Count(e => e.State == "frontier"));

    private async Task<string> DispatchAsync(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        string result;
        var ok = true;
        try
        {
            result = await registry.DispatchAsync(toolName, args);
        }
        catch (Exception e)
        {
            ok = false;
            result = $"ERROR: {e.GetType().Name}: {e.Message}";
        }
        await hooks.RaiseAfterToolCall(toolName, args, result, ok, CancellationToken.None);
        return result;
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ExplorationPlannerTests`
Expected: all 10 tests pass (6 pre-existing, signatures updated, one — the unresolved-exit test — with updated message assertions + 4 new). If `ExploreTowardsAsync_PrefersHigherConfidenceCandidateOverNearerUnprovenOne` or `ExploreTowardsAsync_RecallFails_FallsBackToRetracingOwnStepsInReverse` fail, check the room-block text used in `moveResponses` factories matches exactly (name line + description line) what `UpsertRoom` was originally called with in the test setup — a mismatched description produces a different SHA-256 fingerprint and a duplicate room, which silently breaks the `Id` comparisons these tests rely on.

- [x] **Step 5: Verify no regressions in `RoutePlannerTests`** (untouched by this task, but shares `RoomGraph`):

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoutePlannerTests`
Expected: all 9 tests still pass.

- [x] **Step 6: Commit** — deferred to the final batched commit (Task 6).

---

## Task 6: Wire into `BoukenshaHost`, full-suite verification, and commit

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs`

**Interfaces:**
- Consumes: `ExplorationPlanner`'s new constructor and `ExploreTowardsAsync` signature (Task 5); `Config.AgentExplorationConfidenceThreshold` (Task 3).

- [x] **Step 1: Update the `ExplorationPlanner` construction and `plan_route` handler**

Modify `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs` — line 89, pass `logger` into the constructor:

```csharp
        var explorationPlanner = new Knowledge.ExplorationPlanner(knowledgeStore, registry, agentHooks, logger);
```

And line 108, pass the new threshold argument:

```csharp
                    result = await explorationPlanner.ExploreTowardsAsync(destination, config.AgentExplorationMaxSteps, config.AgentExplorationConfidenceThreshold);
```

(`logger` is already constructed above this point at line 74, and `config` is already in scope — both are simple additions to an existing call, no new fields or parameters on `BoukenshaHost` itself.)

- [x] **Step 2: Build the full solution**

Run: `dotnet build week2_capable/dotnet/Boukensha.slnx`
Expected: success, no new warnings.

- [x] **Step 3: Run the full test suite**

Run: `dotnet test week2_capable/dotnet/Boukensha.slnx`
Expected: all tests pass — the pre-existing suite (87 tests as of the base frontier-exploration feature) plus this plan's additions: 6 (`RoomGraphTests`) + 6 (`OppositeDirection` theory cases) + 2 (`LoggerTests`) + 3 (`KnowledgeHooksTests`) + 11 (`ExplorationPlannerTests`, replacing the prior 6) = 108 total.

- [x] **Step 4: Update spec status**

Modify `docs/plans/week_2/specs/frontier_exploration_confidence.md` line 3 — change `Status: draft` to `Status: implemented (pending live verification)` once Steps 2–3 are green. (A live-MUD verification pass, mirroring the base spec's Task 3, is recommended before flipping this to fully "verified" — the `recall` command's real availability/behavior for this session's test character is unconfirmed against the live server; see the spec's "Retreat on stop" section.)

- [x] **Step 5: Commit**

```bash
git add week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoomGraph.cs \
        week2_capable/dotnet/src/Boukensha.Core/Knowledge/MudTextParser.cs \
        week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeHooks.cs \
        week2_capable/dotnet/src/Boukensha.Core/Knowledge/ExplorationPlanner.cs \
        week2_capable/dotnet/src/Boukensha.Core/Config.cs \
        week2_capable/dotnet/src/Boukensha.Core/Logger.cs \
        week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/RoomGraphTests.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/MudTextParserTests.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/KnowledgeHooksTests.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/ExplorationPlannerTests.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/LoggerTests.cs \
        docs/plans/week_2/specs/frontier_exploration_confidence.md \
        docs/plans/week_2/plans/frontier_exploration_confidence_plan.md
git commit -m "$(cat <<'EOF'
dotnet: confidence-ranked exploration with recall-based retreat

ExplorationPlanner now ranks frontier candidates by destination-hint
confidence (RoomGraph.ExitConfidence) instead of pure nearest-distance,
logs the reasoning per step, and stops chasing once nothing clears
agent.exploration_confidence_threshold (default 0.5) -- retreating to
the room exploration started from via a recall command, falling back
to retracing this call's own moves in reverse (MudTextParser.
OppositeDirection) if recall fails to resolve.

KnowledgeHooks gains a send_raw/recall case, deliberately not sharing
flee's clear-on-parse-failure behavior since a rejected recall is
unambiguous, unlike a dark room.
EOF
)"
```

- [x] **Step 6: Verify**

Run: `git status`
Expected: clean working tree, one new commit on top of the base frontier-exploration work.

---

## Notes for the implementer

- **Live verification is out of scope for this plan's automated steps** (mirroring how the base spec's Task 3 required a real MUD connection). Before trusting `recall` in production, confirm live whether the "dummy"/"helloworld" test character can actually issue it, and what CircleMUD's exact accept/reject text looks like — `MudTextParser.ParseRoomBlock`'s existing dark-room/closed-door parsing gaps (see the base plan's Task 3 findings) are exactly the kind of thing that only shows up against a real, imperfect server.
- If live verification finds `recall` is never available to this character, the fallback (retracing this call's own moves) is still fully functional on its own — `TryRecallAsync` degrades gracefully since `send_raw` dispatching a rejected command just means `afterRecall.Id != startRoom.Id`, immediately falling into the retrace branch.
