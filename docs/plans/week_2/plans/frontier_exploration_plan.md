# Frontier-Ranked Autonomous Exploration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent.
>
> Spec: `docs/plans/week_2/specs/frontier_exploration.md`.

**Goal:** When `plan_route` can't resolve a destination from the player's current location, automatically drive a deterministic frontier-ranked walk (no LLM turns spent per step) that discovers new rooms until the destination is found, the known map is exhausted, or a step budget is hit — replacing today's "not found, here's a hint" dead end.

**Architecture:** A shared `RoomGraph` helper (BFS path-finding + name matching) is extracted out of `RoutePlanner` so both it and a new `ExplorationPlanner` use exactly one implementation of each. `ExplorationPlanner` drives `move`/`check` tool calls itself via `Registry.DispatchAsync`, raising the same `AgentHooks.RaiseAfterToolCall` event the main agent loop raises — so the *existing* `KnowledgeHooks` recording logic captures every room/exit discovered, with no duplicated recording code and no LLM conversation messages added per step. `plan_route`'s tool handler in `BoukenshaHost` falls back to it automatically whenever `RoutePlanner.FindRoute` fails to resolve a destination from the current room.

**Tech Stack:** No new dependencies — pure C#/.NET additions to the existing `Boukensha.Core` project.

## Global Constraints

- `RoutePlanner.FindRoute`'s public signature and all 9 existing `RoutePlannerTests` must continue to pass **unmodified** after the `RoomGraph` extraction — this is a pure refactor, not a behavior change.
- Exploration only ever starts from the player's actual current room (`KnowledgeStore.GetCurrentRoom()`) — never from an arbitrary `from` room someone named, since it physically moves the character via real `move` calls. If `plan_route`'s `from` argument was explicitly provided and didn't resolve, today's plain "no known room matching starting point" message is unchanged; exploration does not apply.
- Exploration must never call `Context.AddMessage` — internal steps stay out of the LLM's conversation entirely. This is the actual token-efficiency property being built; get it wrong and this whole feature just moves the token cost around instead of removing it.
- Default step budget is 30 `move` calls per `plan_route` invocation, configurable via `agent.exploration_max_steps` in `settings.yaml`, following the exact pattern `Config.AgentMaxTurnTokens`/`AgentCompactionThreshold` already use.
- No persisted "blocked" exit state, no per-room item/monster/observation capture, no standalone no-target explore tool — all explicitly out of scope per the spec.

---

## Task 1: Extract `RoomGraph` and refactor `RoutePlanner` onto it (regression-tested)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoomGraph.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoutePlanner.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/RoutePlannerTests.cs` (existing file — **not modified**, its current 9 tests are the regression check)

**Interfaces:**
- Produces: `RoomGraph.RoomMatchesQuery(RoomRecord room, string query) -> bool`; `RoomGraph.FindBestMatch(IReadOnlyList<RoomRecord> rooms, string query) -> RoomRecord?`; `RoomGraph.FindPath(KnowledgeStore store, int startId, int targetId) -> IReadOnlyList<string>?`. Task 2 (`ExplorationPlanner`) consumes all three.

- [ ] Write `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoomGraph.cs`:
```csharp
namespace Boukensha.Core.Knowledge;

/// <summary>
/// Shared graph queries used by both RoutePlanner (known-route BFS) and
/// ExplorationPlanner (frontier-ranked walking), so there's exactly one
/// path-finding implementation and one name-matching rule instead of two
/// copies that could drift apart.
/// </summary>
public static class RoomGraph
{
    public static bool RoomMatchesQuery(RoomRecord room, string query) =>
        room.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
        || room.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

    public static RoomRecord? FindBestMatch(IReadOnlyList<RoomRecord> rooms, string query) =>
        rooms.FirstOrDefault(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? rooms.FirstOrDefault(r => RoomMatchesQuery(r, query));

    public static IReadOnlyList<string>? FindPath(KnowledgeStore store, int startId, int targetId)
    {
        var visited = new HashSet<int> { startId };
        var queue = new Queue<(int RoomId, List<string> Path)>();
        queue.Enqueue((startId, []));

        while (queue.Count > 0)
        {
            var (roomId, path) = queue.Dequeue();
            if (roomId == targetId) return path;

            foreach (var exit in store.ListExits(roomId).Where(e => e.State == "walked" && e.ToRoomId is not null))
            {
                var nextId = exit.ToRoomId!.Value;
                if (visited.Add(nextId))
                {
                    queue.Enqueue((nextId, [.. path, exit.Direction]));
                }
            }
        }
        return null;
    }
}
```
- [x] Run: `dotnet build week2_capable/dotnet/Boukensha.slnx` — expect success (new file compiles standalone, nothing references it yet).
- [ ] Replace `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoutePlanner.cs`'s body with the refactored version — same public behavior, now delegating to `RoomGraph`:
```csharp
namespace Boukensha.Core.Knowledge;

public sealed record RouteResult(bool Found, string? DestinationRoomName, IReadOnlyList<string> Directions, string Message);

public sealed class RoutePlanner(KnowledgeStore store)
{
    public RouteResult FindRoute(string destinationQuery, string? fromQuery = null)
    {
        RoomRecord? start;
        if (fromQuery is null)
        {
            start = store.GetCurrentRoom();
            if (start is null)
            {
                return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
            }
        }
        else
        {
            start = RoomGraph.FindBestMatch(store.ListRooms(), fromQuery);
            if (start is null)
            {
                return new RouteResult(false, null, [], $"No known room matching starting point '{fromQuery}'.");
            }
        }

        var destination = RoomGraph.FindBestMatch(store.ListRooms(), destinationQuery);

        if (destination is null)
        {
            return new RouteResult(false, null, [], $"No known room matching '{destinationQuery}'.{FrontierHint(start.Id)}");
        }

        if (destination.Id == start.Id)
        {
            return new RouteResult(true, destination.Name, [], $"You are already at '{destination.Name}'.");
        }

        var path = RoomGraph.FindPath(store, start.Id, destination.Id);
        if (path is null)
        {
            return new RouteResult(false, destination.Name, [],
                $"'{destination.Name}' is known but no walked path from '{start.Name}' has been found yet.{FrontierHint(start.Id)}");
        }

        return new RouteResult(true, destination.Name, path,
            $"Route to '{destination.Name}': {string.Join(", ", path)} ({path.Count} step{(path.Count == 1 ? "" : "s")}).");
    }

    private string FrontierHint(int roomId)
    {
        var frontier = store.ListExits(roomId).Where(e => e.State == "frontier").Select(e => e.Direction).ToList();
        return frontier.Count > 0
            ? $" Unexplored exits from here: {string.Join(", ", frontier)}."
            : " No unexplored exits known from here either.";
    }
}
```
  (The private `FindPath` method is gone entirely — moved to `RoomGraph.FindPath`. `FrontierHint` is unchanged. Message text and matching semantics are identical to before; only where the logic lives has changed.)
- [x] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoutePlannerTests` — expect all 9 existing tests to pass **unmodified**. This *is* the regression check the spec calls for — no new test code needed, since the refactor must not change observable behavior at all.
- [x] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds with no new warnings.
- [x] Commit (deferred to the final batched commit, per this session's cadence).

---

## Task 2: `ExplorationPlanner` (frontier-ranked walk, TDD'd against a fake MUD) + wiring into `plan_route`

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/ExplorationPlanner.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/ExplorationPlannerTests.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Config.cs` (new `AgentExplorationMaxSteps` setting)
- Modify: `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs` (wire the automatic fallback into `plan_route`)

**Interfaces:**
- Consumes: `RoomGraph.FindPath`/`RoomMatchesQuery` (Task 1); `KnowledgeStore.GetCurrentRoom`/`ListRooms`/`ListExits`/`SetCurrentRoom` (existing); `Registry.DispatchAsync(string, IReadOnlyDictionary<string,object?>) -> Task<string>` (existing); `AgentHooks.RaiseAfterToolCall(string, IReadOnlyDictionary<string,object?>, string, bool, CancellationToken) -> Task` (existing); `RouteResult` record (Task 1's file, unchanged shape).
- Produces: `ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks)` with `ExploreTowardsAsync(string destinationQuery, int maxSteps) -> Task<RouteResult>`.

### Step 1: Write the failing tests

The test fixture builds a real `Registry` + `AgentHooks` pair with the *actual* `KnowledgeHooks.Register` wired in (so recording happens exactly as it does in production), backed by fake `move`/`check` tool handlers that return scripted MUD text instead of talking to a real server. `MudTextParser.ParseRoomBlock` requires a `[ Exits: ... ]` line to recognize a response as a room (the exact letters don't matter — `KnowledgeHooks` never reads them, only the parsed name/description), and `check kind=exits` responses use the `direction - Destination Name` format `MudTextParser.ParseExitsBlock` expects.

Create `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/ExplorationPlannerTests.cs`:
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

    private static (Registry Registry, AgentHooks Hooks) BuildFakeMud(
        KnowledgeStore store,
        string startRoomName,
        Dictionary<(string From, string Direction), Func<string>> moveResponses,
        Dictionary<string, string> exitsResponses)
    {
        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);
        var registry = new Registry(context);

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

        return (registry, hooks);
    }

    [Fact]
    public async Task ExploreTowardsAsync_FindsMultiHopTarget()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Hallway" });

        var (registry, hooks) = BuildFakeMud(store, "Start",
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

        var result = await new ExplorationPlanner(store, registry, hooks).ExploreTowardsAsync("Bakery", maxSteps: 10);

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

        var (registry, hooks) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Closet\nA tiny closet, no other exits.\n[ Exits: s ]",
            },
            exitsResponses: new());

        var result = await new ExplorationPlanner(store, registry, hooks).ExploreTowardsAsync("Nonexistent", maxSteps: 10);

        Assert.False(result.Found);
        Assert.Contains("Explored the full known map (1 new room found)", result.Message);
        Assert.Contains("Nonexistent", result.Message);
    }

    [Fact]
    public async Task ExploreTowardsAsync_RespectsStepBudget_ReturnsStillExploringStatus()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Hallway" });

        var (registry, hooks) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "north")] = () => "Hallway\nA narrow hallway.\n[ Exits: e ]",
            },
            exitsResponses: new()
            {
                ["Hallway"] = "east - Bakery",
            });

        var result = await new ExplorationPlanner(store, registry, hooks).ExploreTowardsAsync("Bakery", maxSteps: 1);

        Assert.False(result.Found);
        Assert.Contains("Still exploring for 'Bakery'", result.Message);
        Assert.Contains("1 new room found", result.Message);
        Assert.Contains("1 frontier", result.Message);
        Assert.Contains("Call plan_route again", result.Message);
    }

    [Fact]
    public async Task ExploreTowardsAsync_UnresolvedExit_StopsCleanlyWithoutGuessingPosition_RetriedAfterPositionRecovered()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["east"] = "Garden" });

        var attempts = 0;
        var (registry, hooks) = BuildFakeMud(store, "Start",
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

        var planner = new ExplorationPlanner(store, registry, hooks);

        var firstAttempt = await planner.ExploreTowardsAsync("Garden", maxSteps: 5);
        Assert.False(firstAttempt.Found);
        Assert.Contains("0 new room", firstAttempt.Message);
        Assert.Contains("1 frontier", firstAttempt.Message);
        Assert.Null(store.GetCurrentRoom()); // position genuinely unknown, not guessed back to Start

        // A real agent recovers via a normal `look` call at this point; simulate that here
        // rather than depending on a live MUD round trip just to re-establish position.
        store.SetCurrentRoom(start.Id);

        var secondAttempt = await planner.ExploreTowardsAsync("Garden", maxSteps: 5);
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

        var (registry, hooks) = BuildFakeMud(store, "Start",
            moveResponses: new()
            {
                [("Start", "east")] = () => "Storage\nA cramped storage room.\n[ Exits: w ]",
                [("Storage", "west")] = () => "Start\nThe starting room.\n[ Exits: n e ]",
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

        var result = await new ExplorationPlanner(store, registry, hooks).ExploreTowardsAsync("Bakery", maxSteps: 10);

        // Storage is a dead end for forward progress (its only exit leads back to Start),
        // so the walk must return to Start and go explore Hallway instead -- driven purely
        // by "which known frontier is nearest", no dead-end counter or special-casing.
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

        var (registry, hooks) = BuildFakeMud(store, "B",
            moveResponses: new()
            {
                [("B", "south")] = () => "A\nRoom A.\n[ Exits: n e ]",
                [("A", "east")] = () => "Bakery\nSmells of fresh bread.\n[ Exits: w ]",
            },
            exitsResponses: new()
            {
                ["Bakery"] = "west - A",
            });

        var result = await new ExplorationPlanner(store, registry, hooks).ExploreTowardsAsync("Bakery", maxSteps: 10);

        // Reaching the nearest frontier (at A) takes one approach move (B -> south -> A)
        // before the frontier-crossing move (A -> east -> Bakery) itself.
        Assert.True(result.Found);
        Assert.Equal(["south", "east"], result.Directions);
        Assert.Contains("Discovered 1 new room", result.Message);
    }
}
```
- [x] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ExplorationPlannerTests` — expect a build failure (`ExplorationPlanner` doesn't exist yet).

### Step 2: Implement `ExplorationPlanner`

Create `week2_capable/dotnet/src/Boukensha.Core/Knowledge/ExplorationPlanner.cs`:
```csharp
namespace Boukensha.Core.Knowledge;

public sealed class ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks)
{
    public async Task<RouteResult> ExploreTowardsAsync(string destinationQuery, int maxSteps)
    {
        var startRoom = store.GetCurrentRoom();
        if (startRoom is null)
        {
            return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
        }

        var discovered = new HashSet<int>();
        var unresolved = new HashSet<(int RoomId, string Direction)>();
        var stepsUsed = 0;

        while (stepsUsed < maxSteps)
        {
            var current = store.GetCurrentRoom();
            if (current is null) break;

            var candidate = NextFrontierCandidate(current.Id, unresolved);
            if (candidate is null) break;

            var (targetRoomId, direction) = candidate.Value;

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
                // cannot tell "genuinely rejected, stayed put" ("Alas, you cannot go
                // that way") apart from "did move, into an unlit room" ("It is pitch
                // black...") -- both fail to parse as a room block -- so it has already
                // (correctly) cleared position to unknown rather than guess. Overwriting
                // that with an assumed "stayed put" would desync the knowledge store from
                // the real game state whenever it was actually the dark-room case; live
                // verification against a real dungeon with dark rooms hit exactly this,
                // producing an endless "0 new rooms" loop. So: stop here rather than
                // continue on a possibly-wrong position -- the next plan_route call will
                // surface "current location unknown -- look around first" via
                // RoutePlanner's own existing guard, prompting a normal recovery look.
                unresolved.Add((targetRoomId, direction));
                break;
            }

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
        return frontiersRemaining == 0
            ? new RouteResult(false, null, [],
                $"Explored the full known map ({discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found) -- no room matching '{destinationQuery}' exists.")
            : new RouteResult(false, null, [],
                $"Still exploring for '{destinationQuery}': {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found, " +
                $"{frontiersRemaining} frontier{(frontiersRemaining == 1 ? "" : "s")} remaining. Call plan_route again to continue.");
    }

    private (int RoomId, string Direction)? NextFrontierCandidate(int currentRoomId, HashSet<(int RoomId, string Direction)> unresolved)
    {
        (int RoomId, string Direction, int Distance)? best = null;

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

                if (best is null || distance < best.Value.Distance)
                {
                    best = (room.Id, exit.Direction, distance);
                }
            }
        }

        return best is null ? null : (best.Value.RoomId, best.Value.Direction);
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
- [x] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ExplorationPlannerTests` — expect all 6 tests to pass. (Passed on first implementation; the "unresolved exit" test and the `beforeMoveRoomId`/position-restore logic shown above were later revised during Task 3's live verification — see that task's notes. The code block above already reflects the final, corrected version.)

### Step 3: Add the `agent.exploration_max_steps` setting

Modify `week2_capable/dotnet/src/Boukensha.Core/Config.cs` — add a new property immediately after the existing `AgentCompactionThreshold` property:
```csharp
    public double AgentCompactionThreshold => Convert.ToDouble(Dig("agent", "compaction_threshold") ?? 0.85);

    public int AgentExplorationMaxSteps => Convert.ToInt32(Dig("agent", "exploration_max_steps") ?? 30);
```

### Step 4: Wire the automatic fallback into `plan_route`

Modify `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs` — replace the existing `plan_route` registration block:
```csharp
        var routePlanner = new Knowledge.RoutePlanner(knowledgeStore);
        var explorationPlanner = new Knowledge.ExplorationPlanner(knowledgeStore, registry, agentHooks);
        registry.Tool("plan_route",
            "Find a route between two previously-visited rooms by name. If 'from' is omitted, plans from your " +
            "current location, automatically exploring unmapped territory if the destination isn't known yet -- " +
            "if the result says 'still exploring', call plan_route again with the same destination to continue. " +
            "Returns step-by-step directions once found, or suggests unexplored exits if 'from' was given " +
            "explicitly and couldn't be resolved.",
            new Dictionary<string, ToolParameter>
            {
                ["destination"] = new("string", "Name of the destination room"),
                ["from"] = new("string", "Name of the starting room (optional -- defaults to your current location)"),
            },
            async args =>
            {
                var destination = args.GetValueOrDefault("destination") as string ?? "";
                var from = args.GetValueOrDefault("from") as string;
                var result = routePlanner.FindRoute(destination, from);
                if (!result.Found && from is null)
                {
                    result = await explorationPlanner.ExploreTowardsAsync(destination, config.AgentExplorationMaxSteps);
                }
                return result.Message;
            });
```
(This replaces the single-line `args => Task.FromResult(routePlanner.FindRoute(...).Message)` handler with the async version above — same tool name, same parameter schema, only the handler body and description change.)

- [x] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [x] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — full suite passes (existing tests + 6 new `ExplorationPlannerTests`).
- [x] Commit (deferred to the final batched commit, per this session's cadence).

---

## Task 3: End-to-end verification

**Files:** none (verification only) — plus two small bug fixes discovered along the way (see below).

- [x] Ensure a `.boukensha/settings.yaml` exists pointing at a reachable MUD server (this session's existing `dummy`/`helloworld` character against the live tbaMUD instance, per this repo's established setup) and `ANTHROPIC_API_KEY` is set.
- [x] Run the console app and, from a room with at least one unexplored exit, ask it to go to a destination **not yet in the knowledge store** ("go to the bakery") — confirm the transcript shows only `plan_route` tool calls doing the searching, not the agent manually issuing its own `move`/`check` tool calls.
- [x] If the result was "still exploring", call it again and confirm forward progress until it either finds the destination or reports the map exhausted.
- [x] Open the observability viewer at `/Knowledge/Map` and confirm the rooms discovered during exploration appear with correct walked connections.
- [x] Confirm the session log shows no per-step `move`/`check` messages leaked into the LLM's conversation from *inside* `ExplorationPlanner` (the token-efficiency property).
- [x] Update this plan's checkboxes and the spec's status line.
- [x] Commit (final) — single commit for all of Tasks 1–3 plus the two fixes below, matching this session's established batching preference.

### Findings during live verification

The very first live run reproduced the exact "go to the bakery" scenario from the journal — and immediately surfaced a real bug that no unit test had caught, because the fake-MUD test fixtures never exercised what a *real* dark room does.

**Bug 1 — position desync on dark rooms (in the code just written this task).** `ExplorationPlanner`'s original unresolved-exit handling called `store.SetCurrentRoom(beforeMoveRoomId)`, assuming a failed move meant "stayed put." Live against the real dungeon, `plan_route` looped forever reporting identical "0 new rooms, 3 frontiers remaining" on every call — real moves were happening (visit counts climbing) but no progress ever registered. Root cause, found by reading the raw telnet log: CircleMUD has two distinct move-failure messages, `"It is pitch black..."` (the move *did* succeed, into an unlit room) and `"Alas, you cannot go that way..."` (genuinely rejected) — and `MudTextParser.ParseRoomBlock` can't tell them apart, so `KnowledgeHooks` already (correctly) clears position to unknown for both rather than guess. `ExplorationPlanner`'s "restore to before" logic was silently overwriting that correct "unknown" with a false "definitely back where I started," desyncing the knowledge store from the real game state. Fixed by removing the restore and just stopping the call cleanly on unresolved position (see the corrected `ExplorationPlanner.cs` code block in Task 2 above, and the renamed/rewritten test `ExploreTowardsAsync_UnresolvedExit_StopsCleanlyWithoutGuessingPosition_RetriedAfterPositionRecovered`). Re-verified live: no more looping — `plan_route` now correctly reports "current location unknown -- look around first" when this happens, and the LLM recovers via a normal `look`/`move`, exactly like it already does for any other unexpected MUD state.

**Bug 2 — closed-door exits silently dropped (pre-existing, in `MudTextParser`, unrelated to this feature).** After the fix above, a genuinely new room (`The South End Of The Grand Pipe`) was discovered live but never appeared in the knowledge store. Cause: its compact exits line was `[ Exits: n (w) ]` — CircleMUD's syntax for "north is open, west is a closed door" — and `ExitsLinePattern`'s character class (`[a-z\s]*`) didn't allow parentheses, so the whole line failed to match, `ParseRoomBlock` returned null, and the room was silently treated the same as an unparseable dark-room result. This is a pre-existing gap in `MudTextParser` (present since the basic-memory sub-project), not something this feature introduced, but it was actively hiding this feature's own verification results, so it was fixed here: widened the regex to `[a-z\s()]*` and stripped parens when building `ExitLetters`, with a new regression test (`ParseRoomBlock_ParsesRoomWithClosedDoorExit`) using the exact captured live text. Re-verified live: the room now appears correctly in `/Knowledge` and `/Knowledge/Map` with its walked connection.

Both fixes are small, surgical, and covered by new tests (87 total, up from 86). Neither changes the frontier-exploration design itself — both are pre-existing-code correctness fixes that live verification against a real, imperfect dungeon (dark rooms, closed doors) was specifically positioned to catch, exactly as intended.
