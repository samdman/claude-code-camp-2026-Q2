# Journey Agent + Map Visualization — Design

Status: implemented and verified (2026-08-10) — see `docs/plans/week_2/plans/journey_agent_plan.md` for the task-by-task build log
Owner: Sam Alhambra
First of two follow-on sub-projects beyond week 2's original three (basic memory, observability, token optimization) — room agent (per-room entity/monster/event tracking + room cards) follows as a separate spec once this ships.

## Purpose

Two capabilities, confirmed with the user as **deterministic services, not new LLM-driven subagents** (no new task-delegation infrastructure — nothing like that exists yet in the `.NET` port, and this pass doesn't build it):

1. **Generalized path planning** — `RoutePlanner.FindRoute` currently only plans from the *current* room. "Point A to point B" routing needs an explicit start point too.
2. **Journey trail tracking** — nothing currently reconstructs "the order rooms were actually visited" as a first-class view, even though the data already exists.
3. **Map visualization** in `Boukensha.Observability` — the room graph, laid out spatially, with the journey trail overlaid. This is the exact gap `docs/plans/week_2/observability_viewer.md` flagged and deliberately deferred ("Map/graph visualization of the room graph... a natural follow-up, not this pass").

## Generalized route planning

`RoutePlanner.FindRoute`'s signature changes from `FindRoute(string destinationQuery)` to `FindRoute(string destinationQuery, string? fromQuery = null)`:
- `fromQuery: null` (the default) → identical behavior to today: BFS from `KnowledgeStore.GetCurrentRoom()`.
- `fromQuery` provided → resolved via the same exact-then-substring case-insensitive matching already used for `destinationQuery`; if it doesn't resolve to a known room, returns `Found: false` with a message naming the unresolved start room (a new failure case; today's only "not found" reasons are unknown/unreachable destination or unknown current location).

Backward-compatible: the existing `plan_route` tool registration in `BoukenshaHost` calls `FindRoute(destination)` positionally today, which continues to mean "from current room" unchanged.

## Journey trail tracking

No new write-side tracking in `Boukensha.Core` — the CDC change journal (`knowledge_changes.jsonl`, shipped in the instrumentation sub-project) already records every `location_changed` entry as `{at, session_id, before: {room_id}, after: {room_id}}`. A new `JourneyReader` in `Boukensha.Observability` **wraps the existing `ChangeLogReader`** (calls `ChangeLogReader.ReadEntries(changeLogPath)`, does not re-parse the JSONL itself), filters to `kind == "location_changed"`, and resolves each `before`/`after` room id to a name via one `KnowledgeStore.ListRooms()` call (loaded once per request, not once per entry — an `IReadOnlyDictionary<int, string>` id→name lookup built from it), producing an ordered list of `JourneyStep(DateTimeOffset At, string? SessionId, string? FromRoomName, string ToRoomName)`.

## `/Knowledge/Map` page

**Layout algorithm** (deterministic, not a physics/force-directed simulation — matches the journal's own prior approach to this exact problem): pick a root room (the one with the earliest `first_seen_at`, i.e. the session's actual starting room), BFS outward over `walked` exits only, assigning each newly-reached room a grid position offset from its parent by direction (`north`: y−1, `south`: y+1, `east`: x+1, `west`: x−1). `up`/`down` exits don't get a 2D position — they're shown as a small `↑`/`↓` badge on the room node instead of attempting a 3rd dimension. A room reached via multiple paths keeps whichever position was assigned first (BFS order) — simple and deterministic, not a "best" layout. Disconnected components (rooms with no walked path from the root at all) get placed in a separate row below the main graph rather than overlapping it.

Implemented as a **pure function taking already-loaded data, not a `KnowledgeStore` reference** — so `MapLayoutTests` can exercise it without any SQLite connection at all:
```csharp
public sealed record RoomPosition(int RoomId, int X, int Y);

public static class MapLayout
{
    public static IReadOnlyList<RoomPosition> Calculate(
        IReadOnlyList<RoomRecord> rooms,
        IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> exitsByRoomId);
}
```
The `Knowledge/Map` page's `PageModel` calls `store.ListRooms()`/`store.ListExits(id)` to build the two arguments, then calls `MapLayout.Calculate` — the same "page does the store calls, pure class does the algorithm" split already used for reader classes elsewhere in this project.

**Rendering**: static SVG (no pan/zoom in this pass — flagged as a later polish item, not required to see a clear picture per the request). Each room is a rectangle labeled with its name and visit count; the current room (`KnowledgeStore.GetCurrentRoom()`) is highlighted with a distinct border/fill. Walked exits are solid lines between the two room rectangles. Frontier exits are short dangling stubs off the originating room (no target to draw a line to) labeled with the direction and, if known, the destination-name hint.

**Journey Trail panel**: below the map, a table driven by `JourneyReader` — timestamp, from-room → to-room, session id — the same visual language as `/Knowledge/Changes` (which this reuses `ChangeLogReader`'s underlying file for) but presented as a walkable narrative rather than a raw change-kind log.

## Testing

- `RoutePlannerTests` gains cases for the new `fromQuery` parameter: valid explicit start, unresolvable explicit start, and confirmation that omitting it still matches today's from-current-room behavior (regression guard on the signature change).
- New `JourneyReaderTests`: parses `location_changed` entries into ordered, name-resolved steps from real fixture lines (same discipline as every other reader this session — captured from an actual run, not synthetic).
- New `MapLayoutTests` (pure algorithm, no SQLite/HTTP): given a small room/exit graph, asserts specific rooms land at specific grid coordinates, confirms cycle handling (first-assigned position wins) and disconnected-component placement, without going through a live page render — matches the pattern the Ruby/JS precedent used ("layout tests" mentioned in the journal's own rollup).
- No test for the SVG rendering itself (matches every other page in this viewer — test the logic, not the HTML).

## Out of scope for this pass

- Pan/zoom interactivity on the map
- Room agent (entities/monsters/events, room cards) — separate follow-on spec
- Animating the trail (e.g., a playback control) — the trail panel is a static ordered table
- 3D/vertical layout for up/down exits — badge-only, as above
