# Token-Usage Optimization — Design

Status: implemented and verified (2026-08-10) — see `docs/plans/week_2/token_optimization_plan.md` for the task-by-task build log
Owner: Sam Alhambra
Sub-project 3 of 3 for week 2 (see `docs/journal/week2.md`'s Technical Goal).

## Purpose

Two concrete, journal-identified gaps, both left deliberately deferred by earlier sub-projects:

1. **`[here]` block de-duplication** — `docs/plans/week_2/basic_memory.md` explicitly scoped this out ("injects unconditionally every iteration... a token-optimization concern for the later sub-project, not this one"). Live verification during that sub-project's own Task 7 showed it happening in practice: the same `[here]` block accumulating in message history across 5+ consecutive iterations while the agent stayed in one room.
2. **A `plan_route` tool** — the journal's own conclusion after reviewing a real bakery-navigation run (Step 18): *"If an agent is trying to find a destination we need a tool_call to `plan_route` — if we already know the location, return the route; if not, reason where to look."* This targets the journal's actual headline problem (Step 1: ~65K tokens spent without reaching the destination) far more directly than any per-message text trimming — wasted *iterations* from blind wandering dwarf the token cost of a repeated status line.

**Explicitly out of scope**, confirmed with the user: active move-gating (denying/blocking `move` when the current room hasn't been surveyed). Left undecided in the memory sub-project ("the journal itself never confirmed which gating approach it settled on") — a genuinely open design question, not bundled into this pass.

## `[here]` block de-duplication

`KnowledgeHooks.Register(AgentHooks hooks, KnowledgeStore store)` (`Boukensha.Core.Knowledge`) gains a closure-captured `string? lastInjected = null`. Its `OnBeforeAgentCall` handler:
```
here = store.BuildHereBlock()
if here is non-empty AND here != lastInjected:
    context.AddMessage("user", here)
    lastInjected = here
```
`Register` is called once per CLI session (`BoukenshaHost.BuildAsync`), and the same `AgentHooks` instance is shared by every `Agent` the session's `AgentFactory` constructs (one per REPL turn) — so `lastInjected` correctly persists across turns, not just within one. Since `BuildHereBlock()`'s `(visit N)` count only changes on an actual `KnowledgeStore.UpsertRoom` call (i.e., a real `look`/`move` into that room), staying put across many iterations now injects the block exactly once, and revisiting a room later correctly re-injects (different visit count → different text → not a duplicate).

## `plan_route` tool

New `RoutePlanner` class (`Boukensha.Core.Knowledge`), operating purely through `KnowledgeStore`'s existing read methods (`ListRooms`, `ListExits`, `GetCurrentRoom`) — no new SQL, no direct `SqliteConnection` access:

```csharp
public sealed record RouteResult(bool Found, string? DestinationRoomName, IReadOnlyList<string> Directions, string Message);

public sealed class RoutePlanner(KnowledgeStore store)
{
    public RouteResult FindRoute(string destinationQuery);
}
```

Behavior, in order:
1. `GetCurrentRoom()` is `null` → `Found: false`, message tells the agent to look around first (routing from an unknown position is meaningless).
2. No room in `ListRooms()` matches `destinationQuery` (exact match first, then substring, both case-insensitive) → `Found: false`, message names the current room's `frontier` exits as exploration suggestions instead of leaving the agent to guess blindly.
3. Destination matches the current room itself → `Found: true`, zero-step "already there" message.
4. BFS over `walked`-state exits only (a `frontier` exit means "we know a name, not that we've been there" — not a valid traversal edge) from the current room to the destination. Found → step-by-step direction list plus a human-readable summary. Not found (destination known but not reachable from here via any walked path) → same frontier-exit-suggestion framing as case 2, but naming the known destination too.

**`ExitRecord` gains a `ToRoomId` field** (`Boukensha.Core.Knowledge.KnowledgeStore`): the existing record only exposes the destination's *name* (for display purposes, added in the observability viewer sub-project) — BFS needs the destination's *id* to continue traversal. Constructor-arity change, one call site (`KnowledgeStore.ListExits`) to update; no other code constructs `ExitRecord` positionally.

**Tool registration** (`BoukenshaHost.BuildAsync`): a native C# tool (no MCP round-trip — it's pure in-process graph traversal over data the agent's own hooks already wrote), registered via `Registry.Tool(...)` alongside the existing `KnowledgeHooks.Register` call, before `logger.ToolCatalog(context.Tools)` fires so it appears in the logged catalog like every other tool:
```csharp
registry.Tool("plan_route",
    "Find a route from your current location to a previously-visited room by name. " +
    "Returns step-by-step directions if a known walked path exists, or suggests unexplored exits if not.",
    new Dictionary<string, ToolParameter> { ["destination"] = new("string", "Name of the destination room") },
    args => Task.FromResult(routePlanner.FindRoute(args.GetValueOrDefault("destination") as string ?? "").Message));
```

## Testing

- `KnowledgeHooksTests` (new — the memory sub-project deliberately skipped a dedicated test file here since hooks were stateless then; de-duplication is real stateful logic worth a direct unit test now): no duplicate `[here]` message while stationary across multiple `BeforeAgentCall` raises; a fresh message after the current room changes.
- `RoutePlannerTests`: a reachable multi-hop path; an unreachable-but-known destination (frontier-exit suggestion); a fully unknown destination name (same suggestion, different framing); "already there"; "current location unknown."

## Out of scope for this pass

- Active move-gating (see Purpose)
- Any change to `MudTextParser`/room-survey behavior itself
- Route suggestions across `frontier` (unwalked) exits — `plan_route` only returns *known, walked* paths; suggesting unexplored directions is deliberately just a hint, not a claimed route
