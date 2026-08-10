# Frontier-Ranked Autonomous Exploration — Design

Status: approved, not yet implemented — see `docs/plans/week_2/plans/frontier_exploration_plan.md` (once written) for the task-by-task build log
Owner: Sam Alhambra
Third follow-on sub-project beyond week 2's original three (basic memory, observability, token optimization), after journey agent. Room agent (per-room entity/item/monster/observation tracking + room cards) remains deferred and follows this.

## Problem

`plan_route` (shipped in the journey-agent sub-project) only covers the "known-route" case: BFS over already-*walked* exits. When a destination hasn't been discovered yet — the recurring "go to the bakery" case documented at length in `docs/journal/week2.md` — it returns a "not found" message and a list of unexplored exits from the current room, and leaves it entirely up to the LLM's own judgment to decide what to do next. The journal's own benchmark found this costs ~65K tokens per attempt and often still fails to reach the destination; it also names the root causes directly: the agent inconsistently checks `exits` after moving (so it under-collects exit-name hints), and pure LLM-driven navigation choices are unreliable.

The journal separately records that "known-route, frontier-ranking, and broad-exploration behavior" was *designed* against this exact problem before the Ruby prototype pivoted to observability work, but never implemented as a tool (journal item 19). This spec implements that missing piece, natively, as a deterministic service — consistent with the session's existing precedent (journey agent's `RoutePlanner`/`MapLayout` are deterministic, not LLM-driven) and with the week's own stated hypothesis: *"We will need as humans build the problem in front of us... Will we simply just end up have an Agent that is simply wearing a trenchcoat of many traditional scripting and routing logic"* — yes, deliberately, for navigation.

## Non-goals (deferred)

- **Per-room item/monster/observation capture** ("room agent") — a separate, already-deferred spec. This spec only uses the room name/description/exit data the knowledge store already captures today.
- **`look_candidates`-style tag extraction** (the BERT-medium hidden-object classifier from the Ruby prototype) — not ported to .NET, not built here. See "Extension seam" below for how room-agent can plug into this spec later without a rearchitecture.
- **A standalone "explore the whole map, no target" tool** — this pass only exposes exploration as an automatic `plan_route` fallback. A no-target variant is a small, easy follow-up if it turns out to be wanted.

## Architecture

A new `ExplorationPlanner` class in `Boukensha.Core/Knowledge/`, alongside the existing `RoutePlanner`. Constructed with `KnowledgeStore`, `Registry`, and `AgentHooks` — all already built in `BoukenshaHost.BuildAsync`.

`plan_route`'s tool handler changes: when `RoutePlanner.FindRoute` can't resolve `destinationQuery` to a known room, it calls `ExplorationPlanner.ExploreTowards(destinationQuery)` instead of just returning the existing "not found" message — but **only when `fromQuery` was omitted** (i.e. `FindRoute` was resolving from the player's actual current location). Exploration physically moves the character via real `move` calls, so it can only ever start from wherever they actually are right now, never from an arbitrary already-visited room someone named via `from`. If `fromQuery` was explicitly provided and unresolved, `plan_route` keeps today's plain "no known room matching starting point" message unchanged — exploration doesn't apply to that case.

**The key technical seam:** `ExplorationPlanner` drives `move` and `check` itself via `registry.DispatchAsync(name, args)`, then calls `hooks.RaiseAfterToolCall(name, args, result, ok, CancellationToken.None)` after each one — mirroring exactly what `Agent.HandleToolCallsAsync` does for LLM-issued tool calls (`Agent.cs:151,161`). This is what makes every room and exit discovered during exploration get recorded by the *existing* `KnowledgeHooks.Register` logic automatically, with zero duplicated recording code.

Critically, `ExplorationPlanner` never calls `Context.AddMessage`. Internal exploration steps stay entirely out of the LLM's conversation — a 30-step walk still costs the conversation exactly one tool call and one summary response. This is the actual token-efficiency win, and it's why exploration must be driven by a deterministic loop rather than by giving the LLM better prompting to call `move` repeatedly itself.

(Tool handlers today have no `CancellationToken` flowing into them from `Registry.Tool`'s handler signature, so `CancellationToken.None` is used for the internal `RaiseAfterToolCall` calls — consistent with the rest of the tool-handler surface.)

## Algorithm: frontier-ranked greedy walk

Each step:

1. List every `(room, direction)` pair anywhere in the known map where `KnowledgeStore.ListExits` reports `state == "frontier"`.
2. Rank by BFS distance over `walked` exits from the current room to `room` — reusing the same path-finding `RoutePlanner.FindPath` already implements (extracted to a shared internal helper both classes call, rather than duplicated).
3. Navigate to the nearest candidate (zero or more `move` calls along the known walked path), then `move` through that frontier exit (one more call). This discovers a new room via the existing `KnowledgeHooks` `move` handling — no new recording logic.
4. `check kind=exits` at the newly-discovered room, so its own exits and destination-name hints are captured immediately. This is not optional and not left to LLM judgment — it directly fixes the journal's own named root cause ("the agent isn't checking exits... making its reasoning navigating an unknown world often random movements").
5. Compare the new room against the target via `RoomMatchesQuery(room, destinationQuery)` — see "Extension seam" below. Match → stop, return the route via `RoutePlanner.FindPath` from the room the player was actually standing in when `plan_route` was called to this one.
6. Repeat from step 1.

Backtracking out of a dead end isn't a special case requiring detection or a counter: step 2 always picks the globally nearest remaining frontier, which may sit in a completely different branch than the one just walked. This satisfies the original ask (keep exploring, don't get stuck, eventually cover everything reachable) without a tunable "how many dead ends is a few" threshold.

**Stopping conditions**, checked every step: target found; no frontier exits remain anywhere in the known map (exhausted); or the step budget for this call is reached.

### Extension seam: `RoomMatchesQuery`

Step 5's match check is its own named function, not inlined `.Name.Contains(...)` — matching the same exact-then-substring, case-insensitive rule `RoutePlanner` already uses for `destinationQuery`/`fromQuery`. `RoutePlanner`'s own destination resolution is refactored to call the same function, so there's one match rule, not two. When room-agent later adds tags (from `look_candidates`-style extraction or otherwise), extending the match to also consult tags is a one-function change here, not a new integration.

### Unresolved exits within a call

A frontier exit might not resolve to a parseable new room (blocked path, an interruption, a dark room misparsed as a dead exit — `MudTextParser.ParseRoomBlock` already returns `null` for several such cases indistinguishably). Rather than adding new persisted exit states to distinguish "permanently blocked" from "temporarily blocked" — not reliably derivable from MUD text alone, and out of scope for this pass — `ExplorationPlanner` keeps an **in-memory skip-set** of `(roomId, direction)` pairs that failed to resolve, scoped to the current call only. Such exits are excluded from ranking for the rest of this invocation (preventing an infinite retry loop against the same failing exit) but are eligible again on the next `plan_route` call, giving transient blockers a chance to clear.

## Step budget and resumption

A new `agent.exploration_max_steps` setting (`Config.Dig("agent", "exploration_max_steps")`, following the exact pattern `AgentMaxTurnTokens`/`AgentCompactionThreshold` already use), default **30** `move` calls per `plan_route` invocation — this bounds real wall-clock time and MUD command volume per call, since each step may be a real network round trip against a live server.

Resumption needs no dedicated session state: every room and exit discovered is already persisted in `KnowledgeStore` as it's found, so the next `plan_route` call simply re-reads current frontier state fresh and continues. Only the unresolved-exit skip-set (above) is deliberately *not* persisted, so it resets between calls.

## Response shapes

`plan_route`'s final message covers four cases:

1. **Found immediately** (destination already known) — unchanged from today.
2. **Found via exploration** — `"Route to '{name}': {directions} ({n} steps). Discovered {k} new room(s) while exploring."`
3. **Still exploring** (step budget hit, no match yet) — `"Still exploring for '{query}': {k} new room(s) found, {f} frontier(s) remaining. Call plan_route again to continue."`
4. **Exhausted** (no frontiers remain anywhere, no match) — `"Explored the full known map ({k} new room(s) found) — no room matching '{query}' exists."`

## Testing

`ExplorationPlannerTests` against a fake in-memory `Registry`/`AgentHooks` pair — a small scripted fixture graph of rooms wired to canned `move`/`check` responses (same fixture-from-real-capture discipline as every other reader/parser test this session, adapted here since there's no live MUD in unit tests):

- Finds a multi-hop target, returning the correct route.
- Exhausts a small closed map with no match, returns the exhausted-map message.
- Respects `agent.exploration_max_steps`, returns the still-exploring message with correct counts.
- An unresolved exit is skipped within one call rather than retried in a loop, and is retried successfully on a simulated second call.
- `RoomMatchesQuery` regression: `RoutePlanner`'s existing exact/substring matching behavior is unchanged after the refactor to share the function.

## Out of scope for this pass

- Per-room item/monster/observation capture (room agent, separate spec)
- `look_candidates` tag extraction/porting (room-agent input, not built here)
- A standalone no-target "explore everything" tool
- Persisted "blocked" exit state / distinguishing permanent vs. transient obstructions
