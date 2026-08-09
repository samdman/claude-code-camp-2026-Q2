# Observability: Instrumentation — Design

Status: implemented and live-verified (2026-08-10) — see `docs/plans/week_2/observability_instrumentation_plan.md` for the task-by-task build log
Owner: Sam Alhambra
Sub-project 2 of 3 for week 2, phase 1 of 2 (instrumentation now; the `Boukensha.Observability` viewer app that reads all of this follows as a separate spec once this ships — see `docs/journal/week2.md`'s Technical Goal).

## Purpose

The user's requirements for the observability layer:
1. How long does each unit of work take? Where's the bottleneck?
2. What actual messages, tools, and system prompt were passed to the LLM API call?
3. What token total and granular breakdown?
4. What exact commands are being called by our MUD manager, and by which task (subagent)?
5. What does the actual command call look like, what was actually returned, and what could have been dropped/missed due to mistimed polling?
6. What's in our database?
7. What incremental changes are being made in our database?
8. How do we see changes over time?
9. How can we see what's going on in realtime (cockpit view)?

Items 6/9 are a viewer concern (Spec 2, once this ships). Items 3 is mostly already satisfiable by the *existing* `response` event's raw `usage` object (Anthropic returns `input_tokens`/`output_tokens`/`cache_creation_input_tokens`/`cache_read_input_tokens` — already logged verbatim via `JsonUtil.ToObject`) — no instrumentation change needed there, just a viewer that surfaces it. This spec covers what actually requires new data capture: 1, 2, 4, 5, 7, 8's write side.

Item 5 in particular ("what could have been dropped due to mistimed polling") cannot be answered from anything the `.NET` agent observes — `mud_manager` (Ruby) has **no raw telnet I/O logging today** (confirmed by inspecting `lib/mud_manager/session.rb`), so this spec includes a small, focused Ruby change alongside the `.NET` ones.

## Ground truth: exact hook points

From reading `week0_explore/mud_manager/lib/mud_manager/session.rb` directly (not assumed):
- `Session#send_command` (line 76–88) is the sole place a raw command is written to the socket (`@socket.write(line + "\r\n")`).
- `Session#start_reader`'s background-thread loop (line 198–225) is the lowest possible level data arrives at: `chunk = @socket.readpartial(4096)` → `strip_iac` → appended to `@buffer`. Logging *here*, independent of whatever `read_until_quiet`/`read_until_prompt` later decide to do with the buffer, is what makes a "was this dropped due to mistimed polling" diagnosis possible — the raw arrival is recorded regardless of consumption timing.
- `Session.new` is constructed once in `bin/mud-manager` (line 46), which already reads MUD connection details from environment variables (`MUD_HOST`/`MUD_PORT`/`MUD_USERNAME`/`MUD_PASSWORD`) set via `settings.yaml`'s `mcp_servers.mud.env` — the natural place to add one more.

## Ruby: `mud_manager` raw telnet logging

New file `week0_explore/mud_manager/lib/mud_manager/telnet_log.rb`:
```ruby
require "json"

module MudManager
  class TelnetLog
    def initialize(path)
      @file = File.open(path, "a")
      @file.sync = true
      @mutex = Mutex.new
    end

    def record(direction:, text:)
      @mutex.synchronize do
        @file.puts(JSON.generate(at: Time.now.iso8601(3), direction: direction, text: text))
      end
    end

    def close
      @file.close
    end
  end
end
```

`Session`:
- `initialize(..., telnet_log_path: nil)` — builds `@telnet_log = telnet_log_path ? TelnetLog.new(telnet_log_path) : nil`.
- `send_command`: after the `@socket.write` line, `@telnet_log&.record(direction: "send", text: line)`.
- `start_reader`'s loop: right after `text = strip_iac(chunk)` and the `unless text.empty?` guard, before the buffer-append, `@telnet_log&.record(direction: "recv", text: text)`.
- `close`: also closes `@telnet_log` if present.

`bin/mud-manager`: reads `ENV["MUD_TELNET_LOG"]`, passes as `telnet_log_path:` to `Session.new`. Undocumented/unset by default (opt-in) — documented in the file header comment alongside the existing `MUD_HOST`/etc. list.

`settings.yaml` (this repo's `.boukensha/settings.yaml`, not code): add `MUD_TELNET_LOG: "<repo>/.boukensha/telnet.jsonl"` to `mcp_servers.mud.env` to turn it on. `BoukenshaHost`'s MCP-spawn code needs **no change** — it already passes through whatever `env:` dict `settings.yaml` configures verbatim.

## `.NET`: richer `Logger`/`Agent`/`BoukenshaHost`

**`Logger.cs`:**
- `Response(...)` gains an `int durationMs` parameter, included in the logged dict as `["duration_ms"]`.
- `ToolCall(...)` gains a `string task` parameter (`["task"]`).
- `ToolResult(...)` gains `string task` and `int durationMs` parameters.
- New `ToolCatalog(IReadOnlyDictionary<string, ToolDefinition> tools)` method, logging `{"phase": "tool_catalog", "tools": [{name, description, parameters}, ...]}` — **not** folded into `session_start`'s snapshot, because `Logger` is constructed (and writes `session_start`) *before* `BoukenshaHost`'s MCP tool-registration loop runs (confirmed by reading the actual current construction order in `BoukenshaHost.BuildAsync`) — the full tool set doesn't exist yet at that point. Fired as its own event once registration (MCP loop + the `options.Configure?.Invoke(...)` callback for any `RunDsl`-registered tools) completes.
- The private `GenerateSessionId()` becomes a `public static string GenerateSessionId()` — `BoukenshaHost` needs to generate one session id upfront and hand it to *both* `Logger` and `KnowledgeStore`, rather than leaving `Logger` to generate its own internally-hidden one.

**`Agent.cs`:**
- Wraps the `Client.CallAsync` call in a `Stopwatch`, passes `stopwatch.ElapsedMilliseconds` to `LogResponse`'s `Logger.Response` call and to the wrap-up path's response logging too.
- Wraps each `Registry.DispatchAsync` call (inside `HandleToolCallsAsync`'s per-`ToolUseBlock` loop) in a `Stopwatch`, passes elapsed ms + `_context.Task.TaskName` to `Logger.ToolCall`/`Logger.ToolResult`.

**`BoukenshaHost.cs`:**
- Generates `var sessionId = Logger.GenerateSessionId();` once, passes `sessionId: sessionId` to `new Logger(...)` (already accepts an optional `sessionId` parameter — just needs to actually receive one instead of relying on its own default) and to the `KnowledgeStore` constructor (new parameter, see below).
- Adds `["system"] = system` to the `session_start` snapshot dict (known upfront, before `Logger` is constructed — no ordering issue here).
- After the MCP registration loop and the `options.Configure?.Invoke(...)` call (so any `RunDsl`-registered tools are included too), calls `logger.ToolCatalog(context.Tools)` once — full `{name, description, parameters}` fidelity for every tool actually available this session, logged once rather than repeated on every `prompt` event (the set doesn't change mid-session).

**`KnowledgeStore.cs`:**
- Constructor gains `string? sessionId = null`.
- New private `RecordChange(string kind, object? before, object? after)` appends a JSONL line to `<config dir>/knowledge_changes.jsonl` (same directory as `knowledge.db`, sibling file — not a new SQLite table, matching the journal's own JSONL-based CDC design and this codebase's existing JSONL-for-events precedent): `{at, session_id, kind, before, after}`.
- Called from every mutating method:
  - `UpsertRoom`: `kind: "room_upserted"`. First-time creation: `before: null`, `after: {id, name, description, visit_count: 1}`. Revisit: `before: {id, visit_count: N}`, `after: {id, visit_count: N+1}` — a plain revisit counts as a change worth recording (visit frequency over time is itself a useful signal).
  - `RecordExits`: one `kind: "exit_recorded"` entry per direction actually written (i.e., per direction whose `to_room_name_hint` or row didn't already exist as `walked` — matches the existing non-destructive-upsert behavior, just adding a journal entry alongside it).
  - `LinkExit`: `kind: "exit_linked"`, `before: {direction, state: previous state}`, `after: {direction, state: "walked", to_room_id}`.
  - `SetCurrentRoom`: `kind: "location_changed"`, `before: {room_id: previous}`, `after: {room_id: new}`.
  - `ClearCurrentRoom`: `kind: "location_cleared"`, `before: {room_id: previous}`, `after: null`.

## Testing

- Ruby: no existing test suite in `mud_manager` (confirmed — only `examples/*.rb` smoke scripts exist, no test framework). Verified via a live smoke check instead (same discipline as the ground-truth captures from the memory sub-project): connect, send a command, confirm `telnet.jsonl` gets both a `send` and `recv` line with the expected text.
- `.NET`: xUnit tests for the new `KnowledgeStore.RecordChange` call sites (assert the right `kind`/`before`/`after` shape lands in the journal file for each mutating method) and for `Logger`'s new fields showing up in serialized output. No new tests for `Agent`'s `Stopwatch` wrapping itself (timing values aren't meaningfully assertable in a unit test) — verified functionally via the live end-to-end check instead.

## Out of scope for this pass

- The `Boukensha.Observability` viewer itself (Spec 2)
- `AgentHooks`-level timing (SQLite writes are fast; the real bottlenecks — LLM call, MUD round-trip — are covered)
- Correlating a specific `telnet.jsonl` line to a specific MCP `tool_call`/`tool_result` pair programmatically — the viewer (Spec 2) can do this by timestamp-range overlap, but this spec only produces the raw data, not the correlation logic
- Live/cockpit polling mechanism (Spec 2)
