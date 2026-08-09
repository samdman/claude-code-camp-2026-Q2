# Basic Memory + Lifecycle Hooks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent (see `docs/plans/week_2/dotnet_port_plan.md`'s header and `docs/plans/python_port/IMPLEMENTATION.md`'s "Execution Workflow Notes": direct in-session execution, one commit per task, no per-task subagent round trips).

**Goal:** Add a SQLite knowledge store (rooms/exits/current location) and three generic lifecycle hooks (`before_agent_call`/`before_tool_call`/`after_tool_call`) to the `.NET` `boukensha` agent built in `docs/plans/week_2/dotnet_port.md`, per the design in `docs/plans/week_2/basic_memory.md`.

**Architecture:** New `AgentHooks` class wired into `Agent`'s existing loop at three firing points; a new `Boukensha.Core.Knowledge` namespace (`MudTextParser`, `KnowledgeStore`, `KnowledgeHooks`) that subscribes to those hooks to populate SQLite from raw MUD tool output and inject a compact `[here]` state block before every model call.

**Tech Stack:** `Microsoft.Data.Sqlite` (new dependency, WAL mode) added to `Boukensha.Core`. No other new dependencies.

## Global Constraints

- Schema scope this pass: `rooms`, `exits`, `location` only — no player vitals/inventory/entities/CDC journal (see design doc's "Out of scope").
- Hooks are passive recorders only — no tool-call gating/denial this pass.
- `[here]` injection fires unconditionally every iteration (no de-duplication) — acceptable per the design doc, deferred optimization for a later sub-project.
- Room identity = SHA-256 fingerprint of normalized `name+description`, since CircleMUD never exposes a room vnum to players.
- All directions normalized to full compass words (`north`/`east`/`south`/`west`/`up`/`down`) everywhere in the knowledge store — never store single-letter abbreviations.
- Parser tests must use the real captured fixture strings from the design doc's "Ground truth" section, not synthetic/assumed text.

---

## File Structure

```
week2_capable/dotnet/
  src/Boukensha.Core/
    Boukensha.Core.csproj             # modified: + Microsoft.Data.Sqlite
    AgentHooks.cs                      # new
    Agent.cs                           # modified: hooks field + firing points
    BoukenshaHost.cs                   # modified: wire KnowledgeStore + KnowledgeHooks
    Knowledge/
      MudTextParser.cs                 # new
      KnowledgeStore.cs                # new
      KnowledgeHooks.cs                # new
  tests/Boukensha.Core.Tests/
    AgentHooksTests.cs                 # new
    Knowledge/
      MudTextParserTests.cs            # new
      KnowledgeStoreTests.cs           # new
```

---

## Decisions logged during execution

- **`Microsoft.Data.Sqlite` 10.0.10 pulls in `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which has a known NU1903 high-severity advisory (CVE-2025-6965, memory corruption in SQLite's aggregate-query handling, fixed upstream in SQLite 3.50.2+).** No patched `SQLitePCLRaw` NuGet package exists yet (confirmed via the advisory page — no fixed version listed). Accepted as a documented risk rather than blocked on: every SQL string in `KnowledgeStore` is static, parameterized text written by us, never built from untrusted input or dynamic aggregate expressions, so this specific crafted-query attack surface isn't reachable through this codebase's usage. Revisit if `SQLitePCLRaw` ships a fix.

## Task 1: Add Microsoft.Data.Sqlite dependency

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Boukensha.Core.csproj`

- [ ] Run: `dotnet add week2_capable/dotnet/src/Boukensha.Core/Boukensha.Core.csproj package Microsoft.Data.Sqlite`
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 2: `MudTextParser` (tested against real captured fixtures)

**Files:**
- Create: `src/Boukensha.Core/Knowledge/MudTextParser.cs`
- Test: `tests/Boukensha.Core.Tests/Knowledge/MudTextParserTests.cs`

**Produces:** `MudTextParser.StripAnsi(string)`, `MudTextParser.NormalizeDirection(string)`, `MudTextParser.ParseRoomBlock(string) -> (string Name, string Description, IReadOnlyList<string> ExitLetters)?`, `MudTextParser.ParseExitsBlock(string) -> IReadOnlyDictionary<string, string?>`.

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/Knowledge/MudTextParserTests.cs`, using the exact raw strings captured live in the design doc (design doc: `docs/plans/week_2/basic_memory.md`, "Ground truth" section):
```csharp
using Boukensha.Core.Knowledge;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class MudTextParserTests
{
    private const string LitRoomLook =
        "[0;33mThe Sewer Pipe[0m\r\n" +
        "   You are in what reminds you of a foul sewer, as if you liked being here!\r\n" +
        "You can see two exits leading either north or south.\r\n" +
        "[0;36m[ Exits: n s ][0m\r\n" +
        "[0;33mThe small hairy Spider is here, busy with its web.\r\n[0m\r\n" +
        "21H 100M 84V (news) (motd) > ";

    private const string DarkRoomLook =
        "It is pitch black...\r\n[0;33m[0m\r\n21H 100M 85V (news) (motd) > ";

    private const string ExitsBlock =
        "Obvious exits:\r\nnorth - Too dark to tell.\r\nsouth - The Grand Sewer\r\n\r\n21H 100M 84V (news) (motd) > ";

    [Fact]
    public void StripAnsi_RemovesColorCodes()
    {
        var stripped = MudTextParser.StripAnsi("[0;33mThe Sewer Pipe[0m");
        Assert.Equal("The Sewer Pipe", stripped);
    }

    [Theory]
    [InlineData("n", "north")]
    [InlineData("e", "east")]
    [InlineData("s", "south")]
    [InlineData("w", "west")]
    [InlineData("u", "up")]
    [InlineData("d", "down")]
    [InlineData("north", "north")]
    [InlineData("D", "down")]
    public void NormalizeDirection_MapsLettersAndPassesThroughFullWords(string input, string expected)
    {
        Assert.Equal(expected, MudTextParser.NormalizeDirection(input));
    }

    [Fact]
    public void ParseRoomBlock_ExtractsNameDescriptionAndExitLetters()
    {
        var parsed = MudTextParser.ParseRoomBlock(LitRoomLook);

        Assert.NotNull(parsed);
        Assert.Equal("The Sewer Pipe", parsed!.Value.Name);
        Assert.Equal("You are in what reminds you of a foul sewer, as if you liked being here!", parsed.Value.Description);
        Assert.Equal(["n", "s"], parsed.Value.ExitLetters);
    }

    [Fact]
    public void ParseRoomBlock_ReturnsNullForDarkRoom()
    {
        Assert.Null(MudTextParser.ParseRoomBlock(DarkRoomLook));
    }

    [Fact]
    public void ParseExitsBlock_ExtractsDirectionsAndDestinations()
    {
        var exits = MudTextParser.ParseExitsBlock(ExitsBlock);

        Assert.Equal(2, exits.Count);
        Assert.Null(exits["north"]);
        Assert.Equal("The Grand Sewer", exits["south"]);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter MudTextParserTests` — expect build failure (`MudTextParser` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/Knowledge/MudTextParser.cs`:
```csharp
using System.Text.RegularExpressions;

namespace Boukensha.Core.Knowledge;

public static class MudTextParser
{
    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);
    private static readonly Regex ExitsLinePattern = new(@"^\[\s*Exits:\s*([a-z\s]*)\]$", RegexOptions.Compiled);
    private static readonly Regex ExitEntryPattern = new(@"^(\w+)\s*-\s*(.+)$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> DirectionLetters = new Dictionary<string, string>
    {
        ["n"] = "north",
        ["e"] = "east",
        ["s"] = "south",
        ["w"] = "west",
        ["u"] = "up",
        ["d"] = "down",
    };

    private static readonly HashSet<string> FullDirections = ["north", "east", "south", "west", "up", "down"];

    public static string StripAnsi(string raw) => AnsiPattern.Replace(raw, string.Empty);

    public static string NormalizeDirection(string directionOrLetter)
    {
        var trimmed = directionOrLetter.Trim().ToLowerInvariant();
        return DirectionLetters.TryGetValue(trimmed, out var full) ? full : trimmed;
    }

    public static (string Name, string Description, IReadOnlyList<string> ExitLetters)? ParseRoomBlock(string raw)
    {
        var clean = StripAnsi(raw).Replace("\r\n", "\n");
        if (clean.Contains("It is pitch black")) return null;

        var lines = clean.Split('\n');
        if (lines.Length == 0) return null;

        var name = lines[0].Trim();
        if (name.Length == 0) return null;

        var descriptionLines = new List<string>();
        var exitLetters = new List<string>();
        var foundExitsLine = false;

        foreach (var line in lines.Skip(1))
        {
            var trimmedLine = line.Trim();
            var match = ExitsLinePattern.Match(trimmedLine);
            if (match.Success)
            {
                exitLetters = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                foundExitsLine = true;
                break;
            }
            if (trimmedLine.Length > 0) descriptionLines.Add(trimmedLine);
        }

        if (!foundExitsLine) return null;

        var description = descriptionLines.Count > 0 ? descriptionLines[0] : string.Empty;
        return (name, description, exitLetters);
    }

    public static IReadOnlyDictionary<string, string?> ParseExitsBlock(string raw)
    {
        var clean = StripAnsi(raw).Replace("\r\n", "\n");
        var result = new Dictionary<string, string?>();

        foreach (var line in clean.Split('\n'))
        {
            var match = ExitEntryPattern.Match(line.Trim());
            if (!match.Success) continue;

            var direction = NormalizeDirection(match.Groups[1].Value);
            if (!FullDirections.Contains(direction)) continue;

            var destination = match.Groups[2].Value.Trim();
            result[direction] = destination.Equals("Too dark to tell.", StringComparison.OrdinalIgnoreCase) ? null : destination;
        }

        return result;
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter MudTextParserTests` — expect all pass.
- [ ] Commit.

---

## Task 3: `KnowledgeStore` (tested)

**Files:**
- Create: `src/Boukensha.Core/Knowledge/KnowledgeStore.cs`
- Test: `tests/Boukensha.Core.Tests/Knowledge/KnowledgeStoreTests.cs`

**Consumes:** nothing from earlier tasks (self-contained; SQLite only).
**Produces:** `RoomRecord(int Id, string Fingerprint, string Name, string Description, int VisitCount)`; `KnowledgeStore(string path) : IDisposable` with `UpsertRoom(string, string) -> RoomRecord`, `RecordExits(int, IReadOnlyDictionary<string, string?>)`, `LinkExit(int, string, int)`, `GetCurrentRoom() -> RoomRecord?`, `SetCurrentRoom(int)`, `BuildHereBlock() -> string`.

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/Knowledge/KnowledgeStoreTests.cs`:
```csharp
using Boukensha.Core.Knowledge;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class KnowledgeStoreTests
{
    private static KnowledgeStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName, "knowledge.db"));

    [Fact]
    public void UpsertRoom_SameFingerprintIncrementsVisitCountInsteadOfDuplicating()
    {
        using var store = NewStore();

        var first = store.UpsertRoom("The Sewer Pipe", "You are in what reminds you of a foul sewer.");
        var second = store.UpsertRoom("The Sewer Pipe", "You are in what reminds you of a foul sewer.");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, first.VisitCount);
        Assert.Equal(2, second.VisitCount);
    }

    [Fact]
    public void UpsertRoom_DifferentDescriptionIsADifferentRoom()
    {
        using var store = NewStore();

        var a = store.UpsertRoom("The Sewer Pipe", "description A");
        var b = store.UpsertRoom("The Sewer Pipe", "description B");

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void GetCurrentRoom_NullUntilSet()
    {
        using var store = NewStore();
        Assert.Null(store.GetCurrentRoom());

        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        Assert.Equal(room.Id, store.GetCurrentRoom()!.Id);
    }

    [Fact]
    public void LinkExit_ThenBuildHereBlock_ShowsWalkedDestinationWithCheckmark()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("The Sewer Pipe", "start description");
        var dest = store.UpsertRoom("The Grand Sewer", "dest description");
        store.SetCurrentRoom(start.Id);

        store.LinkExit(start.Id, "south", dest.Id);

        var here = store.BuildHereBlock();
        Assert.Contains("[here] The Sewer Pipe (visit 1)", here);
        Assert.Contains("s→The Grand Sewer ✓", here);
    }

    [Fact]
    public void RecordExits_FrontierExitShowsQuestionMark()
    {
        using var store = NewStore();
        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        store.RecordExits(room.Id, new Dictionary<string, string?> { ["north"] = null, ["south"] = "The Grand Sewer" });

        var here = store.BuildHereBlock();
        Assert.Contains("n→?", here);
    }

    [Fact]
    public void RecordExits_DoesNotOverwriteAlreadyWalkedExit()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("The Sewer Pipe", "start description");
        var dest = store.UpsertRoom("The Grand Sewer", "dest description");
        store.SetCurrentRoom(start.Id);
        store.LinkExit(start.Id, "south", dest.Id);

        // A later `check exits` call re-reports the same direction as a hint --
        // must not clobber the already-walked link.
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["south"] = "The Grand Sewer" });

        var here = store.BuildHereBlock();
        Assert.Contains("s→The Grand Sewer ✓", here);
    }

    [Fact]
    public void BuildHereBlock_EmptyWhenNoCurrentRoom()
    {
        using var store = NewStore();
        Assert.Equal(string.Empty, store.BuildHereBlock());
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeStoreTests` — expect build failure (`KnowledgeStore` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/Knowledge/KnowledgeStore.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Boukensha.Core.Knowledge;

public sealed record RoomRecord(int Id, string Fingerprint, string Name, string Description, int VisitCount);

public sealed class KnowledgeStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public KnowledgeStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        CreateSchema();
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
        return new RoomRecord(reader.GetInt32(0), fingerprint, name, description, reader.GetInt32(1));
    }

    public void RecordExits(int roomId, IReadOnlyDictionary<string, string?> directionToDestinationHint)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (direction, hint) in directionToDestinationHint)
        {
            using var upsert = _connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO exits (room_id, direction, to_room_name_hint, state, updated_at)
                VALUES ($roomId, $direction, $hint, 'frontier', $now)
                ON CONFLICT(room_id, direction) DO UPDATE SET
                    to_room_name_hint = CASE WHEN state = 'frontier' THEN excluded.to_room_name_hint ELSE to_room_name_hint END,
                    updated_at = CASE WHEN state = 'frontier' THEN $now ELSE updated_at END;
                """;
            upsert.Parameters.AddWithValue("$roomId", roomId);
            upsert.Parameters.AddWithValue("$direction", direction);
            upsert.Parameters.AddWithValue("$hint", (object?)hint ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
        }
    }

    public void LinkExit(int fromRoomId, string direction, int toRoomId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
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
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO location (id, current_room_id, updated_at) VALUES (1, $roomId, $now)
            ON CONFLICT(id) DO UPDATE SET current_room_id = $roomId, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
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

    public void Dispose() => _connection.Dispose();

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
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter KnowledgeStoreTests` — expect all pass.
- [ ] Commit.

---

## Task 4: `AgentHooks` and wiring into `Agent`

**Files:**
- Create: `src/Boukensha.Core/AgentHooks.cs`
- Modify: `src/Boukensha.Core/Agent.cs`
- Test: `tests/Boukensha.Core.Tests/AgentHooksTests.cs`

**Produces:** `AgentHooks` with `OnBeforeAgentCall`, `OnBeforeToolCall`, `OnAfterToolCall` subscription methods.
**Modifies:** `Agent`'s constructor gains an optional `AgentHooks? hooks = null` parameter (defaults to a fresh empty `AgentHooks`); `RunAsync` fires `BeforeAgentCall` each iteration before logging the prompt; `HandleToolCallsAsync` fires `BeforeToolCall`/`AfterToolCall` around each tool dispatch.

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/AgentHooksTests.cs`:
```csharp
using Boukensha.Core;
using Xunit;

namespace Boukensha.Core.Tests;

public class AgentHooksTests
{
    [Fact]
    public async Task RaiseBeforeAgentCall_InvokesAllSubscribersInOrder()
    {
        var hooks = new AgentHooks();
        var calls = new List<int>();
        hooks.OnBeforeAgentCall((_, _) => { calls.Add(1); return Task.CompletedTask; });
        hooks.OnBeforeAgentCall((_, _) => { calls.Add(2); return Task.CompletedTask; });

        await hooks.RaiseBeforeAgentCall(null!, CancellationToken.None);

        Assert.Equal([1, 2], calls);
    }

    [Fact]
    public async Task RaiseAfterToolCall_PassesNameArgsResultAndOk()
    {
        var hooks = new AgentHooks();
        string? capturedName = null;
        string? capturedResult = null;
        bool? capturedOk = null;
        hooks.OnAfterToolCall((name, args, result, ok, _) =>
        {
            capturedName = name;
            capturedResult = result;
            capturedOk = ok;
            return Task.CompletedTask;
        });

        await hooks.RaiseAfterToolCall("move", new Dictionary<string, object?> { ["direction"] = "south" }, "You walk south.", true, CancellationToken.None);

        Assert.Equal("move", capturedName);
        Assert.Equal("You walk south.", capturedResult);
        Assert.True(capturedOk);
    }

    [Fact]
    public void DefaultAgentHooks_HasNoSubscribersAndDoesNotThrowWhenRaised()
    {
        var hooks = new AgentHooks();
        // No OnX subscriptions -- Raise* must be a safe no-op, since Agent's
        // default constructor argument is `hooks ?? new AgentHooks()`.
        var task = hooks.RaiseBeforeToolCall("look", new Dictionary<string, object?>(), CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AgentHooksTests` — expect build failure (`AgentHooks` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/AgentHooks.cs`:
```csharp
namespace Boukensha.Core;

public sealed class AgentHooks
{
    private readonly List<Func<Context, CancellationToken, Task>> _beforeAgentCall = [];
    private readonly List<Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task>> _beforeToolCall = [];
    private readonly List<Func<string, IReadOnlyDictionary<string, object?>, string, bool, CancellationToken, Task>> _afterToolCall = [];

    public void OnBeforeAgentCall(Func<Context, CancellationToken, Task> handler) => _beforeAgentCall.Add(handler);

    public void OnBeforeToolCall(Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task> handler) => _beforeToolCall.Add(handler);

    public void OnAfterToolCall(Func<string, IReadOnlyDictionary<string, object?>, string, bool, CancellationToken, Task> handler) => _afterToolCall.Add(handler);

    public async Task RaiseBeforeAgentCall(Context context, CancellationToken cancellationToken)
    {
        foreach (var handler in _beforeAgentCall) await handler(context, cancellationToken);
    }

    public async Task RaiseBeforeToolCall(string name, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        foreach (var handler in _beforeToolCall) await handler(name, args, cancellationToken);
    }

    public async Task RaiseAfterToolCall(string name, IReadOnlyDictionary<string, object?> args, string result, bool ok, CancellationToken cancellationToken)
    {
        foreach (var handler in _afterToolCall) await handler(name, args, result, ok, cancellationToken);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AgentHooksTests` — expect all pass.
- [ ] Modify `src/Boukensha.Core/Agent.cs`: add a `private readonly AgentHooks _hooks;` field, add `AgentHooks? hooks = null` as the constructor's final parameter, set `_hooks = hooks ?? new AgentHooks();` in the constructor body. In `RunAsync`, insert the hook call between the iteration-increment block and the prompt log so an injected message is reflected in the logged prompt:
```csharp
            _iteration++;
            _logger.Iteration(_iteration, _maxIterations);
            await _hooks.RaiseBeforeAgentCall(_context, cancellationToken);
            _logger.Prompt(_context.Messages, _context.Tools, _context.ContextWindow);

            var response = await _client.CallAsync(_maxOutputTokens ?? 1024, cancellationToken: cancellationToken);
```
  The current `HandleToolCallsAsync` signature is `private async Task HandleToolCallsAsync(IReadOnlyList<ContentBlock> content)` — no `CancellationToken` parameter — called from `RunAsync` as `await HandleToolCallsAsync(parsed.Content);` (`Agent.cs:75`). Add a `CancellationToken cancellationToken` parameter to `HandleToolCallsAsync` and update the `RunAsync` call site to `await HandleToolCallsAsync(parsed.Content, cancellationToken);`. Then wrap the dispatch call inside it:
```csharp
        foreach (var block in content.OfType<ToolUseBlock>())
        {
            _logger.ToolCall(block.Name, block.Input);
            await _hooks.RaiseBeforeToolCall(block.Name, block.Input, cancellationToken);

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
            _logger.ToolResult(block.Name, result, ok, error);
            await _hooks.RaiseAfterToolCall(block.Name, block.Input, result, ok, cancellationToken);
            _context.AddMessage("tool_result", result, block.Id);
        }
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 5: `KnowledgeHooks` — wire the store into the hooks

**Files:**
- Create: `src/Boukensha.Core/Knowledge/KnowledgeHooks.cs`

**Consumes:** `AgentHooks` (Task 4), `KnowledgeStore`/`MudTextParser` (Tasks 2–3).
**Produces:** `KnowledgeHooks.Register(AgentHooks hooks, KnowledgeStore store)`.

- [ ] Write `src/Boukensha.Core/Knowledge/KnowledgeHooks.cs`:
```csharp
namespace Boukensha.Core.Knowledge;

public static class KnowledgeHooks
{
    public static void Register(AgentHooks hooks, KnowledgeStore store)
    {
        hooks.OnAfterToolCall((name, args, result, ok, _) =>
        {
            if (!ok) return Task.CompletedTask;

            switch (name)
            {
                case "look" when string.IsNullOrEmpty(args.GetValueOrDefault("target") as string):
                    UpdateRoomFromLookOrMove(store, result, direction: null);
                    break;
                case "move":
                    UpdateRoomFromLookOrMove(store, result, direction: args.GetValueOrDefault("direction") as string);
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
            if (!string.IsNullOrEmpty(here)) context.AddMessage("user", here);
            return Task.CompletedTask;
        });
    }

    private static void UpdateRoomFromLookOrMove(KnowledgeStore store, string result, string? direction)
    {
        var parsed = MudTextParser.ParseRoomBlock(result);
        if (parsed is null) return;

        var previousRoomId = store.GetCurrentRoom()?.Id;
        var room = store.UpsertRoom(parsed.Value.Name, parsed.Value.Description);

        if (direction is not null && previousRoomId is not null)
        {
            store.LinkExit(previousRoomId.Value, MudTextParser.NormalizeDirection(direction), room.Id);
        }

        store.SetCurrentRoom(room.Id);
    }
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 6: Wire `KnowledgeStore`/`KnowledgeHooks` into `BoukenshaHost`

**Files:**
- Modify: `src/Boukensha.Core/BoukenshaHost.cs`

**Consumes:** `KnowledgeStore`, `KnowledgeHooks` (Tasks 3, 5), `AgentHooks` (Task 4).
**Modifies:** `BuildAsync` constructs a `KnowledgeStore` at `<config dir>/knowledge.db`, registers `KnowledgeHooks`, and passes the resulting `AgentHooks` to every `Agent` the returned `BoukenshaSession.AgentFactory` builds. `BoukenshaSession` disposes the store alongside its MCP clients and logger.

- [ ] Modify `BoukenshaSession`'s constructor and `DisposeAsync` in `src/Boukensha.Core/BoukenshaHost.cs` to also own a `KnowledgeStore`:
```csharp
public sealed class BoukenshaSession(
    Context context,
    Registry registry,
    Func<Agent> agentFactory,
    Logger logger,
    IReadOnlyList<McpClient> mcpClients,
    string provider,
    string model,
    Knowledge.KnowledgeStore knowledgeStore) : IAsyncDisposable
{
    public Context Context { get; } = context;
    public Registry Registry { get; } = registry;
    public Func<Agent> AgentFactory { get; } = agentFactory;
    public Logger Logger { get; } = logger;
    public string Provider { get; } = provider;
    public string Model { get; } = model;
    public IReadOnlyList<string> McpServerNames { get; } = mcpClients.Select(c => c.Name).ToList();
    public Knowledge.KnowledgeStore Knowledge { get; } = knowledgeStore;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in mcpClients) await client.DisposeAsync();
        Logger.Dispose();
        Knowledge.Dispose();
    }
}
```
- [ ] In `BoukenshaHost.BuildAsync`, after the `logger` is constructed and before the MCP-server loop, add:
```csharp
        var knowledgeStore = new Knowledge.KnowledgeStore(Path.Combine(config.Dir, "knowledge.db"));
        var agentHooks = new AgentHooks();
        Knowledge.KnowledgeHooks.Register(agentHooks, knowledgeStore);
```
- [ ] Change `AgentFactory` to pass `agentHooks` through, and the final `return` to pass `knowledgeStore`:
```csharp
        Agent AgentFactory() => new(
            context, registry, builder, apiClient, logger, taskSettings,
            maxOutputTokens: resolvedMaxOutputTokens,
            maxTurnTokens: config.AgentMaxTurnTokens,
            hooks: agentHooks);

        return new BoukenshaSession(context, registry, AgentFactory, logger, mcpClients, backendName, model, knowledgeStore);
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 7: End-to-end verification

**Files:** none (verification only).

- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — expect all tests pass (19 from the port + this pass's new tests).
- [ ] Run: `dotnet clean week2_capable/dotnet/Boukensha.slnx && dotnet build week2_capable/dotnet/Boukensha.slnx` — expect 0 warnings, 0 errors.
- [ ] **Confirm with the user before proceeding**: this step makes a real, billed Anthropic API call and connects to the live MUD server, same as `dotnet_port_plan.md` Task 18. Since that was already confirmed once this session and the same `.boukensha` config/MUD server are being reused, a quick heads-up is enough rather than a full re-ask — but still pause and say so before running it.
- [ ] Reset the test character's connection state cleanly first if the previous session left it linkless (same issue hit during this plan's design phase): connect and send `quit` via a throwaway script before the real run, exactly as done when capturing the ground-truth fixtures.
- [ ] Run one live turn: `BOUKENSHA_DIR="<repo>/.boukensha" dotnet run --project week2_capable/dotnet/src/Boukensha.Console -- --no-tui` piping in a task like `"look around, then move in any open direction, then look again"`.
- [ ] Inspect `<repo>/.boukensha/knowledge.db` (via the `sqlite3` CLI or a throwaway script) and confirm: at least two rows in `rooms`, at least one `exits` row with `state='walked'` and a non-null `to_room_id`, and `location.current_room_id` pointing at the room the agent ended the turn in.
- [ ] Inspect the new session's JSONL log (`<repo>/.boukensha/sessions/*.jsonl`) and confirm a `[here]` block appears as a `user`-role message inside a `prompt` event on the *second* iteration onward (not the first, since nothing is known yet before any tool call completes).
- [ ] Update this plan's checkboxes and `docs/plans/week_2/basic_memory.md`'s status line to reflect completion.
- [ ] Commit (final).
