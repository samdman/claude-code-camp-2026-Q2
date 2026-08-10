# Frontier Exploration: Confidence-Ranked Leads with Retreat — Design

Status: draft
Owner: Sam Alhambra
Amends `docs/plans/week_2/specs/frontier_exploration.md` (the frontier-ranked greedy walk, shipped in `d6780bf`). Read that spec first — this one only describes what changes.

## Problem

Today's `ExplorationPlanner` ranks frontier candidates purely by BFS distance: always cross the *nearest* unexplored exit, regardless of whether anything about it suggests it leads toward the destination. It also never gives up — it either finds the target, hits the step budget ("still exploring, call again"), or exhausts the entire known map. There's no notion of "this lead looks promising" vs. "this is a guess," and no way to stop chasing unpromising exits and come back to where the player actually was.

`check kind=exits` already captures a per-exit destination-name hint (`ExitRecord.Hint`, from `to_room_name_hint` — e.g. an unwalked exit hinting "Bakery") before that exit is ever walked. This spec uses that existing data to rank candidates by how likely they are to lead to `destinationQuery`, log that reasoning, only cross exits that look "good enough," and retreat to the room exploration started from once nothing does.

## Behavior change

This trades the current completeness guarantee ("repeated calls eventually cover the whole reachable map") for "chase good leads, then stop and report back at home." A destination reachable only through exits with no hint, or a misleading hint, may now go unfound where the old exhaustive walk would eventually have stumbled onto it. This is the intended trade — chasing every unhinted exit is exactly the token/time cost this feature exists to avoid.

## Confidence scoring

New `RoomGraph.ExitConfidence(ExitRecord exit, string query) -> double`, alongside the existing `RoomMatchesQuery`:

| Condition | Confidence |
|---|---|
| `exit.Hint` equals `query`, case-insensitive | 1.0 |
| `exit.Hint` and `query` substring-match either direction, case-insensitive | 0.6 |
| `exit.Hint` is `null` (unproven, not walked/checked yet) | 0.2 |
| `exit.Hint` is set but matches neither rule above | 0.0 |

## Ranking change

`ExplorationPlanner.NextFrontierCandidate` currently picks the globally nearest frontier exit (BFS distance from the current room). It changes to: pick the candidate with the **highest confidence**; break ties by nearest BFS distance (today's rule, preserved as the tiebreaker). Distance no longer drives candidate choice on its own.

## "Good enough" threshold

New `agent.exploration_confidence_threshold` setting (`Config.Dig("agent", "exploration_confidence_threshold")`, following the exact pattern `AgentExplorationMaxSteps` uses), default **0.5** — so exact/substring hint matches (1.0/0.6) clear it, but an unproven exit (0.2) or a contradicting hint (0.0) does not.

Each step, before acting: compute the best remaining candidate and its confidence, log it (see Logging below). If confidence `< threshold`, exploration stops for this call — this is the new "stuck" condition, checked *before* any of the existing stopping conditions (target found / no frontiers / step budget).

## Retreat on stop

Whenever exploration stops **without finding the target** — stuck (low confidence), map exhausted (no frontiers anywhere), or an unresolved exit (existing dark-room/ambiguous-move case) — it now attempts to return to `startRoom` (the room the player was actually in when this `plan_route` call began) via a `recall` command, dispatched the same way `move`/`check` are today (`Registry.DispatchAsync("send_raw", {command: "recall"})`, followed by `hooks.RaiseAfterToolCall`, so the resulting room still gets recorded).

**Not applied to the step-budget-hit case** ("still exploring, call `plan_route` again to continue") — that case is meant to resume from wherever exploration currently is on the next call, so recalling home would just force walking back out again.

`KnowledgeHooks` gets one new case, mirroring how `flee` is handled today (`KnowledgeHooks.cs:21-24`): a `send_raw` call whose `command` argument equals `recall` (case-insensitive) updates the current room the same way a transition does — parse the result as a room block, no `LinkExit` call (recall isn't a walked exit), `SetCurrentRoom` on success.

**Fallback if recall fails to resolve** (not available to this character, on cooldown, unexpected response): fall back to walking back via `RoomGraph.FindPath` from the current room to `startRoom` over already-walked exits — always resolvable, since exploration only ever walks through rooms it already discovered. If *that* isn't possible either (position is genuinely unknown, e.g. the unresolved-exit dark-room case with a failed recall), leave state as today: unknown position, surfaced via `RoutePlanner`'s existing "current location unknown -- look around first" guard on the next call.

## Logging

New `Logger` methods, following the existing `Turn`/`Iteration`/`LimitReached` phase-object convention (`Logger.cs:30-38`), written to the session's existing `.jsonl`:

- `ExplorationStep(int step, int roomId, string direction, string? hint, double confidence, bool explored)` — logged every iteration of the walk, whether or not the candidate was actually crossed.
- `ExplorationRetreat(string reason, int stepsUsed, int discovered, int frontiersRemaining, bool recalled)` — logged once when exploration stops without finding the target and attempts (or fails) the return trip; `reason` is one of `"stuck"`, `"exhausted"`, `"unresolved_position"`.

`ExplorationPlanner`'s constructor gains a `Logger` parameter: `ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks, Logger logger)`. `BoukenshaHost.BuildAsync` passes the session's existing `logger` (already constructed at that point, `BoukenshaHost.cs:74`).

## Response shapes

Extends the four cases in the base spec with a fifth:

5. **Stuck, retreated** — `"No promising leads for '{query}' (best candidate confidence {c:0.0}, threshold {t:0.0}). Recalled back to '{originName}'. {k} new room(s) found, {f} frontier(s) remain unexplored. Call plan_route again to keep exploring, or try a different name for the destination."`

The **exhausted** case (base spec, case 4) gains the same retreat behavior but keeps its existing message text (it already implies no further progress is possible; retreat is a side effect, not a new thing to explain).

## Config

`Config.cs`, immediately after `AgentExplorationMaxSteps`:

```csharp
public double AgentExplorationConfidenceThreshold => Convert.ToDouble(Dig("agent", "exploration_confidence_threshold") ?? 0.5);
```

## Testing

Extends `ExplorationPlannerTests` (existing fake-`Registry`/`AgentHooks` fixture, now also registering a fake `send_raw` handler):

- A candidate with an exact-matching hint is chosen over a nearer candidate with no hint.
- All remaining candidates below the confidence threshold → stops without exploring further, `send_raw {command: recall}` is dispatched, message reports the retreat.
- Recall succeeds (fake handler returns a parseable room block for the origin) → `KnowledgeStore.GetCurrentRoom()` reflects the origin room afterward.
- Recall fails (fake handler returns unparseable text) → falls back to walking the known path back to the origin.
- Map exhausted (zero frontiers anywhere) → still retreats (extends the existing exhausted-map test).
- Step-budget-hit ("still exploring") case does **not** trigger retreat — position stays wherever the walk stopped.
- `RoomGraph.ExitConfidence` unit tests for all four scoring rules.
- `KnowledgeHooks` regression: a `send_raw`/`recall` result updates current room like `flee` does; a `send_raw` call for any other command is ignored (unchanged behavior).

## Out of scope for this pass

- A way to force a "keep exploring blindly, ignore confidence" mode — if this turns out to be wanted after real use (mirroring how the base spec's exhaustive walk was itself motivated by real journal findings), it's a small follow-up: an optional `plan_route` argument or a second config default.
- Weighting confidence by anything other than the hint string (e.g. room description text, tags from a future room-agent) — the extension seam for that is the same `RoomMatchesQuery`/tag seam the base spec already named.
- Persisted "blocked"/"recall unavailable" state across calls — recall availability is re-attempted fresh every time exploration stops, same as the base spec's exit skip-set resetting between calls.
