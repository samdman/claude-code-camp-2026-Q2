# Observability: Viewer — Design

Status: implemented and verified (2026-08-10) — see `docs/plans/week_2/observability_viewer_plan.md` for the task-by-task build log
Owner: Sam Alhambra
Sub-project 2 of 3 for week 2, phase 2 of 2 (builds on `docs/plans/week_2/observability_instrumentation.md`, which shipped the data this reads).

## Purpose

Spec 1 made the data exist (durations, full prompt fidelity, task attribution, raw telnet traffic, a CDC change journal). Nothing yet lets a human actually look at it. This spec is the `Boukensha.Observability` Razor Pages app that reads all of it — matching the journal's "Key Takeaway": a bespoke walkthrough view, not generic tracing infrastructure.

## Project

New `week2_capable/dotnet/src/Boukensha.Observability` — ASP.NET Core Razor Pages (`Microsoft.NET.Sdk.Web`), server-rendered, no client-side framework or build step. References `Boukensha.Core` for `Config` (resolves `BOUKENSHA_DIR` identically to the agent) and `Boukensha.Core.Knowledge.KnowledgeStore`. Runs as its own process, independent of the agent — both point at the same `.boukensha` directory. No new NuGet dependencies (Razor Pages ships in the ASP.NET Core shared framework).

**Read-only by convention**: the viewer constructs a real `KnowledgeStore` (needed for its read methods) but must never call its mutating methods (`UpsertRoom`, `RecordExits`, `LinkExit`, `SetCurrentRoom`, `ClearCurrentRoom`). No separate read-only type is introduced this pass — enforced by code review discipline, not the type system. Two SQLite connections (agent + viewer) to the same `knowledge.db` is safe under WAL mode, which the store already enables.

## Data access

New in `Boukensha.Observability` (not `Boukensha.Core` — only the viewer ever needs to parse these formats back out; the writers stay write-only):

- **`SessionLogReader`**: `SessionSummary(string SessionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Task, string? Provider, string? Model, int TurnCount, long TotalInputTokens, long TotalOutputTokens, double TotalCostUsd, string FilePath)`; `ListSessions(string sessionsDir) -> IReadOnlyList<SessionSummary>` (newest first — reads each file's first line for identity fields, scans for `turn`/`response` phase lines for counts/token sums); `SessionEvent(string Phase, DateTimeOffset At, JsonObject Raw)`; `ReadEvents(string filePath) -> IReadOnlyList<SessionEvent>` (full parse, used only by the detail page, not the list page).
- **`TelnetLogReader`**: `TelnetEntry(DateTimeOffset At, string Direction, string Text)`; `ReadEntries(string filePath) -> IReadOnlyList<TelnetEntry>`. Time-range filtering (for correlating to a specific session) is a plain LINQ `.Where(...)` in the page itself — `telnet.jsonl` has no session id to key on, since it's a continuous MUD-manager-level log spanning every session, not scoped to one.
- **`ChangeLogReader`**: `ChangeEntry(DateTimeOffset At, string? SessionId, string Kind, JsonNode? Before, JsonNode? After)`; `ReadEntries(string filePath) -> IReadOnlyList<ChangeEntry>`.
- **`KnowledgeStore`** (`Boukensha.Core.Knowledge`, modified) gains two read methods: `ListRooms() -> IReadOnlyList<RoomRecord>` (all rooms, most-recently-seen first) and `ListExits(int roomId) -> IReadOnlyList<ExitRecord>` (new record: `ExitRecord(string Direction, string State, string? ToRoomName, string? Hint)`, left-joining `rooms` for the walked destination's name).

## Pages

- **`/`** — Session list: id, started-at, duration (`EndedAt - StartedAt`), task/provider/model, turn count, total tokens, total cost. Newest first.
- **`/Sessions/{id}`** — The core view: every event in chronological order, phase-specific rendering (`iteration`, `prompt` with expandable full message/tool content, `plan`, `tool_call`/`tool_result` as a paired block showing `duration_ms` and `task`, `reasoning`, `response` showing `duration_ms`/token breakdown/cost, `compaction`, `turn_end`, `limit_reached`), plus the one `tool_catalog` event and `session_start`'s `system` field surfaced prominently near the top rather than buried mid-stream. A **duration column sorted descending** (a simple re-sort of the same event list, not a separate data source) is the direct "where's the bottleneck" view.
- **`/Sessions/{id}/Telnet`** — `telnet.jsonl` entries whose timestamp falls within `[session.StartedAt, session.EndedAt]`, interleaved with markers at the session's own `tool_call`/`tool_result` timestamps, so a human can visually spot MUD traffic that arrived outside any tool call's request/response window.
- **`/Knowledge`** — All rooms (`ListRooms()`), each with its exits (`ListExits(room.Id)`) shown inline using the same `dir→destination ✓` / `dir→?` notation `KnowledgeStore.BuildHereBlock()` already established. Current room (via `GetCurrentRoom()`) highlighted.
- **`/Knowledge/Changes`** — `knowledge_changes.jsonl`, chronological, with a `kind` filter dropdown (client-side, no server round-trip needed for a filter this simple — plain `<select>` + a few lines of vanilla JS toggling row visibility).
- **`/Live`** — The cockpit: latest session's most recent N events + current knowledge state (current room + its exits), server-rendered on first load, then refreshed via a small inline `<script>` polling a new `/api/live` minimal-API JSON endpoint (`Program.cs`, not a Razor Page) every ~3 seconds and patching the relevant DOM nodes. No SignalR, no client framework — matches the earlier "lightweight polling via fetch" decision.

## Wiring (`Program.cs`)

Composition root constructs `Config`, derives `sessionsDir`/`knowledgeDbPath`/`changeLogPath`/`telnetLogPath` from `Config.Dir` (same `.boukensha` convention throughout — `telnetLogPath` defaults to `<Config.Dir>/telnet.jsonl`, matching what Spec 1 wired into `.boukensha/settings.yaml`'s `MUD_TELNET_LOG`). `KnowledgeStore` is registered **Scoped** (a fresh instance — and fresh `SqliteConnection` — per request), not Singleton: ASP.NET Core serves concurrent requests on multiple threads, and `SqliteConnection` isn't safe for concurrent use from more than one thread. WAL mode makes opening a new connection per request cheap and safe; the three reader classes (stateless, no connection to manage) and the resolved paths are registered Singleton, all injected into Razor `PageModel`s via constructor injection.

## Testing

xUnit tests for `SessionLogReader`, `TelnetLogReader`, `ChangeLogReader`'s parsing logic, using real fixture lines captured from this session's own live-verification runs (not synthetic — same discipline as `MudTextParser`), plus `KnowledgeStore.ListRooms`/`ListExits`. No tests for Razor Page rendering itself (matches the established precedent: test the logic, not the HTML).

## Out of scope for this pass

- Map/graph visualization of the room graph
- Authentication (local dev tool only)
- Editing/annotating from the viewer (strictly read-only)
- Cross-referencing `telnet.jsonl` lines to a *specific* `tool_call`/`tool_result` pair programmatically (the Telnet page shows time-window overlap visually; exact pairing is a human judgment call, not computed)
