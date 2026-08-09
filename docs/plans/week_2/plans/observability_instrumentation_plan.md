# Observability Instrumentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent (see `docs/plans/week_2/dotnet_port_plan.md`'s header).

**Goal:** Implement the design in `docs/plans/week_2/observability_instrumentation.md`: raw telnet logging in `mud_manager` (Ruby), and richer `Logger`/`Agent`/`BoukenshaHost`/`KnowledgeStore` instrumentation (durations, full prompt/tool fidelity, task attribution, a CDC change journal) in the `.NET` port.

**Architecture:** Six tasks, ordered by dependency: Ruby telnet logging (independent) → `Logger` gets new fields/methods → `KnowledgeStore` gets the CDC journal (depends on nothing new from Logger) → `Agent` gets `Stopwatch` timing and threads `task`/duration through to the now-changed `Logger` calls → `BoukenshaHost` wires a single generated session id into both `Logger` and `KnowledgeStore`, adds `system` to the snapshot, and fires `logger.ToolCatalog` once after tool registration → end-to-end verification.

**Tech Stack:** No new dependencies. `System.Diagnostics.Stopwatch` (BCL) for timing.

## Decisions logged during execution

- **`KnowledgeStore`'s change journal writer switched from a persistent open `StreamWriter` (matching `Logger`'s pattern) to an open-append-close-per-write pattern.** Discovered via test failures: a persistent writer held open for the `KnowledgeStore`'s whole lifetime caused a genuine Windows `IOException` ("being used by another process") when a test tried to read the file back while the store was still alive — reproducible in complete isolation, not a parallel-test-execution artifact. Widening the writer's `FileShare` to `ReadWrite` didn't fix it; switching to open-write-close per `RecordChange` call did, and is arguably a better fit anyway since change-journal writes are low-frequency (not a hot path like the per-event session `Logger`, which keeps its persistent-writer pattern). Side effect: `knowledge_changes.jsonl` is now created lazily on first write rather than eagerly in the constructor — one test (`ClearCurrentRoom_WhenAlreadyUnknown_WritesNoChangeEntry`) had to account for the file potentially not existing at all yet.
- **`Logger.cs`'s persistent writer also widened from `FileShare.Read` to `FileShare.ReadWrite`**, even though no test exercised a failure there (every `LoggerTests` case disposes the logger before reading its file back) — done proactively since the eventual `Boukensha.Observability` viewer (Spec 2) will need to read a session's JSONL log *while the agent process that's writing it is still running*, a cross-process version of the exact contention this task's test failure just surfaced.

## Global Constraints

- No behavior changes to existing tool-call semantics or MUD command translation — only new logging is added.
- `RecordExits`'s non-destructive-upsert invariant (never overwrite an already-`walked` exit) must still hold — verified by the existing `RecordExits_DoesNotOverwriteAlreadyWalkedExit` test, which must keep passing unmodified.
- Ruby telnet logging is opt-in via `MUD_TELNET_LOG` env var — `mud_manager` must behave identically to today when it's unset.
- All new `.NET` JSONL fields use the same `snake_case` key convention already established (`duration_ms`, `session_id`, `tool_catalog`, etc.).

---

## Task 1: Ruby — `mud_manager` raw telnet logging

**Files:**
- Create: `week0_explore/mud_manager/lib/mud_manager/telnet_log.rb`
- Modify: `week0_explore/mud_manager/lib/mud_manager/session.rb`
- Modify: `week0_explore/mud_manager/lib/mud_manager.rb` (require the new file — check its current require list first)
- Modify: `week0_explore/mud_manager/bin/mud-manager`

**Interfaces produced:** `MudManager::TelnetLog.new(path)` with `#record(direction:, text:)` and `#close`; `Session.new(..., telnet_log_path: nil)`.

- [ ] `week0_explore/mud_manager/lib/mud_manager.rb` currently reads:
```ruby
module MudManager
end

require_relative "mud_manager/version"
require_relative "mud_manager/primitives"
require_relative "mud_manager/session"
require_relative "mud_manager/mcp_server"
require_relative "mud_manager/mcp_tools"
```
  Insert `require_relative "mud_manager/telnet_log"` between the `primitives` and `session` lines (loaded before `session.rb`, which will reference `TelnetLog`).
- [ ] Write `week0_explore/mud_manager/lib/mud_manager/telnet_log.rb`:
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
- [ ] Modify `week0_explore/mud_manager/lib/mud_manager/session.rb`:
  - `initialize` (line 34): add `telnet_log_path: nil` keyword arg, and inside the body add `@telnet_log = telnet_log_path ? TelnetLog.new(telnet_log_path) : nil`.
  - `send_command` (line 76-88): after `@socket.write(line + "\r\n")`, add `@telnet_log&.record(direction: "send", text: line)`.
  - `start_reader`'s loop (line 198-225): inside `unless text.empty?` (line 205), before the `@buffer_mu.synchronize do` block, add `@telnet_log&.record(direction: "recv", text: text)`.
  - `close` (line 61-72): add `@telnet_log&.close` before `@socket = nil`.
- [ ] Modify `week0_explore/mud_manager/bin/mud-manager`:
  - Add to the header comment's env var list: `MUD_TELNET_LOG  path to append raw send/recv telnet traffic as JSONL (optional)`.
  - After line 39 (`password = ENV["MUD_PASSWORD"]`), add `telnet_log_path = ENV["MUD_TELNET_LOG"]`.
  - Change line 46 to `session = MudManager::Session.new(host: host, port: port, telnet_log_path: telnet_log_path)`.
- [ ] Verify with a live smoke check (no test framework exists in `mud_manager` — confirmed, matches the design doc). Write a throwaway script (not committed) that sets `telnet_log_path` to a temp file, connects, sends one command, and asserts the file has both a `"direction":"send"` and a `"direction":"recv"` line:
```ruby
$stdout.sync = true
require "mud_manager"
require "tmpdir"

Dir.mktmpdir do |dir|
  log_path = File.join(dir, "telnet.jsonl")
  s = MudManager::Session.new(host: "localhost", port: 4000, telnet_log_path: log_path)
  s.open
  s.login("dummy", "helloworld")
  s.drain
  s.send_command("look")
  s.read_until_prompt
  s.send_command("quit")
  sleep 0.5
  s.close

  lines = File.readlines(log_path)
  send_lines = lines.select { |l| l.include?('"direction":"send"') }
  recv_lines = lines.select { |l| l.include?('"direction":"recv"') }
  raise "expected send lines, got none" if send_lines.empty?
  raise "expected recv lines, got none" if recv_lines.empty?
  puts "OK: #{send_lines.size} send, #{recv_lines.size} recv lines logged"
end
```
Run it (`ruby -Ilib <script>`), confirm `OK: ...` prints, then delete the script.
- [ ] Commit.

---

## Task 2: `.NET` — `Logger` gains durations, task attribution, and `ToolCatalog`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Logger.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/LoggerTests.cs` (new file)

**Produces:** `Logger.Response(..., int durationMs)` (new trailing param), `Logger.ToolCall(string name, IReadOnlyDictionary<string,object?> args, string task)` (new `task` param), `Logger.ToolResult(string name, string result, string task, int durationMs, bool ok = true, string? error = null)` (reordered/new params), `Logger.ToolCatalog(IReadOnlyDictionary<string, ToolDefinition> tools)` (new method), `public static string Logger.GenerateSessionId()` (was `private static`).

- [ ] Modify `Response` in `Logger.cs`:
```csharp
    public void Response(string text, IReadOnlyDictionary<string, object?>? usage, string? stopReason, string? task, string? backend, double? costUsd, int durationMs) =>
        WriteLog(new()
        {
            ["phase"] = "response",
            ["text"] = text,
            ["usage"] = usage,
            ["stop_reason"] = stopReason,
            ["task"] = task,
            ["provider"] = backend,
            ["cost_usd"] = costUsd,
            ["duration_ms"] = durationMs,
        });
```
- [ ] Modify `ToolCall` and `ToolResult`:
```csharp
    public void ToolCall(string name, IReadOnlyDictionary<string, object?> args, string task) =>
        WriteLog(new() { ["phase"] = "tool_call", ["name"] = name, ["args"] = args, ["task"] = task });

    public void ToolResult(string name, string result, string task, int durationMs, bool ok = true, string? error = null) =>
        WriteLog(new()
        {
            ["phase"] = "tool_result",
            ["name"] = name,
            ["result"] = result,
            ["task"] = task,
            ["duration_ms"] = durationMs,
            ["ok"] = ok,
            ["error"] = error,
        });
```
- [ ] Add `ToolCatalog` (place it after `Prompt`, before `Compaction`, to keep prompt-adjacent events grouped):
```csharp
    public void ToolCatalog(IReadOnlyDictionary<string, ToolDefinition> tools) =>
        WriteLog(new()
        {
            ["phase"] = "tool_catalog",
            ["tools"] = tools.Values.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.ToDictionary(p => p.Key, p => new Dictionary<string, object?>
                {
                    ["type"] = p.Value.Type,
                    ["description"] = p.Value.Description,
                }),
            }).ToList(),
        });
```
- [ ] Change `private static string GenerateSessionId()` to `public static string GenerateSessionId()` (no body change).
- [ ] Write `week2_capable/dotnet/tests/Boukensha.Core.Tests/LoggerTests.cs`:
```csharp
using System.Text.Json;
using Boukensha.Core;
using Xunit;

namespace Boukensha.Core.Tests;

public class LoggerTests
{
    private static (Logger Logger, string Path) NewLogger()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_logger_test").FullName;
        var logger = new Logger(dir, sessionId: "sess-1");
        return (logger, logger.Path);
    }

    private static List<Dictionary<string, JsonElement>> ReadEvents(string path) =>
        File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!)
            .ToList();

    [Fact]
    public void Response_IncludesDurationMs()
    {
        var (logger, path) = NewLogger();
        logger.Response("hello", usage: null, stopReason: "end_turn", task: "player", backend: "anthropic", costUsd: null, durationMs: 1234);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal(1234, evt["duration_ms"].GetInt32());
    }

    [Fact]
    public void ToolCall_IncludesTask()
    {
        var (logger, path) = NewLogger();
        logger.ToolCall("move", new Dictionary<string, object?> { ["direction"] = "south" }, "player");
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("player", evt["task"].GetString());
    }

    [Fact]
    public void ToolResult_IncludesTaskAndDurationMs()
    {
        var (logger, path) = NewLogger();
        logger.ToolResult("move", "You walk south.", task: "player", durationMs: 42);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("player", evt["task"].GetString());
        Assert.Equal(42, evt["duration_ms"].GetInt32());
    }

    [Fact]
    public void ToolCatalog_ListsToolNameDescriptionAndParameters()
    {
        var (logger, path) = NewLogger();
        var tools = new Dictionary<string, ToolDefinition>
        {
            ["look"] = new("look", "Look around", new Dictionary<string, ToolParameter>
            {
                ["target"] = new("string", "what to look at"),
            }, _ => Task.FromResult("")),
        };
        logger.ToolCatalog(tools);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("tool_catalog", evt["phase"].GetString());
        var toolsArray = evt["tools"];
        Assert.Equal(1, toolsArray.GetArrayLength());
        Assert.Equal("look", toolsArray[0].GetProperty("name").GetString());
    }

    [Fact]
    public void GenerateSessionId_IsPubliclyAccessible()
    {
        var id = Logger.GenerateSessionId();
        Assert.NotEmpty(id);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter LoggerTests` — expect build failure (new `Logger` signatures don't exist as called yet, and `Agent.cs`'s existing call sites to the old signatures will also fail to compile until Task 4 updates them). **This task will not build green in isolation** — `Agent.cs` calls `Logger.ToolCall`/`ToolResult`/`Response` with the old signatures. Proceed to Task 4 before expecting a clean build; the "run test, expect fail, then pass" cycle for `LoggerTests` itself completes within this task (its own file only depends on `Logger`/`ToolDefinition`/`ToolParameter`, not `Agent`), but the *solution* won't build clean until Task 4 lands. Run `dotnet build week2_capable/dotnet/src/Boukensha.Core/Boukensha.Core.csproj` scoped to just this project after writing `Logger.cs`'s changes to confirm `Logger.cs` itself compiles, without waiting on `Agent.cs`.
- [ ] Commit (commit `Logger.cs` and `LoggerTests.cs` together — the plan will fix `Agent.cs`'s now-stale call sites in Task 4, immediately next, so the solution is broken for the shortest possible window between these two commits, matching this session's established one-file-class-at-a-time commit granularity elsewhere).

---

## Task 3: `.NET` — `KnowledgeStore` gains the CDC change journal

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeStore.cs`
- Modify: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/KnowledgeStoreTests.cs`

**Produces:** `KnowledgeStore(string path, string? sessionId = null)` (new optional param); every mutating method now also appends a JSONL line to `<dir of path>/knowledge_changes.jsonl`.

- [ ] Write the failing tests first — append to `KnowledgeStoreTests.cs`:
```csharp
    [Fact]
    public void UpsertRoom_FirstCreation_WritesChangeJournalEntryWithNullBefore()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"), sessionId: "sess-1");

        store.UpsertRoom("The Sewer Pipe", "description");

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        Assert.Single(lines);
        Assert.Contains("\"kind\":\"room_upserted\"", lines[0]);
        Assert.Contains("\"session_id\":\"sess-1\"", lines[0]);
        Assert.Contains("\"before\":null", lines[0]);
    }

    [Fact]
    public void UpsertRoom_Revisit_WritesChangeWithPreviousVisitCountAsBefore()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));

        store.UpsertRoom("The Sewer Pipe", "description");
        store.UpsertRoom("The Sewer Pipe", "description");

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"visit_count\":1", lines[1]);
    }

    [Fact]
    public void LinkExit_WritesChangeJournalEntryWithWalkedAfterState()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));
        var start = store.UpsertRoom("A", "a");
        var dest = store.UpsertRoom("B", "b");

        store.LinkExit(start.Id, "south", dest.Id);

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        var linkLine = Assert.Single(lines, l => l.Contains("\"kind\":\"exit_linked\""));
        Assert.Contains("\"state\":\"walked\"", linkLine);
    }

    [Fact]
    public void RecordExits_AlreadyWalkedExit_WritesNoChangeEntry()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));
        var start = store.UpsertRoom("A", "a");
        var dest = store.UpsertRoom("B", "b");
        store.LinkExit(start.Id, "south", dest.Id);
        var beforeCount = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl")).Length;

        store.RecordExits(start.Id, new Dictionary<string, string?> { ["south"] = "B" });

        var afterCount = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl")).Length;
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public void SetCurrentRoom_WritesLocationChangedEntry()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));
        var room = store.UpsertRoom("A", "a");

        store.SetCurrentRoom(room.Id);

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        Assert.Contains(lines, l => l.Contains("\"kind\":\"location_changed\""));
    }

    [Fact]
    public void ClearCurrentRoom_WhenAlreadyUnknown_WritesNoChangeEntry()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));

        store.ClearCurrentRoom();

        Assert.Empty(File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl")));
    }
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeStoreTests` — expect the 6 new tests to fail (no `knowledge_changes.jsonl` gets written yet; `ClearCurrentRoom_WhenAlreadyUnknown_WritesNoChangeEntry` will actually fail with a file-not-found rather than an assertion failure, since the file doesn't exist at all yet).
- [ ] Rewrite `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeStore.cs` in full:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Boukensha.Core.Knowledge;

public sealed record RoomRecord(int Id, string Fingerprint, string Name, string Description, int VisitCount);

public sealed class KnowledgeStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly StreamWriter _changeLog;
    private readonly Lock _changeLogLock = new();
    private readonly string? _sessionId;

    public KnowledgeStore(string path, string? sessionId = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _sessionId = sessionId;

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        CreateSchema();

        var changeLogPath = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "knowledge_changes.jsonl");
        _changeLog = new StreamWriter(new FileStream(changeLogPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public RoomRecord UpsertRoom(string name, string description)
    {
        var fingerprint = ComputeFingerprint(name, description);
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO rooms (fingerprint, name, description, first_seen_at, last_seen_at, visit_count)
            VALUES ($fingerprint, $name, $description, $now, $now, 1)
            ON CONFLICT(fingerprint) DO UPDATE SET
                last_seen_at = $now,
                visit_count = visit_count + 1
            RETURNING id, visit_count;
            """;
        upsert.Parameters.AddWithValue("$fingerprint", fingerprint);
        upsert.Parameters.AddWithValue("$name", name);
        upsert.Parameters.AddWithValue("$description", description);
        upsert.Parameters.AddWithValue("$now", now);

        using var reader = upsert.ExecuteReader();
        reader.Read();
        var id = reader.GetInt32(0);
        var visitCount = reader.GetInt32(1);

        RecordChange("room_upserted",
            before: visitCount == 1 ? null : new { id, visit_count = visitCount - 1 },
            after: new { id, name, description, visit_count = visitCount });

        return new RoomRecord(id, fingerprint, name, description, visitCount);
    }

    public void RecordExits(int roomId, IReadOnlyDictionary<string, string?> directionToDestinationHint)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (direction, hint) in directionToDestinationHint)
        {
            var previousState = GetExitState(roomId, direction);
            if (previousState == "walked") continue;

            using var upsert = _connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO exits (room_id, direction, to_room_name_hint, state, updated_at)
                VALUES ($roomId, $direction, $hint, 'frontier', $now)
                ON CONFLICT(room_id, direction) DO UPDATE SET
                    to_room_name_hint = excluded.to_room_name_hint,
                    updated_at = $now;
                """;
            upsert.Parameters.AddWithValue("$roomId", roomId);
            upsert.Parameters.AddWithValue("$direction", direction);
            upsert.Parameters.AddWithValue("$hint", (object?)hint ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();

            RecordChange("exit_recorded",
                before: new { room_id = roomId, direction, state = previousState },
                after: new { room_id = roomId, direction, state = "frontier", hint });
        }
    }

    public void LinkExit(int fromRoomId, string direction, int toRoomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var previousState = GetExitState(fromRoomId, direction);

        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO exits (room_id, direction, to_room_id, state, updated_at)
            VALUES ($roomId, $direction, $toRoomId, 'walked', $now)
            ON CONFLICT(room_id, direction) DO UPDATE SET
                to_room_id = $toRoomId,
                state = 'walked',
                to_room_name_hint = NULL,
                updated_at = $now;
            """;
        upsert.Parameters.AddWithValue("$roomId", fromRoomId);
        upsert.Parameters.AddWithValue("$direction", direction);
        upsert.Parameters.AddWithValue("$toRoomId", toRoomId);
        upsert.Parameters.AddWithValue("$now", now);
        upsert.ExecuteNonQuery();

        RecordChange("exit_linked",
            before: new { room_id = fromRoomId, direction, state = previousState },
            after: new { room_id = fromRoomId, direction, state = "walked", to_room_id = toRoomId });
    }

    public RoomRecord? GetCurrentRoom()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.fingerprint, r.name, r.description, r.visit_count
            FROM location l JOIN rooms r ON r.id = l.current_room_id
            WHERE l.id = 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new RoomRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
    }

    public void SetCurrentRoom(int roomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var previousRoomId = GetCurrentRoom()?.Id;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO location (id, current_room_id, updated_at) VALUES (1, $roomId, $now)
            ON CONFLICT(id) DO UPDATE SET current_room_id = $roomId, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        RecordChange("location_changed",
            before: previousRoomId is null ? null : new { room_id = previousRoomId },
            after: new { room_id = roomId });
    }

    /// <summary>
    /// Marks the current location as unknown -- used when a transition (move/flee)
    /// lands somewhere unparseable (e.g. a dark room), so a stale current_room_id
    /// doesn't cause later tool results to be misattributed to a room the player
    /// has actually already left.
    /// </summary>
    public void ClearCurrentRoom()
    {
        var previousRoomId = GetCurrentRoom()?.Id;
        if (previousRoomId is null) return;

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO location (id, current_room_id, updated_at) VALUES (1, NULL, $now)
            ON CONFLICT(id) DO UPDATE SET current_room_id = NULL, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        RecordChange("location_cleared", before: new { room_id = previousRoomId }, after: null);
    }

    public string BuildHereBlock()
    {
        var current = GetCurrentRoom();
        if (current is null) return string.Empty;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.direction, e.state, dest.name
            FROM exits e LEFT JOIN rooms dest ON dest.id = e.to_room_id
            WHERE e.room_id = $roomId ORDER BY e.direction;
            """;
        cmd.Parameters.AddWithValue("$roomId", current.Id);

        var exitParts = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var direction = reader.GetString(0);
                var state = reader.GetString(1);
                var letter = direction[0];
                exitParts.Add(state == "walked" && !reader.IsDBNull(2)
                    ? $"{letter}→{reader.GetString(2)} ✓"
                    : $"{letter}→?");
            }
        }

        var exitsLine = exitParts.Count > 0 ? string.Join(" | ", exitParts) : "(none surveyed)";
        return $"[here] {current.Name} (visit {current.VisitCount})\nexits: {exitsLine}";
    }

    public void Dispose()
    {
        _connection.Dispose();
        _changeLog.Dispose();
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rooms (
                id INTEGER PRIMARY KEY,
                fingerprint TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS exits (
                room_id INTEGER NOT NULL REFERENCES rooms(id),
                direction TEXT NOT NULL,
                to_room_id INTEGER REFERENCES rooms(id),
                to_room_name_hint TEXT,
                state TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (room_id, direction)
            );
            CREATE TABLE IF NOT EXISTS location (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                current_room_id INTEGER REFERENCES rooms(id),
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private string? GetExitState(int roomId, string direction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT state FROM exits WHERE room_id = $roomId AND direction = $direction;";
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$direction", direction);
        return cmd.ExecuteScalar() as string;
    }

    private void RecordChange(string kind, object? before, object? after)
    {
        var evt = new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["session_id"] = _sessionId,
            ["kind"] = kind,
            ["before"] = before,
            ["after"] = after,
        };
        lock (_changeLogLock)
        {
            _changeLog.WriteLine(JsonSerializer.Serialize(evt));
        }
    }

    private static string ComputeFingerprint(string name, string description)
    {
        var normalized = $"{Normalize(name)}\n{Normalize(description)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeStoreTests` — expect all tests to pass, including the pre-existing `RecordExits_DoesNotOverwriteAlreadyWalkedExit`/`UpsertRoom_SameFingerprintIncrementsVisitCountInsteadOfDuplicating`/etc. from the memory sub-project (regression check — the `RecordExits` SQL was restructured from a `CASE WHEN`-guarded conditional `UPDATE` to a C#-level `continue`-on-already-walked check; behavior must be identical).
- [ ] Commit.

---

## Task 4: `.NET` — `Agent` gains `Stopwatch` timing and threads `task` through

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Agent.cs`

**Consumes:** `Logger`'s new signatures (Task 2).
**Modifies:** `RunAsync`'s LLM call and `WrapUpAsync`'s LLM call are each timed; `LogResponse` gains a `durationMs` parameter; `HandleToolCallsAsync`'s per-tool dispatch is timed and passes `_context.Task.TaskName` to `ToolCall`/`ToolResult`.

- [ ] Add `using System.Diagnostics;` to the top of `Agent.cs`.
- [ ] In `RunAsync`, wrap the LLM call and update the `LogResponse` call:
```csharp
            _iteration++;
            _logger.Iteration(_iteration, _maxIterations);
            await _hooks.RaiseBeforeAgentCall(_context, cancellationToken);
            _logger.Prompt(_context.Messages, _context.Tools, _context.ContextWindow);

            var stopwatch = Stopwatch.StartNew();
            var response = await _client.CallAsync(_maxOutputTokens ?? 1024, cancellationToken: cancellationToken);
            stopwatch.Stop();
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            LogReasoning(parsed.Content);

            if (parsed.StopReason == "tool_use")
            {
                await HandleToolCallsAsync(parsed.Content, cancellationToken);
                continue;
            }

            var text = ExtractText(parsed.Content);
            LogResponse(text, response, parsed.StopReason, (int)stopwatch.ElapsedMilliseconds);
```
- [ ] In `WrapUpAsync`, wrap its LLM call the same way:
```csharp
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await _client.CallAsync(WrapUpOutputTokens, tools: [], cancellationToken: cancellationToken);
            stopwatch.Stop();
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            text = ExtractText(parsed.Content);
            if (string.IsNullOrWhiteSpace(text)) text = FallbackMessage(reason);
            LogResponse(text, response, parsed.StopReason, (int)stopwatch.ElapsedMilliseconds);
        }
```
- [ ] Update `LogResponse`'s signature and its `_logger.Response(...)` call:
```csharp
    private void LogResponse(string text, JsonNode response, string stopReason, int durationMs)
    {
        var usage = response["usage"] is JsonObject u ? JsonUtil.ToObject(u) as IReadOnlyDictionary<string, object?> : null;
        double? cost = null;
        if (usage is not null
            && usage.TryGetValue("input_tokens", out var i)
            && usage.TryGetValue("output_tokens", out var o)
            && i is not null && o is not null)
        {
            cost = _builder.Backend.EstimateCost(Convert.ToInt32(i), Convert.ToInt32(o));
        }
        _logger.Response(text, usage, stopReason, _context.Task.TaskName, BackendName(), cost, durationMs);
    }
```
- [ ] Update `HandleToolCallsAsync`'s per-tool loop:
```csharp
        foreach (var block in content.OfType<ToolUseBlock>())
        {
            _logger.ToolCall(block.Name, block.Input, _context.Task.TaskName);
            await _hooks.RaiseBeforeToolCall(block.Name, block.Input, cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            string result;
            bool ok = true;
            string? error = null;
            try
            {
                result = await _registry.DispatchAsync(block.Name, block.Input);
            }
            catch (Exception e)
            {
                ok = false;
                error = e.Message;
                result = $"ERROR: {e.GetType().Name}: {e.Message}";
            }
            stopwatch.Stop();
            _logger.ToolResult(block.Name, result, _context.Task.TaskName, (int)stopwatch.ElapsedMilliseconds, ok, error);
            await _hooks.RaiseAfterToolCall(block.Name, block.Input, result, ok, cancellationToken);
            _context.AddMessage("tool_result", result, block.Id);
        }
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds (this is the point where `Logger.cs`'s Task 2 signature changes and `Agent.cs`'s call sites finally agree — confirms Task 2 + Task 4 together compile clean).
- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — all tests pass, including `LoggerTests` from Task 2.
- [ ] Commit.

---

## Task 5: `.NET` — `BoukenshaHost` wires a shared session id, `system` snapshot field, and `ToolCatalog`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs`

**Consumes:** `Logger.GenerateSessionId()` (Task 2), `KnowledgeStore(path, sessionId)` (Task 3), `Logger.ToolCatalog` (Task 2).

- [ ] Insert a shared session id generation before the `logger` construction, and pass it to both `Logger` and `KnowledgeStore`:
```csharp
        var sessionId = Logger.GenerateSessionId();

        var logger = new Logger(Path.Combine(config.Dir, "sessions"), sessionId: sessionId, log: options.Log, snapshot: new Dictionary<string, object?>
        {
            ["task"] = task.TaskName,
            ["provider"] = backendName,
            ["model"] = model,
            ["context_window"] = contextWindow,
            ["max_turn_tokens"] = config.AgentMaxTurnTokens,
            ["system"] = system,
        });

        var knowledgeStore = new Knowledge.KnowledgeStore(Path.Combine(config.Dir, "knowledge.db"), sessionId: sessionId);
```
- [ ] After `options.Configure?.Invoke(new RunDsl(registry));`, add:
```csharp
        logger.ToolCatalog(context.Tools);
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — all tests pass.
- [ ] Commit.

---

## Task 6: End-to-end verification

**Files:** none (verification only).

- [x] Ran: `dotnet test week2_capable/dotnet/Boukensha.slnx` — all 53 tests pass.
- [x] Ran: `dotnet clean week2_capable/dotnet/Boukensha.slnx && dotnet build week2_capable/dotnet/Boukensha.slnx` — 0 errors, 6 warnings, all the already-accepted NU1903 SQLite advisory (same count as before this spec — no new warnings).
- [x] Set `MUD_TELNET_LOG: C:/Lab/aralab/exampro/claude-code-camp-2026-Q2/.boukensha/telnet.jsonl` in `.boukensha/settings.yaml`'s `mcp_servers.mud.env`.
- [x] Reset the test character's connection state cleanly (throwaway `connect + quit` script, same as every prior live run this session).
- [x] Ran one live turn: `BOUKENSHA_DIR=".../.boukensha" dotnet run --project week2_capable/dotnet/src/Boukensha.Console -- --no-tui`, task "look around, check the exits, then move in any open direction" — exercised `look`, `check kind=exits`, and `move`.
- [x] Session JSONL log confirmed: `session_start` has a real `system` prompt; `tool_catalog` fired exactly once listing all ~20 tools with `name`/`description`/`parameters` (`mud_connect`, `look`, etc.); every `tool_call`/`tool_result` has `"task":"player"`; `tool_result` `duration_ms` values were plausible fast MUD round-trips (22–96ms); the final `response` had `duration_ms: 2270` (plausible LLM call time).
- [x] `telnet.jsonl` confirmed populated with real interleaved `send`/`recv` lines (28 total), including the full CircleMUD login banner and password prompt, timestamped across the same window as the session's tool calls.
- [x] `knowledge_changes.jsonl` confirmed: 8 entries this run (`room_upserted` ×1, `exit_recorded` ×4, `exit_linked` ×1, `location_changed` ×1, `location_cleared` ×1), cross-checked against `knowledge.db`'s final state — the one `room_upserted` matched a revisit of the already-known "Grand Sewer" (visit_count 1→2, matching the model's own transcript describing seeing it twice); the final `location_cleared` correctly left `location.current_room_id` `NULL`, matching the run ending in an unparseable dark room.
- [x] Discovered and fixed a real Windows file-sharing bug during this task (see "Decisions logged during execution" above) — not something a design review would have caught, only live/integration testing surfaced it.
- [x] Updated this plan's checkboxes and `docs/plans/week_2/observability_instrumentation.md`'s status line to reflect completion.
- [ ] Commit (final) — single commit for all of Tasks 1–6, per the user's request mid-execution to batch rather than commit per task.
