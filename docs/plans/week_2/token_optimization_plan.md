# Token-Usage Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent.

**Goal:** Implement `docs/plans/week_2/token_optimization.md`: de-duplicate `[here]` block injection, and add a `plan_route` tool doing BFS pathfinding over the known room graph.

**Architecture:** `KnowledgeHooks` gains closure-captured dedup state; `KnowledgeStore.ExitRecord` gains a `ToRoomId` field; a new `RoutePlanner` class does pure in-memory BFS over `KnowledgeStore`'s existing read methods; `BoukenshaHost` registers `plan_route` as a native `Registry` tool.

**Tech Stack:** No new dependencies.

## Global Constraints

- `plan_route` only returns *known, walked* paths — never fabricates a route across `frontier` (unwalked) exits, only suggests them as exploration hints.
- `[here]` dedup must still inject on the very first call of a session (no false "already sent" state) and must re-inject whenever the underlying text actually changes (room change, revisit with a different visit count, exit state change).
- No active tool-call gating in this pass (explicitly out of scope per the design doc).

---

## Task 1: `[here]` block de-duplication (tested)

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeHooks.cs`
- Create: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/KnowledgeHooksTests.cs`

**Produces:** `KnowledgeHooks.Register` behaves identically for `AfterToolCall` handling; its `BeforeAgentCall` handler now skips injection when the computed `[here]` text matches the last text it injected.

- [ ] Write the failing tests, `KnowledgeHooksTests.cs`:
```csharp
using Boukensha.Core;
using Boukensha.Core.Knowledge;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class KnowledgeHooksTests
{
    private static KnowledgeStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("boukensha_knowledge_hooks_test").FullName, "knowledge.db"));

    [Fact]
    public async Task Register_BeforeAgentCall_DoesNotInjectDuplicateHereBlockWhileStationary()
    {
        using var store = NewStore();
        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);

        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);
        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);
        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);

        Assert.Single(context.Messages);
    }

    [Fact]
    public async Task Register_BeforeAgentCall_InjectsAgainWhenCurrentRoomChanges()
    {
        using var store = NewStore();
        var roomA = store.UpsertRoom("A", "a");
        var roomB = store.UpsertRoom("B", "b");
        store.SetCurrentRoom(roomA.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);

        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);
        store.SetCurrentRoom(roomB.Id);
        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);

        Assert.Equal(2, context.Messages.Count);
    }

    [Fact]
    public async Task Register_BeforeAgentCall_NoCurrentRoom_InjectsNothing()
    {
        using var store = NewStore();
        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);

        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);

        Assert.Empty(context.Messages);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeHooksTests` — expect the first two tests to fail (current behavior injects unconditionally every call, so `DoesNotInjectDuplicateHereBlockWhileStationary` would currently see 3 messages, not 1). The third test already passes today (empty `[here]` is already skipped) — that's fine, it's here as a regression guard, not a new-behavior test.
- [ ] Modify `KnowledgeHooks.cs`'s `Register` method — replace the `OnBeforeAgentCall` registration:
```csharp
    public static void Register(AgentHooks hooks, KnowledgeStore store)
    {
        string? lastInjected = null;

        hooks.OnAfterToolCall((name, args, result, ok, _) =>
        {
            if (!ok) return Task.CompletedTask;

            switch (name)
            {
                case "look" when string.IsNullOrEmpty(args.GetValueOrDefault("target") as string):
                    UpdateRoomFromLookOrMove(store, result, direction: null, isTransition: false);
                    break;
                case "move":
                    UpdateRoomFromLookOrMove(store, result, direction: args.GetValueOrDefault("direction") as string, isTransition: true);
                    break;
                case "flee":
                    // Flees in a random available direction -- MudManager doesn't tell us which,
                    // so no LinkExit call, but the resulting room (or lack thereof) still updates location.
                    UpdateRoomFromLookOrMove(store, result, direction: null, isTransition: true);
                    break;
                case "check" when (args.GetValueOrDefault("kind") as string) == "exits":
                    var current = store.GetCurrentRoom();
                    if (current is not null)
                    {
                        store.RecordExits(current.Id, MudTextParser.ParseExitsBlock(result));
                    }
                    break;
            }

            return Task.CompletedTask;
        });

        hooks.OnBeforeAgentCall((context, _) =>
        {
            var here = store.BuildHereBlock();
            if (!string.IsNullOrEmpty(here) && here != lastInjected)
            {
                context.AddMessage("user", here);
                lastInjected = here;
            }
            return Task.CompletedTask;
        });
    }
```
(Only the `OnBeforeAgentCall` body and the new `lastInjected` local change; `OnAfterToolCall`'s body and `UpdateRoomFromLookOrMove` are unchanged — reproduced above in full only so the task is self-contained.)
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeHooksTests` — expect all 3 pass.
- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — full suite still passes (regression check).
- [ ] Commit.

---

## Task 2: `ExitRecord` gains `ToRoomId`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeStore.cs`

**Modifies:** `ExitRecord(string Direction, string State, string? ToRoomName, string? Hint)` → `ExitRecord(string Direction, string State, string? ToRoomName, string? Hint, int? ToRoomId)`. One call site to update (`ListExits`'s construction).

- [ ] Change the record declaration:
```csharp
public sealed record ExitRecord(string Direction, string State, string? ToRoomName, string? Hint, int? ToRoomId);
```
- [ ] Update `ListExits`'s SQL and construction (add `e.to_room_id` to the `SELECT`, read it as column index 4):
```csharp
    public IReadOnlyList<ExitRecord> ListExits(int roomId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.direction, e.state, dest.name, e.to_room_name_hint, e.to_room_id
            FROM exits e LEFT JOIN rooms dest ON dest.id = e.to_room_id
            WHERE e.room_id = $roomId ORDER BY e.direction;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        using var reader = cmd.ExecuteReader();
        var exits = new List<ExitRecord>();
        while (reader.Read())
        {
            exits.Add(new ExitRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }
        return exits;
    }
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds. **Expected**: `Boukensha.Observability`'s `Pages/Knowledge/Index.cshtml` constructs display strings from `ExitRecord` by property name (`e.State`/`e.ToRoomName`/`e.Hint`), not positionally, so it should compile unchanged — confirm this is actually true by checking the build output has no errors in that file specifically.
- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — full suite passes (the existing `ListExits_IncludesWalkedDestinationNameAndFrontierHint` test only asserts on named properties already returned, unaffected by the new field being added).
- [ ] Commit.

---

## Task 3: `RoutePlanner` (BFS pathfinding, tested)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoutePlanner.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/RoutePlannerTests.cs`

**Consumes:** `KnowledgeStore.ListRooms`/`ListExits`/`GetCurrentRoom` (Task 2's `ExitRecord.ToRoomId`).
**Produces:** `RouteResult(bool Found, string? DestinationRoomName, IReadOnlyList<string> Directions, string Message)`; `RoutePlanner(KnowledgeStore store)` with `FindRoute(string destinationQuery) -> RouteResult`.

- [ ] Write the failing tests, `RoutePlannerTests.cs`:
```csharp
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
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoutePlannerTests` — expect build failure (`RoutePlanner` doesn't exist yet).
- [ ] Write `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoutePlanner.cs`:
```csharp
namespace Boukensha.Core.Knowledge;

public sealed record RouteResult(bool Found, string? DestinationRoomName, IReadOnlyList<string> Directions, string Message);

public sealed class RoutePlanner(KnowledgeStore store)
{
    public RouteResult FindRoute(string destinationQuery)
    {
        var current = store.GetCurrentRoom();
        if (current is null)
        {
            return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
        }

        var rooms = store.ListRooms();
        var destination = rooms.FirstOrDefault(r => r.Name.Equals(destinationQuery, StringComparison.OrdinalIgnoreCase))
            ?? rooms.FirstOrDefault(r => r.Name.Contains(destinationQuery, StringComparison.OrdinalIgnoreCase));

        if (destination is null)
        {
            return new RouteResult(false, null, [], $"No known room matching '{destinationQuery}'.{FrontierHint(current.Id)}");
        }

        if (destination.Id == current.Id)
        {
            return new RouteResult(true, destination.Name, [], $"You are already at '{destination.Name}'.");
        }

        var path = FindPath(current.Id, destination.Id);
        if (path is null)
        {
            return new RouteResult(false, destination.Name, [],
                $"'{destination.Name}' is known but no walked path from here has been found yet.{FrontierHint(current.Id)}");
        }

        return new RouteResult(true, destination.Name, path,
            $"Route to '{destination.Name}': {string.Join(", ", path)} ({path.Count} step{(path.Count == 1 ? "" : "s")}).");
    }

    private IReadOnlyList<string>? FindPath(int startId, int targetId)
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

    private string FrontierHint(int roomId)
    {
        var frontier = store.ListExits(roomId).Where(e => e.State == "frontier").Select(e => e.Direction).ToList();
        return frontier.Count > 0
            ? $" Unexplored exits from here: {string.Join(", ", frontier)}."
            : " No unexplored exits known from here either.";
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoutePlannerTests` — expect all 6 pass.
- [ ] Commit.

---

## Task 4: Wire `plan_route` into `BoukenshaHost`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs`

**Consumes:** `RoutePlanner` (Task 3).

- [ ] Insert immediately after `Knowledge.KnowledgeHooks.Register(agentHooks, knowledgeStore);` (currently line 86):
```csharp
        var routePlanner = new Knowledge.RoutePlanner(knowledgeStore);
        registry.Tool("plan_route",
            "Find a route from your current location to a previously-visited room by name. " +
            "Returns step-by-step directions if a known walked path exists, or suggests unexplored exits if not.",
            new Dictionary<string, ToolParameter> { ["destination"] = new("string", "Name of the destination room") },
            args => Task.FromResult(routePlanner.FindRoute(args.GetValueOrDefault("destination") as string ?? "").Message));
```
  This must land before `logger.ToolCatalog(context.Tools)` (currently line 125) so `plan_route` appears in the logged tool catalog — it already does, since this insertion point (right after line 86) is well before line 125.
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — full suite passes.
- [ ] Commit.

---

## Task 5: End-to-end verification

**Files:** none (verification only).

- [x] Ran: `dotnet test week2_capable/dotnet/Boukensha.slnx` — all 72 tests pass (66 from before this spec + 6 `RoutePlannerTests`; the 3 `KnowledgeHooksTests` from Task 1 are already included in the 66).
- [x] Ran: `dotnet clean week2_capable/dotnet/Boukensha.slnx && dotnet build week2_capable/dotnet/Boukensha.slnx` — 0 errors, 8 warnings, all the already-accepted NU1903 advisory (matching the count from the observability viewer sub-project, no new ones).
- [x] Reset the test character's connection state, then ran one live turn (task: look around, explore a few rooms, then use `plan_route` back to the starting room by name).
- [x] **`[here]` dedup confirmed working dramatically**: `The Grand Sewer` and `The Sewer Pipe` each appear exactly once in the session log (iterations 8 and 9 respectively) and never repeat across iterations 10–14, despite persisting in message history the whole time. Direct contrast with the memory sub-project's own original verification run, where the identical block repeated on every single iteration.
- [x] **`plan_route` confirmed correctly wired and invoked**: `tool_catalog` includes it; two real `tool_call`/`tool_result` pairs appear with real `destination` arguments (`"Sewer, First Level"`). Both calls happened while `GetCurrentRoom()` was null (the agent was in a dark/unparsed room at the time) — the tool correctly returned `"Current location is unknown -- look around first."` rather than guessing, exactly matching the design's case-1 guard and the `FindRoute_CurrentLocationUnknown_ReturnsNotFoundWithoutQuerying` unit test. The reachable-path branch itself wasn't exercised by this particular live run (the agent's timing landed in an unknown-location state both times it asked), but it's exhaustively covered by `RoutePlannerTests` (6/6 passing, including the exact reachable-multi-hop-path scenario) — judged sufficient rather than spending another billed call chasing that specific branch live.
- [x] Updated this plan's checkboxes and `docs/plans/week_2/token_optimization.md`'s status line to reflect completion.
- [ ] Commit (final) — single commit for all of Tasks 1–5, matching this session's established batching preference.
