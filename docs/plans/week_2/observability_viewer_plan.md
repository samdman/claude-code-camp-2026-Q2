# Observability Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent.

**Goal:** Build `Boukensha.Observability`, a Razor Pages app implementing `docs/plans/week_2/observability_viewer.md`: session list/detail/telnet views, a knowledge browser + change journal, and a live cockpit page.

**Architecture:** Three new reader classes (`SessionLogReader`/`TelnetLogReader`/`ChangeLogReader`) parse the JSONL formats `Logger`/`TelnetLog`/`KnowledgeStore` already write; `KnowledgeStore` gains two read methods; a new ASP.NET Core Razor Pages project wires it all together and renders six pages plus one JSON polling endpoint.

**Tech Stack:** ASP.NET Core Razor Pages (`Microsoft.NET.Sdk.Web`, ships in the shared framework — no new NuGet dependency). Plain server-rendered HTML, one small inline `<script>` for `/Live`'s polling. No client-side framework.

## Decisions logged during execution

- **Telnet page route is `/Sessions/Telnet/{id}`, not `/Sessions/{id}/Telnet`** as the design doc's illustrative URL suggested. Razor Pages' file/folder-based routing maps `Pages/Sessions/Telnet.cshtml` with `@page "{id}"` to `/Sessions/Telnet/{id}` — getting the literal `/Sessions/{id}/Telnet` shape would require custom route templates for no real benefit at this scale. Functionally identical, just a URL-shape adjustment to fit the framework's convention.

## Global Constraints

- Read-only: the viewer must never call `KnowledgeStore`'s mutating methods (`UpsertRoom`/`RecordExits`/`LinkExit`/`SetCurrentRoom`/`ClearCurrentRoom`) — only `GetCurrentRoom`/`ListRooms`/`ListExits`.
- `KnowledgeStore` is registered **Scoped** in DI (fresh `SqliteConnection` per request) — never Singleton, since ASP.NET Core serves requests concurrently and `SqliteConnection` isn't safe across threads.
- Reader-class tests use real fixture lines captured from this session's own live-verification runs (session log, `telnet.jsonl`, `knowledge_changes.jsonl`), not synthetic examples.
- No page performs a write of any kind — this is a pure observability tool.

---

## Task 1: Scaffold `Boukensha.Observability`

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/` (via `dotnet new webapp`)
- Modify: `week2_capable/dotnet/Boukensha.slnx`

- [ ] Run:
```bash
dotnet new webapp -n Boukensha.Observability -o week2_capable/dotnet/src/Boukensha.Observability -f net10.0
dotnet sln week2_capable/dotnet/Boukensha.slnx add week2_capable/dotnet/src/Boukensha.Observability/Boukensha.Observability.csproj
dotnet add week2_capable/dotnet/src/Boukensha.Observability/Boukensha.Observability.csproj reference week2_capable/dotnet/src/Boukensha.Core/Boukensha.Core.csproj
```
- [ ] Delete the template's `Pages/Privacy.cshtml` and `Pages/Privacy.cshtml.cs` (unrelated boilerplate).
- [ ] Replace `Pages/Shared/_Layout.cshtml`'s contents with a minimal shared layout (nav bar + basic table/badge styling, no client framework):
```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>@ViewData["Title"] - Boukensha Observability</title>
    <style>
        body { font-family: system-ui, sans-serif; margin: 0; background: #111; color: #ddd; }
        nav { background: #1a1a1a; padding: 0.75rem 1rem; border-bottom: 1px solid #333; }
        nav a { color: #9cf; margin-right: 1.5rem; text-decoration: none; }
        nav a:hover { text-decoration: underline; }
        main { padding: 1rem 1.5rem; }
        table { border-collapse: collapse; width: 100%; margin-bottom: 1rem; }
        th, td { border: 1px solid #333; padding: 0.4rem 0.6rem; text-align: left; font-size: 0.9rem; }
        th { background: #1a1a1a; }
        a { color: #9cf; }
        pre, code, .mono { font-family: ui-monospace, Consolas, monospace; }
        pre { background: #1a1a1a; padding: 0.75rem; overflow-x: auto; white-space: pre-wrap; word-break: break-word; }
        .badge { display: inline-block; padding: 0.1rem 0.5rem; border-radius: 3px; font-size: 0.8rem; margin-right: 0.3rem; }
        .badge-send { background: #264; }
        .badge-recv { background: #246; }
        .badge-slow { background: #622; }
        .badge-ok { background: #262; }
        .badge-error { background: #622; }
        details { margin: 0.4rem 0; }
        summary { cursor: pointer; color: #9cf; }
    </style>
</head>
<body>
    <nav>
        <a href="/">Sessions</a>
        <a href="/Knowledge">Knowledge</a>
        <a href="/Knowledge/Changes">Changes</a>
        <a href="/Live">Live</a>
    </nav>
    <main>
        @RenderBody()
    </main>
</body>
</html>
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 2: `SessionLogReader` (tested against real fixtures)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Data/SessionLogReader.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Observability/SessionLogReaderTests.cs`

Note: since `Boukensha.Observability` is a `Microsoft.NET.Sdk.Web` project (not a library the test project already references), add its test coverage to the existing `Boukensha.Core.Tests` project by also referencing `Boukensha.Observability` from it — simpler than standing up a second test project for one reader class's worth of logic. Add the reference first:
```bash
dotnet add week2_capable/dotnet/tests/Boukensha.Core.Tests/Boukensha.Core.Tests.csproj reference week2_capable/dotnet/src/Boukensha.Observability/Boukensha.Observability.csproj
```

**Produces:** `SessionSummary(string SessionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Task, string? Provider, string? Model, int TurnCount, long TotalInputTokens, long TotalOutputTokens, double TotalCostUsd, string FilePath)`; `SessionEvent(string Phase, DateTimeOffset At, JsonObject Raw)`; `SessionLogReader.ListSessions(string sessionsDir) -> IReadOnlyList<SessionSummary>`; `SessionLogReader.ReadEvents(string filePath) -> IReadOnlyList<SessionEvent>`.

- [ ] Write the failing tests, `SessionLogReaderTests.cs`, using real fixture lines captured from `.boukensha/sessions/20260809T201708Z-1bf1dc93.jsonl`:
```csharp
using System.Text.Json.Nodes;
using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class SessionLogReaderTests
{
    private const string SessionStartLine =
        """{"phase":"session_start","task":"player","provider":"anthropic","model":"claude-haiku-4-5","context_window":200000,"max_turn_tokens":60000,"system":"You are Boukensha.","session_id":"20260809T201708Z-1bf1dc93","at":"2026-08-10T08:17:08+12:00"}""";

    private const string TurnLine =
        """{"phase":"turn","n":1,"session_id":"20260809T201708Z-1bf1dc93","at":"2026-08-10T08:17:12+12:00"}""";

    private const string ResponseLine =
        """{"phase":"response","text":"done","usage":{"input_tokens":4488,"output_tokens":106},"stop_reason":"end_turn","task":"player","provider":"anthropic","cost_usd":0.005018,"duration_ms":2270,"session_id":"20260809T201708Z-1bf1dc93","at":"2026-08-10T08:17:23+12:00"}""";

    private static string WriteFixture(params string[] lines)
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_session_reader_test").FullName;
        var path = Path.Combine(dir, "20260809T201708Z-1bf1dc93.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ListSessions_SummarizesTaskProviderModelTurnCountAndTokens()
    {
        var path = WriteFixture(SessionStartLine, TurnLine, ResponseLine);
        var sessionsDir = Path.GetDirectoryName(path)!;

        var summaries = new SessionLogReader().ListSessions(sessionsDir);

        var summary = Assert.Single(summaries);
        Assert.Equal("20260809T201708Z-1bf1dc93", summary.SessionId);
        Assert.Equal("player", summary.Task);
        Assert.Equal("anthropic", summary.Provider);
        Assert.Equal("claude-haiku-4-5", summary.Model);
        Assert.Equal(1, summary.TurnCount);
        Assert.Equal(4488, summary.TotalInputTokens);
        Assert.Equal(106, summary.TotalOutputTokens);
        Assert.Equal(0.005018, summary.TotalCostUsd, 6);
    }

    [Fact]
    public void ListSessions_NewestFirst()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_session_reader_test").FullName;
        File.WriteAllLines(Path.Combine(dir, "a.jsonl"),
            [SessionStartLine.Replace("20260809T201708Z-1bf1dc93", "session-a").Replace("2026-08-10T08:17:08+12:00", "2026-08-10T08:00:00+12:00")]);
        File.WriteAllLines(Path.Combine(dir, "b.jsonl"),
            [SessionStartLine.Replace("20260809T201708Z-1bf1dc93", "session-b").Replace("2026-08-10T08:17:08+12:00", "2026-08-10T09:00:00+12:00")]);

        var summaries = new SessionLogReader().ListSessions(dir);

        Assert.Equal("session-b", summaries[0].SessionId);
        Assert.Equal("session-a", summaries[1].SessionId);
    }

    [Fact]
    public void ListSessions_EmptyDirectory_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_session_reader_test").FullName;
        Assert.Empty(new SessionLogReader().ListSessions(dir));
    }

    [Fact]
    public void ReadEvents_ParsesEveryLineWithPhaseAndTimestamp()
    {
        var path = WriteFixture(SessionStartLine, TurnLine, ResponseLine);

        var events = new SessionLogReader().ReadEvents(path);

        Assert.Equal(3, events.Count);
        Assert.Equal("session_start", events[0].Phase);
        Assert.Equal("turn", events[1].Phase);
        Assert.Equal("response", events[2].Phase);
        Assert.Equal("player", events[0].Raw["task"]!.GetValue<string>());
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter SessionLogReaderTests` — expect build failure (`SessionLogReader` doesn't exist yet).
- [ ] Write `week2_capable/dotnet/src/Boukensha.Observability/Data/SessionLogReader.cs`:
```csharp
using System.Text.Json.Nodes;

namespace Boukensha.Observability;

public sealed record SessionSummary(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Task,
    string? Provider,
    string? Model,
    int TurnCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    double TotalCostUsd,
    string FilePath);

public sealed record SessionEvent(string Phase, DateTimeOffset At, JsonObject Raw);

public sealed class SessionLogReader
{
    public IReadOnlyList<SessionSummary> ListSessions(string sessionsDir)
    {
        if (!Directory.Exists(sessionsDir)) return [];

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.jsonl"))
        {
            var summary = Summarize(file);
            if (summary is not null) summaries.Add(summary);
        }
        return summaries.OrderByDescending(s => s.StartedAt).ToList();
    }

    public IReadOnlyList<SessionEvent> ReadEvents(string filePath)
    {
        var events = new List<SessionEvent>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;
            var phase = obj["phase"]?.GetValue<string>() ?? "unknown";
            events.Add(new SessionEvent(phase, ParseAt(obj), obj));
        }
        return events;
    }

    private static SessionSummary? Summarize(string filePath)
    {
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        string? sessionId = null, task = null, provider = null, model = null;
        var turnCount = 0;
        long totalInput = 0, totalOutput = 0;
        double totalCost = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;

            var at = ParseAt(obj);
            startedAt ??= at;
            endedAt = at;

            switch (obj["phase"]?.GetValue<string>())
            {
                case "session_start":
                    sessionId = obj["session_id"]?.GetValue<string>();
                    task = obj["task"]?.GetValue<string>();
                    provider = obj["provider"]?.GetValue<string>();
                    model = obj["model"]?.GetValue<string>();
                    break;
                case "turn":
                    turnCount++;
                    break;
                case "response":
                    if (obj["usage"] is JsonObject usage)
                    {
                        totalInput += usage["input_tokens"]?.GetValue<long>() ?? 0;
                        totalOutput += usage["output_tokens"]?.GetValue<long>() ?? 0;
                    }
                    totalCost += obj["cost_usd"]?.GetValue<double>() ?? 0.0;
                    break;
            }
        }

        if (sessionId is null || startedAt is null) return null;
        return new SessionSummary(sessionId, startedAt.Value, endedAt, task, provider, model, turnCount, totalInput, totalOutput, totalCost, filePath);
    }

    private static DateTimeOffset ParseAt(JsonObject obj) =>
        obj["at"]?.GetValue<string>() is { } at && DateTimeOffset.TryParse(at, out var parsed) ? parsed : DateTimeOffset.MinValue;
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter SessionLogReaderTests` — expect all pass.
- [ ] Commit.

---

## Task 3: `TelnetLogReader` (tested against real fixtures)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Data/TelnetLogReader.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Observability/TelnetLogReaderTests.cs`

**Produces:** `TelnetEntry(DateTimeOffset At, string Direction, string Text)`; `TelnetLogReader.ReadEntries(string filePath) -> IReadOnlyList<TelnetEntry>`.

- [ ] Write the failing tests, using real fixture lines captured from `.boukensha/telnet.jsonl`:
```csharp
using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class TelnetLogReaderTests
{
    private const string RecvLine =
        """{"at":"2026-08-10T08:17:09.844+12:00","direction":"recv","text":"\r\nAttempting to Detect Client, Please Wait...\r\n"}""";

    private const string SendLine =
        """{"at":"2026-08-10T08:17:11.245+12:00","direction":"send","text":"dummy"}""";

    [Fact]
    public void ReadEntries_ParsesDirectionTextAndTimestamp()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("boukensha_telnet_reader_test").FullName, "telnet.jsonl");
        File.WriteAllLines(path, [RecvLine, SendLine]);

        var entries = new TelnetLogReader().ReadEntries(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("recv", entries[0].Direction);
        Assert.Contains("Attempting to Detect Client", entries[0].Text);
        Assert.Equal("send", entries[1].Direction);
        Assert.Equal("dummy", entries[1].Text);
    }

    [Fact]
    public void ReadEntries_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new TelnetLogReader().ReadEntries(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl")));
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter TelnetLogReaderTests` — expect build failure.
- [ ] Write `week2_capable/dotnet/src/Boukensha.Observability/Data/TelnetLogReader.cs`:
```csharp
using System.Text.Json.Nodes;

namespace Boukensha.Observability;

public sealed record TelnetEntry(DateTimeOffset At, string Direction, string Text);

public sealed class TelnetLogReader
{
    public IReadOnlyList<TelnetEntry> ReadEntries(string filePath)
    {
        if (!File.Exists(filePath)) return [];

        var entries = new List<TelnetEntry>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;

            var at = obj["at"]?.GetValue<string>() is { } atText && DateTimeOffset.TryParse(atText, out var parsed) ? parsed : DateTimeOffset.MinValue;
            entries.Add(new TelnetEntry(at, obj["direction"]?.GetValue<string>() ?? "unknown", obj["text"]?.GetValue<string>() ?? string.Empty));
        }
        return entries;
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter TelnetLogReaderTests` — expect all pass.
- [ ] Commit.

---

## Task 4: `ChangeLogReader` (tested against real fixtures)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Data/ChangeLogReader.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Observability/ChangeLogReaderTests.cs`

**Produces:** `ChangeEntry(DateTimeOffset At, string? SessionId, string Kind, JsonNode? Before, JsonNode? After)`; `ChangeLogReader.ReadEntries(string filePath) -> IReadOnlyList<ChangeEntry>`.

- [ ] Write the failing tests, using a real fixture line captured from `.boukensha/knowledge_changes.jsonl`:
```csharp
using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class ChangeLogReaderTests
{
    private const string RoomUpsertedLine =
        """{"at":"2026-08-09T20:17:16.2560555+00:00","session_id":"20260809T201708Z-1bf1dc93","kind":"room_upserted","before":{"id":2,"visit_count":1},"after":{"id":2,"name":"The Grand Sewer","description":"...","visit_count":2}}""";

    [Fact]
    public void ReadEntries_ParsesKindSessionIdBeforeAndAfter()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("boukensha_change_reader_test").FullName, "knowledge_changes.jsonl");
        File.WriteAllLines(path, [RoomUpsertedLine]);

        var entries = new ChangeLogReader().ReadEntries(path);

        var entry = Assert.Single(entries);
        Assert.Equal("room_upserted", entry.Kind);
        Assert.Equal("20260809T201708Z-1bf1dc93", entry.SessionId);
        Assert.Equal(1, entry.Before!["visit_count"]!.GetValue<int>());
        Assert.Equal(2, entry.After!["visit_count"]!.GetValue<int>());
    }

    [Fact]
    public void ReadEntries_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new ChangeLogReader().ReadEntries(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl")));
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ChangeLogReaderTests` — expect build failure.
- [ ] Write `week2_capable/dotnet/src/Boukensha.Observability/Data/ChangeLogReader.cs`:
```csharp
using System.Text.Json.Nodes;

namespace Boukensha.Observability;

public sealed record ChangeEntry(DateTimeOffset At, string? SessionId, string Kind, JsonNode? Before, JsonNode? After);

public sealed class ChangeLogReader
{
    public IReadOnlyList<ChangeEntry> ReadEntries(string filePath)
    {
        if (!File.Exists(filePath)) return [];

        var entries = new List<ChangeEntry>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;

            var at = obj["at"]?.GetValue<string>() is { } atText && DateTimeOffset.TryParse(atText, out var parsed) ? parsed : DateTimeOffset.MinValue;
            entries.Add(new ChangeEntry(at, obj["session_id"]?.GetValue<string>(), obj["kind"]?.GetValue<string>() ?? "unknown", obj["before"], obj["after"]));
        }
        return entries;
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ChangeLogReaderTests` — expect all pass.
- [ ] Commit.

---

## Task 5: `KnowledgeStore.ListRooms`/`ListExits` (tested)

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/KnowledgeStore.cs`
- Modify: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/KnowledgeStoreTests.cs`

**Produces:** `ExitRecord(string Direction, string State, string? ToRoomName, string? Hint)`; `KnowledgeStore.ListRooms() -> IReadOnlyList<RoomRecord>`; `KnowledgeStore.ListExits(int roomId) -> IReadOnlyList<ExitRecord>`.

- [ ] Append the failing tests to `KnowledgeStoreTests.cs`:
```csharp
    [Fact]
    public void ListRooms_ReturnsAllRoomsMostRecentlySeenFirst()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        Thread.Sleep(5);
        var b = store.UpsertRoom("B", "b");

        var rooms = store.ListRooms();

        Assert.Equal(2, rooms.Count);
        Assert.Equal(b.Id, rooms[0].Id);
        Assert.Equal(a.Id, rooms[1].Id);
    }

    [Fact]
    public void ListExits_IncludesWalkedDestinationNameAndFrontierHint()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("A", "a");
        var dest = store.UpsertRoom("B", "b");
        store.LinkExit(start.Id, "south", dest.Id);
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["north"] = "Somewhere" });

        var exits = store.ListExits(start.Id);

        var south = Assert.Single(exits, e => e.Direction == "south");
        Assert.Equal("walked", south.State);
        Assert.Equal("B", south.ToRoomName);

        var north = Assert.Single(exits, e => e.Direction == "north");
        Assert.Equal("frontier", north.State);
        Assert.Equal("Somewhere", north.Hint);
    }
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter "ListRooms|ListExits"` — expect build failure.
- [ ] Add to `KnowledgeStore.cs`: the new record (place near `RoomRecord` at the top) and two methods (place after `GetCurrentRoom`):
```csharp
public sealed record ExitRecord(string Direction, string State, string? ToRoomName, string? Hint);
```
```csharp
    public IReadOnlyList<RoomRecord> ListRooms()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, fingerprint, name, description, visit_count FROM rooms ORDER BY last_seen_at DESC;";
        using var reader = cmd.ExecuteReader();
        var rooms = new List<RoomRecord>();
        while (reader.Read())
        {
            rooms.Add(new RoomRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        return rooms;
    }

    public IReadOnlyList<ExitRecord> ListExits(int roomId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.direction, e.state, dest.name, e.to_room_name_hint
            FROM exits e LEFT JOIN rooms dest ON dest.id = e.to_room_id
            WHERE e.room_id = $roomId ORDER BY e.direction;
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        using var reader = cmd.ExecuteReader();
        var exits = new List<ExitRecord>();
        while (reader.Read())
        {
            exits.Add(new ExitRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return exits;
    }
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter "ListRooms|ListExits"` — expect all pass.
- [ ] Commit.

---

## Task 6: `Program.cs` DI wiring + `/api/live` endpoint

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Observability/Program.cs`
- Create: `week2_capable/dotnet/src/Boukensha.Observability/ObservabilityPaths.cs`

**Consumes:** `SessionLogReader`/`TelnetLogReader`/`ChangeLogReader` (Tasks 2–4), `KnowledgeStore.ListExits`/`GetCurrentRoom` (Task 5, `Boukensha.Core.Knowledge`).

- [ ] Write `week2_capable/dotnet/src/Boukensha.Observability/ObservabilityPaths.cs`:
```csharp
namespace Boukensha.Observability;

public sealed record ObservabilityPaths(string SessionsDir, string KnowledgeDbPath, string ChangeLogPath, string TelnetLogPath);
```
- [ ] Replace `week2_capable/dotnet/src/Boukensha.Observability/Program.cs` in full:
```csharp
using Boukensha.Core;
using Boukensha.Core.Knowledge;
using Boukensha.Observability;

var builder = WebApplication.CreateBuilder(args);

var config = new Config();
var paths = new ObservabilityPaths(
    Path.Combine(config.Dir, "sessions"),
    Path.Combine(config.Dir, "knowledge.db"),
    Path.Combine(config.Dir, "knowledge_changes.jsonl"),
    Path.Combine(config.Dir, "telnet.jsonl"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<SessionLogReader>();
builder.Services.AddSingleton<TelnetLogReader>();
builder.Services.AddSingleton<ChangeLogReader>();
builder.Services.AddScoped(_ => new KnowledgeStore(paths.KnowledgeDbPath));
builder.Services.AddRazorPages();

var app = builder.Build();
app.UseStaticFiles();
app.MapRazorPages();

app.MapGet("/api/live", (SessionLogReader sessionReader, KnowledgeStore knowledge, ObservabilityPaths obsPaths) =>
{
    var latestSession = sessionReader.ListSessions(obsPaths.SessionsDir).FirstOrDefault();
    var recentEvents = latestSession is not null
        ? sessionReader.ReadEvents(latestSession.FilePath).TakeLast(10).ToList()
        : [];
    var currentRoom = knowledge.GetCurrentRoom();
    var exits = currentRoom is not null ? knowledge.ListExits(currentRoom.Id) : [];

    return Results.Json(new
    {
        session = latestSession,
        recentEvents = recentEvents.Select(e => new { e.Phase, at = e.At }),
        currentRoom,
        exits,
    });
});

app.Run();
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Smoke check (not committed as a test): `dotnet run --project week2_capable/dotnet/src/Boukensha.Observability &`, then `curl http://localhost:<port>/api/live` (port from the `dotnet run` startup log) and confirm valid JSON comes back (`{"session":null,...}` is fine if no sessions exist yet at this point — Task 7+ pages aren't built, this only checks the endpoint itself responds), then stop the running process.
- [ ] Commit.

---

## Task 7: `/` (session list) and `/Sessions/{id}` (detail) pages

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Index.cshtml` and `.cshtml.cs`
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Sessions/Index.cshtml` and `.cshtml.cs`

**Consumes:** `SessionLogReader`, `ObservabilityPaths` (Tasks 2, 6).

- [ ] Replace `Pages/Index.cshtml.cs`:
```csharp
using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages;

public class IndexModel(SessionLogReader reader, ObservabilityPaths paths) : PageModel
{
    public IReadOnlyList<SessionSummary> Sessions { get; private set; } = [];

    public void OnGet() => Sessions = reader.ListSessions(paths.SessionsDir);
}
```
- [ ] Replace `Pages/Index.cshtml`:
```html
@page
@model IndexModel
@{ ViewData["Title"] = "Sessions"; }

<h1>Sessions</h1>
<table>
    <thead>
        <tr><th>Started</th><th>Duration</th><th>Task</th><th>Provider/Model</th><th>Turns</th><th>Tokens (in/out)</th><th>Cost</th></tr>
    </thead>
    <tbody>
    @foreach (var s in Model.Sessions)
    {
        var duration = s.EndedAt.HasValue ? (s.EndedAt.Value - s.StartedAt) : (TimeSpan?)null;
        <tr>
            <td><a href="/Sessions/@s.SessionId">@s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")</a></td>
            <td>@(duration.HasValue ? $"{duration.Value.TotalSeconds:F1}s" : "-")</td>
            <td>@s.Task</td>
            <td>@s.Provider / @s.Model</td>
            <td>@s.TurnCount</td>
            <td>@s.TotalInputTokens / @s.TotalOutputTokens</td>
            <td>$@s.TotalCostUsd.ToString("F4")</td>
        </tr>
    }
    </tbody>
</table>
@if (Model.Sessions.Count == 0)
{
    <p>No sessions found.</p>
}
```
- [ ] Write `Pages/Sessions/Index.cshtml.cs`:
```csharp
using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Sessions;

public class IndexModel(SessionLogReader reader, ObservabilityPaths paths) : PageModel
{
    public string SessionId { get; private set; } = string.Empty;
    public IReadOnlyList<SessionEvent> Events { get; private set; } = [];
    public IReadOnlyList<SessionEvent> SlowestFirst => Events
        .Where(e => e.Raw["duration_ms"] is not null)
        .OrderByDescending(e => e.Raw["duration_ms"]!.GetValue<int>())
        .ToList();

    public IActionResult OnGet(string id)
    {
        var filePath = Path.Combine(paths.SessionsDir, $"{id}.jsonl");
        if (!System.IO.File.Exists(filePath)) return NotFound();

        SessionId = id;
        Events = reader.ReadEvents(filePath);
        return Page();
    }
}
```
- [ ] Write `Pages/Sessions/Index.cshtml`:
```html
@page "{id}"
@using System.Text.Json
@model Boukensha.Observability.Pages.Sessions.IndexModel
@{ ViewData["Title"] = "Session " + Model.SessionId; }

<h1>Session @Model.SessionId</h1>
<p><a href="/Sessions/Telnet/@Model.SessionId">View raw telnet traffic for this session</a></p>

<details>
    <summary>Slowest operations (bottleneck view)</summary>
    <table>
        <thead><tr><th>Phase</th><th>Duration (ms)</th><th>At</th></tr></thead>
        <tbody>
        @foreach (var e in Model.SlowestFirst.Take(10))
        {
            <tr><td>@e.Phase</td><td>@e.Raw["duration_ms"]</td><td>@e.At.ToString("HH:mm:ss.fff")</td></tr>
        }
        </tbody>
    </table>
</details>

<h2>Transcript</h2>
@foreach (var e in Model.Events)
{
    <div style="border-bottom:1px solid #333;padding:0.5rem 0;">
        <span class="badge @(e.Phase == "tool_result" && e.Raw["ok"]?.GetValue<bool>() == false ? "badge-error" : "badge-ok")">@e.Phase</span>
        <span class="mono">@e.At.ToString("HH:mm:ss.fff")</span>
        @if (e.Raw["duration_ms"] is not null)
        {
            <span class="mono">(@e.Raw["duration_ms"]ms)</span>
        }
        @if (e.Raw["task"] is not null)
        {
            <span class="mono">task=@e.Raw["task"]</span>
        }
        <details>
            <summary>details</summary>
            <pre>@e.Raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true })</pre>
        </details>
    </div>
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 8: `/Sessions/Telnet/{id}` page

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Sessions/Telnet.cshtml` and `.cshtml.cs`

**Consumes:** `SessionLogReader`, `TelnetLogReader`, `ObservabilityPaths` (Tasks 2, 3, 6).

- [ ] Write `Pages/Sessions/Telnet.cshtml.cs`:
```csharp
using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Sessions;

public class TelnetModel(SessionLogReader sessionReader, TelnetLogReader telnetReader, ObservabilityPaths paths) : PageModel
{
    public string SessionId { get; private set; } = string.Empty;
    public IReadOnlyList<TelnetEntry> Entries { get; private set; } = [];

    public IActionResult OnGet(string id)
    {
        var filePath = Path.Combine(paths.SessionsDir, $"{id}.jsonl");
        if (!System.IO.File.Exists(filePath)) return NotFound();

        SessionId = id;
        var events = sessionReader.ReadEvents(filePath);
        if (events.Count == 0)
        {
            Entries = [];
            return Page();
        }

        var start = events[0].At;
        var end = events[^1].At;
        Entries = telnetReader.ReadEntries(paths.TelnetLogPath)
            .Where(e => e.At >= start && e.At <= end)
            .ToList();
        return Page();
    }
}
```
- [ ] Write `Pages/Sessions/Telnet.cshtml`:
```html
@page "{id}"
@model Boukensha.Observability.Pages.Sessions.TelnetModel
@{ ViewData["Title"] = "Telnet — " + Model.SessionId; }

<h1>Raw telnet traffic — session @Model.SessionId</h1>
<p><a href="/Sessions/@Model.SessionId">Back to session transcript</a></p>
<p>Entries whose timestamp falls within this session's time window (@Model.Entries.Count found).</p>

@foreach (var e in Model.Entries)
{
    <div>
        <span class="badge badge-@e.Direction">@e.Direction</span>
        <span class="mono">@e.At.ToString("HH:mm:ss.fff")</span>
        <pre>@e.Text</pre>
    </div>
}
@if (Model.Entries.Count == 0)
{
    <p>No telnet entries found in this session's time window (is MUD_TELNET_LOG configured?).</p>
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 9: `/Knowledge` and `/Knowledge/Changes` pages

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Knowledge/Index.cshtml` and `.cshtml.cs`
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Knowledge/Changes.cshtml` and `.cshtml.cs`

**Consumes:** `KnowledgeStore.ListRooms`/`ListExits`/`GetCurrentRoom` (Task 5), `ChangeLogReader`/`ObservabilityPaths` (Tasks 4, 6).

- [ ] Write `Pages/Knowledge/Index.cshtml.cs`:
```csharp
using Boukensha.Core.Knowledge;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Knowledge;

public class IndexModel(KnowledgeStore store) : PageModel
{
    public IReadOnlyList<RoomRecord> Rooms { get; private set; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> ExitsByRoom { get; private set; } = new Dictionary<int, IReadOnlyList<ExitRecord>>();
    public int? CurrentRoomId { get; private set; }

    public void OnGet()
    {
        Rooms = store.ListRooms();
        CurrentRoomId = store.GetCurrentRoom()?.Id;
        ExitsByRoom = Rooms.ToDictionary(r => r.Id, r => store.ListExits(r.Id));
    }
}
```
- [ ] Write `Pages/Knowledge/Index.cshtml`:
```html
@page
@model Boukensha.Observability.Pages.Knowledge.IndexModel
@{ ViewData["Title"] = "Knowledge"; }

<h1>Known rooms</h1>
<table>
    <thead><tr><th>Room</th><th>Visits</th><th>Exits</th></tr></thead>
    <tbody>
    @foreach (var room in Model.Rooms)
    {
        var exitParts = Model.ExitsByRoom[room.Id].Select(e =>
            e.State == "walked" ? $"{e.Direction[0]}→{e.ToRoomName} ✓" : $"{e.Direction[0]}→{e.Hint ?? "?"}");
        <tr>
            <td>@room.Name @(room.Id == Model.CurrentRoomId ? "(current)" : "")</td>
            <td>@room.VisitCount</td>
            <td class="mono">@string.Join(" | ", exitParts)</td>
        </tr>
    }
    </tbody>
</table>
@if (Model.Rooms.Count == 0)
{
    <p>No rooms known yet.</p>
}
```
- [ ] Write `Pages/Knowledge/Changes.cshtml.cs`:
```csharp
using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Knowledge;

public class ChangesModel(ChangeLogReader reader, ObservabilityPaths paths) : PageModel
{
    public IReadOnlyList<ChangeEntry> Changes { get; private set; } = [];

    public void OnGet() => Changes = reader.ReadEntries(paths.ChangeLogPath).OrderByDescending(c => c.At).ToList();
}
```
- [ ] Write `Pages/Knowledge/Changes.cshtml`:
```html
@page
@using System.Text.Json
@model Boukensha.Observability.Pages.Knowledge.ChangesModel
@{ ViewData["Title"] = "Knowledge Changes"; }

<h1>Knowledge changes over time</h1>
<label>Filter: <select id="kindFilter" onchange="filterRows()">
    <option value="">(all)</option>
    @foreach (var kind in Model.Changes.Select(c => c.Kind).Distinct())
    {
        <option value="@kind">@kind</option>
    }
</select></label>

<table id="changesTable">
    <thead><tr><th>At</th><th>Session</th><th>Kind</th><th>Before</th><th>After</th></tr></thead>
    <tbody>
    @foreach (var c in Model.Changes)
    {
        <tr data-kind="@c.Kind">
            <td class="mono">@c.At.ToString("yyyy-MM-dd HH:mm:ss.fff")</td>
            <td class="mono">@c.SessionId</td>
            <td>@c.Kind</td>
            <td class="mono">@c.Before?.ToJsonString()</td>
            <td class="mono">@c.After?.ToJsonString()</td>
        </tr>
    }
    </tbody>
</table>

<script>
    function filterRows() {
        var kind = document.getElementById('kindFilter').value;
        document.querySelectorAll('#changesTable tbody tr').forEach(function (row) {
            row.style.display = (!kind || row.dataset.kind === kind) ? '' : 'none';
        });
    }
</script>
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 10: `/Live` cockpit page

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Live.cshtml` and `.cshtml.cs`

**Consumes:** `/api/live` endpoint (Task 6).

- [ ] Write `Pages/Live.cshtml.cs`:
```csharp
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages;

public class LiveModel : PageModel
{
    public void OnGet() { }
}
```
- [ ] Write `Pages/Live.cshtml`:
```html
@page
@{ ViewData["Title"] = "Live"; }

<h1>Live cockpit</h1>
<div id="session">Loading...</div>
<h2>Current room</h2>
<div id="room">Loading...</div>
<h2>Recent activity</h2>
<div id="activity">Loading...</div>

<script>
    async function refresh() {
        const res = await fetch('/api/live');
        const data = await res.json();

        document.getElementById('session').textContent = data.session
            ? `${data.session.sessionId} — task=${data.session.task} — turns=${data.session.turnCount}`
            : 'No active session';

        document.getElementById('room').textContent = data.currentRoom
            ? `${data.currentRoom.name} (visit ${data.currentRoom.visitCount}) — exits: ` +
              data.exits.map(e => `${e.direction[0]}→${e.state === 'walked' ? (e.toRoomName + ' ✓') : (e.hint || '?')}`).join(' | ')
            : 'Location unknown';

        document.getElementById('activity').innerHTML = data.recentEvents
            .map(e => `<div>${e.at} — ${e.phase}</div>`)
            .join('');
    }

    refresh();
    setInterval(refresh, 3000);
</script>
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 11: End-to-end verification

**Files:** none (verification only).

- [x] Ran: `dotnet test week2_capable/dotnet/Boukensha.slnx` — all 63 tests pass (53 from before this spec + 10 new: 4 `SessionLogReaderTests`, 2 `TelnetLogReaderTests`, 2 `ChangeLogReaderTests`, 2 `KnowledgeStore` list-method tests).
- [x] Ran: `dotnet clean week2_capable/dotnet/Boukensha.slnx && dotnet build week2_capable/dotnet/Boukensha.slnx` — 0 errors, 8 warnings (up from 6 — the new `Boukensha.Observability` project adds its own copy of the already-accepted NU1903 SQLite advisory; confirmed no other/new warnings).
- [x] No live Anthropic call needed — the viewer only reads existing `.boukensha/` data from the memory and instrumentation sub-projects' prior live runs, so this verification was free.
- [x] Started the app (`BOUKENSHA_DIR=".../.boukensha" ASPNETCORE_URLS="http://localhost:5288" dotnet run --project week2_capable/dotnet/src/Boukensha.Observability --no-launch-profile`) and `curl`ed every route — all returned real data, not placeholders or errors:
  - `/` — real session id `20260809T201708Z-1bf1dc93` present.
  - `/Sessions/{id}` — `session_start`, `response`, and `duration_ms` all present.
  - `/Sessions/Telnet/{id}` — both `badge-send` and `badge-recv` present.
  - `/Knowledge` — both "The Sewer Pipe" and "The Grand Sewer" present.
  - `/Knowledge/Changes` — all 5 change kinds present (`room_upserted`, `exit_recorded`, `exit_linked`, `location_changed`, `location_cleared`).
  - `/Live` — page loads; `/api/live` returns valid JSON with the real session summary.
- [x] Discovered and fixed a background-process cleanup issue: a `dotnet run` process started via Bash `&`/`kill` left the actual app process (`Boukensha.Observability.exe`, a separate PID from the `dotnet run` wrapper) still running and file-locked, breaking a later `dotnet build`. Resolved via `Stop-Process` (PowerShell) targeting the real process by name — noted for any future background `dotnet run` in this environment.
- [x] Updated this plan's checkboxes and `docs/plans/week_2/observability_viewer.md`'s status line to reflect completion.
- [ ] Commit (final) — single commit for all of Tasks 1–11, matching the batching preference set mid-way through the instrumentation plan.
