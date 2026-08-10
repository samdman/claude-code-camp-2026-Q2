# Week 3 Technical Documentation

## Technical Goal

Take everything from week 2 (memory, observability, token savings) and port it out of the Ruby prototype into a real .NET project, then use that as a solid base to finally fix the thing week 2 never got to: getting the agent to reliably find its way somewhere it's never been.

## Technical Uncertainty

Wasn't sure how much would get lost or behave differently porting from Ruby to .NET, or if it'd just be busywork. Also wasn't sure if "rank unexplored exits by how likely they lead to the target" was actually going to be smarter than just walking the nearest one, or if it'd just add complexity for no real gain.

## Technical Observations

**dotnet port** — pretty mechanical rewrite of the Ruby agent loop, MCP client, and console app. Verified it end-to-end live against the MUD before building anything new on top of it, so we knew we weren't porting forward.

**Memory** — rebuilt the SQLite knowledge store and the before/after tool-call hooks in .NET. Found and fixed a bug where the "current room" could go stale after fleeing or walking into a dark room.

**Observability** — rebuilt a mud-monitor-style viewer for the .NET side: sessions, a knowledge/map view, a change log, live tailing. Added timing and task-attribution info so we can actually see what a session did and how long it took.

**Token optimization** — deduped the repeated room-summary message so we're not paying for it every turn, added a `plan_route` tool that returns an already-known walked route instantly instead of the agent feeling its way there turn by turn, then generalized that into a "journey agent" with a trail and map view.

**Exploration debugging** — this ate most of the week.

- Built an exploration planner: when `plan_route` doesn't know the destination yet, it now drives its own move/look loop internally — no LLM turns spent per step — picking the nearest unexplored exit until it finds the target, runs out of map, or hits a step budget.
- Roadblock: live testing against the real MUD immediately turned up two bugs no unit test caught. One, the code guessed "I must have stayed put" when a move failed, but a failed move looks identical to walking into a dark room — so it was quietly guessing wrong and getting stuck in a loop. Fixed by not guessing at all: if we can't tell what happened, just admit the position is unknown and let the normal recovery flow handle it. Two, exits behind a closed door were silently getting dropped by the text parser (a pre-existing gap), so newly discovered rooms sometimes vanished. Fixed the parsing.
- Took it further: instead of just walking the nearest unexplored exit, rank exits by how well their hinted name matches what we're actually looking for, log that reasoning, only chase a lead if it's confident enough, and retreat back to where we started (via a recall command, or retracing our own steps if that fails) once nothing looks promising anymore.
- Roadblock: caught two more bugs before they ever ran, just by hand-tracing test scenarios while writing the plan instead of writing the plan on faith. The confidence scoring would've ranked "the hint doesn't match yet" worse than "no hint at all" — which breaks basically every multi-hop route, since the very first hop's hint is never going to name the final destination. And the "walk back home" fallback was going to rely on already knowing the return path, which you generally don't have on a corridor you've only ever walked one direction — so it would have failed exactly when it was needed most. Fixed both before writing a line of implementation code.

## Technical Conclusions

The .NET port didn't cost us anything and gave a much better foundation to build on. The real work is in "which way looks most promising" logic, and the biggest wins this week came from catching bad assumptions on paper before they shipped, not just from live bugs after the fact. Deterministic, scripted logic keeps beating "let the LLM figure it out" for anything that has a clearly correct answer.

## Key Takeaway

Hand-tracing your own test scenarios before you run them catches the same kind of bugs live verification does — just before you ship instead of after. Do both; they catch different things.

## Next Steps / Planned Improvements

- Live-verify the new confidence-ranked exploration + recall retreat against the real MUD — we don't yet know if `recall` is even available to our test character, though the fallback should cover us if not.
- Room agent (capturing items/monsters/observations per room) keeps getting deferred — probably the next real feature.
- Tune the confidence threshold and hint-matching once we have real usage data instead of guessed defaults.
- Now that exploration is smarter, revisit token usage numbers again — should be a good chunk lower than the original 65K-token bakery run.
- Maybe surface exploration confidence/retreat events in the observability viewer so we can see the reasoning, not just the outcome.
