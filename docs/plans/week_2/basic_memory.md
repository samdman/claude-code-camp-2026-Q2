# Basic Memory + Lifecycle Hooks — Design

Status: approved (2026-08-10)
Owner: Sam Alhambra
Sub-project 1 of 3 for week 2 (observability layer and token-usage optimization follow as separate specs, each brainstormed after this one ships — see `docs/journal/week2.md`'s Technical Goal).

## Purpose

The week 2 journal (`docs/journal/week2.md`, Step 12) identified that the agent moves without reliably surveying new rooms, has no persistent sense of where it is or what it has already seen, and re-derives everything from raw MUD text on every turn. The journal's own conclusion: *"Before we can create the lifecycle hooks I think we actually need to store something in the db, at least our current location and a rooms table... We really do need the most condensed version of data to the agent."*

This spec builds that first: a SQLite knowledge store (rooms, exits, current location) plus three generic lifecycle hooks (`before_agent_call`, `before_tool_call`, `after_tool_call`) on the `.NET` `Agent` built in `docs/plans/week_2/dotnet_port.md`, wired together so the agent's context is populated from persistent memory instead of raw re-parsed MUD output every turn.

## Scope decision

Full parity with the journal's eventual schema (entities, sightings, encounters, player vitals/inventory/equipment/skills, CDC change journal) is **out of scope for this pass** — confirmed with the user. This pass covers only: rooms, exits, current location, frontier tracking. Player state and the richer entity/CDC schema are deferred to a later pass once this foundation is proven.

## Ground truth: what the MUD actually returns

Captured live against the running MUD server (via `mud_manager`'s `Session` directly, no LLM cost) rather than assumed from the journal's prose, since exact text shape drives the parser regexes.

`look` (lit room, ANSI stripped for readability):
```
The Sewer Pipe
   You are in what reminds you of a foul sewer, as if you liked being here!
You can see two exits leading either north or south.
[ Exits: n s ]
The small hairy Spider is here, busy with its web.
21H 100M 84V (news) (motd) >
```

`check` with `kind: exits`:
```
Obvious exits:
north - Too dark to tell.
south - The Grand Sewer
21H 100M 84V (news) (motd) >
```

`move` returns the same room-block shape as `look` on success. A dark room's `look`/`move` output is just `"It is pitch black..."` — no name or description available, so a dark room cannot be fingerprinted or distinguished from any other dark room.

Confirms the journal's own observation (Step 18): `look`'s inline `[ Exits: n s ]` gives only letter-abbreviated directions with no destination names; `check kind=exits` gives full compass words *and* destination names for previously-seen exits (`"Too dark to tell."` when unresolved). This is also the direct fix for the journal's Step 18 bug — `tbamud__move(direction: "d")` failing with `invalid direction: "d"` — by normalizing single-letter abbreviations to full compass words in one place before anything reaches the `move` tool.

The MCP tool surface actually exposed by `mud-manager` (confirmed from `week0_explore/mud_manager/lib/mud_manager/mcp_tools.rb` and a live session's logged `tools` list) is `look` (no args = current room), `examine`, `check` (kind: `score|inventory|equipment|gold|exits|time|weather|levels|wimpy|toggle|where`), `move` (direction: full compass word, required), `consider`, plus combat/item/social tools not relevant to this pass. There is no separate `exits` tool — it's `check` with `kind: "exits"`.

## Lifecycle hooks

New `AgentHooks` class (`Boukensha.Core`), constructor-injected into `Agent` as an optional parameter (defaults to a no-op empty instance so existing callers are unaffected):

```csharp
public sealed class AgentHooks
{
    public void OnBeforeAgentCall(Func<Context, CancellationToken, Task> handler);
    public void OnBeforeToolCall(Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task> handler);
    public void OnAfterToolCall(Func<string, IReadOnlyDictionary<string, object?>, string, bool, CancellationToken, Task> handler);
}
```

Plain `List<Func<...>>` internally, not C# `event` — multicast delegate invocation doesn't let each subscriber `await` cleanly, and hooks here are genuinely async (SQLite writes).

Firing points in `Agent.RunAsync`/`HandleToolCallsAsync`:
- `BeforeAgentCall(context)` — once per iteration, immediately before `Client.CallAsync`. **Not** fired during `WrapUpAsync`'s extra call (that call is a special wind-down path, not a normal turn step).
- `BeforeToolCall(name, args)` — immediately before each `Registry.DispatchAsync` call inside the per-`ToolUseBlock` loop.
- `AfterToolCall(name, args, result, ok)` — immediately after, using the exact same `result`/`ok` values already passed to `Logger.ToolResult` (no separate re-derivation).

This pass only *subscribes* passive recorders — no hook denies or rewrites a tool call. Active gating (e.g., blocking `move` into an unsurveyed direction) was explicitly deferred to the token-optimization sub-project, since the journal itself never resolved which gating approach it wanted.

## Knowledge store

New `Boukensha.Core.Knowledge` namespace. SQLite via `Microsoft.Data.Sqlite`, WAL mode, file at `<Config.Dir>/knowledge.db` (sibling to `sessions/`). Schema created with `CREATE TABLE IF NOT EXISTS` on first open — no migration framework needed at this size.

```sql
CREATE TABLE rooms (
    id INTEGER PRIMARY KEY,
    fingerprint TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    visit_count INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE exits (
    room_id INTEGER NOT NULL REFERENCES rooms(id),
    direction TEXT NOT NULL,            -- always a full compass word
    to_room_id INTEGER REFERENCES rooms(id),
    to_room_name_hint TEXT,             -- destination name from `check exits`, before walked
    state TEXT NOT NULL,                -- 'frontier' | 'walked'
    updated_at TEXT NOT NULL,
    PRIMARY KEY (room_id, direction)
);

CREATE TABLE location (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    current_room_id INTEGER REFERENCES rooms(id),
    updated_at TEXT NOT NULL
);
```

`fingerprint` = lowercased, whitespace-normalized `"{name}\n{description}"` run through SHA-256, hex-encoded — a room identity substitute since CircleMUD never exposes a room vnum to players. `location` is a single-row table (`id=1`, upserted); `current_room_id` stays `NULL` while the player is in a dark room rather than guessing, since dark rooms are indistinguishable from each other by text alone.

`KnowledgeStore` public API:
```csharp
public sealed class KnowledgeStore : IDisposable
{
    public KnowledgeStore(string path);
    public RoomRecord UpsertRoom(string name, string description);                          // increments visit_count if fingerprint already known
    public void RecordExits(int roomId, IReadOnlyDictionary<string, string?> directionToDestinationHint);
    public void LinkExit(int fromRoomId, string direction, int toRoomId);
    public RoomRecord? GetCurrentRoom();
    public void SetCurrentRoom(int roomId);
    public string BuildHereBlock();                                                          // the compact [here] text, "" if no current room known
}

public sealed record RoomRecord(int Id, string Fingerprint, string Name, string Description, int VisitCount);
```

## Parsing

New static `MudTextParser` (`Boukensha.Core.Knowledge`), tested directly against the ground-truth captures above (real fixture strings, not synthetic ones):

```csharp
public static class MudTextParser
{
    public static (string Name, string Description, IReadOnlyList<string> ExitLetters)? ParseRoomBlock(string raw);
    public static IReadOnlyDictionary<string, string?> ParseExitsBlock(string raw); // full compass word -> destination name, null if unresolved
    public static string StripAnsi(string raw);
    public static string NormalizeDirection(string directionOrLetter); // "n"/"north" -> "north", etc.
}
```

`ParseRoomBlock` returns `null` for dark-room output (`"It is pitch black"` prefix) or anything else that doesn't match the room-block shape — callers treat `null` as "don't update memory," not as an error.

## Hook wiring (`BoukenshaHost`)

A new `KnowledgeHooks.Register(AgentHooks hooks, KnowledgeStore store)` helper subscribes:

- `AfterToolCall("look", args, result, ok)` where `ok` and `args` has no `target` → `MudTextParser.ParseRoomBlock(result)`; if non-null, `UpsertRoom` + `SetCurrentRoom`.
- `AfterToolCall("move", args, result, ok)` where `ok` → same room-block parse; if non-null: read `previousRoomId = store.GetCurrentRoom()?.Id` *before* upserting the new room, upsert the new room, and if `previousRoomId` is not null, `LinkExit(previousRoomId, NormalizeDirection(args["direction"]), newRoomId)` (state becomes `walked`), then `SetCurrentRoom(newRoomId)`. A failed/blocked move (MUD-level rejection, not an exception) naturally no-ops here since `ParseRoomBlock` won't match rejection text.
- `AfterToolCall("check", args, result, ok)` where `ok` and `args["kind"] == "exits"` → `MudTextParser.ParseExitsBlock(result)`, then `RecordExits(currentRoomId, ...)` with the full parsed set. `RecordExits` itself is a non-destructive upsert: an exit already in `walked` state keeps its `to_room_id` and state untouched even if the hint text is passed again — only a still-`frontier` row's `to_room_name_hint` gets updated. This is `RecordExits`'s own invariant, not something callers filter for.
- `BeforeAgentCall(context)` → if `store.GetCurrentRoom()` is non-null, `context.AddMessage("user", store.BuildHereBlock())`.

Fires unconditionally every iteration (no de-duplication when the room hasn't changed) — simplest correct behavior for this pass. Skipping redundant injection is a token-optimization concern for the next sub-project, not this one.

`BuildHereBlock()` format (matches the journal's own template, Step 18):
```
[here] The Sewer Pipe (visit 1)
exits: n→? | s→The Grand Sewer ✓
```
`✓` marks a `walked` exit, `?` marks `frontier`/unresolved.

## Testing

xUnit tests added to `Boukensha.Core.Tests/Knowledge/`:
- `MudTextParserTests` — the two captured fixtures above (lit room, exits block), plus the dark-room `null` case and the letter/full-word direction normalization table.
- `KnowledgeStoreTests` — fingerprint dedup (same name+description upserted twice increments `visit_count`, doesn't create a second row), exit linking after a simulated move sequence, `BuildHereBlock` output shape for a known vs. unknown current room.
- `AgentHooksTests` — hooks fire in the right order and receive the right arguments, using a fake `Agent` wiring (mirrors how `AnthropicBackendTests` avoids a live API call).

## Out of scope for this pass

- Player vitals/inventory/equipment/skills tables
- Entities/sightings/encounters tables
- CDC append-only change journal
- Active tool-call gating (denying/redirecting a move)
- De-duplicating `[here]` injection when state hasn't changed
- A viewer for the knowledge store (that's the observability sub-project)
