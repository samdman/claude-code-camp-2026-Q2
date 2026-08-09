# Journey Agent + Map Visualization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent.
>
> Spec: `docs/plans/week_2/specs/journey_agent.md`. Note the docs layout convention as of this plan: specs live in `docs/plans/week_2/specs/`, plans in `docs/plans/week_2/plans/` (moved from the earlier flat `docs/plans/week_2/` layout — see that spec's own file for the full design).

**Goal:** Generalize `RoutePlanner` to arbitrary point-A-to-point-B routing, add a `JourneyReader` that reconstructs the visit trail from the existing CDC journal, and add a `/Knowledge/Map` page in `Boukensha.Observability` with a deterministic graph layout plus a journey trail panel.

**Architecture:** `RoutePlanner.FindRoute` gains an optional `fromQuery` parameter (Core). `JourneyReader` wraps the existing `ChangeLogReader`, filtering to `location_changed` entries (Observability). `MapLayout` is a pure BFS-grid-layout function taking already-loaded room/exit data, no store dependency (Observability). A new page wires the store calls to both and renders static SVG.

**Tech Stack:** No new dependencies. Plain SVG markup in the `.cshtml`, no charting/graph library.

## Global Constraints

- `RoutePlanner.FindRoute`'s existing single-argument call sites (the `plan_route` tool registration in `BoukenshaHost`) must continue to compile and behave identically without modification — the new parameter is optional with a default preserving today's behavior.
- `MapLayout.Calculate` must be a pure function over plain data (`IReadOnlyList<RoomRecord>` + `IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>>`) — no `KnowledgeStore`/SQLite dependency, so `MapLayoutTests` needs no database at all.
- No pan/zoom, no room-agent work, no trail animation — all explicitly out of scope per the spec.

---

## Task 1: Generalize `RoutePlanner.FindRoute` (tested)

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Knowledge/RoutePlanner.cs`
- Modify: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Knowledge/RoutePlannerTests.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs` (expose the new parameter to the agent)

**Produces:** `RoutePlanner.FindRoute(string destinationQuery, string? fromQuery = null) -> RouteResult` (was `FindRoute(string destinationQuery)`).

- [ ] Append the failing tests to `RoutePlannerTests.cs`:
```csharp
    [Fact]
    public void FindRoute_WithExplicitFrom_PlansFromThatRoomInsteadOfCurrent()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        var b = store.UpsertRoom("B", "b");
        var c = store.UpsertRoom("C", "c");
        store.LinkExit(a.Id, "south", b.Id);
        store.LinkExit(b.Id, "east", c.Id);
        store.SetCurrentRoom(c.Id); // agent is at C, but asks to plan a route starting from A

        var result = new RoutePlanner(store).FindRoute("C", fromQuery: "A");

        Assert.True(result.Found);
        Assert.Equal(["south", "east"], result.Directions);
    }

    [Fact]
    public void FindRoute_ExplicitFromDoesNotResolve_ReturnsNotFound()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("A", fromQuery: "Nonexistent");

        Assert.False(result.Found);
        Assert.Contains("Nonexistent", result.Message);
    }

    [Fact]
    public void FindRoute_OmittingFrom_StillUsesCurrentRoom()
    {
        using var store = NewStore();
        var a = store.UpsertRoom("A", "a");
        var b = store.UpsertRoom("B", "b");
        store.LinkExit(a.Id, "south", b.Id);
        store.SetCurrentRoom(a.Id);

        var result = new RoutePlanner(store).FindRoute("B");

        Assert.True(result.Found);
        Assert.Equal(["south"], result.Directions);
    }
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoutePlannerTests` — expect a build failure (no overload accepting `fromQuery:` yet).
- [ ] Modify `RoutePlanner.cs`'s `FindRoute` method:
```csharp
    public RouteResult FindRoute(string destinationQuery, string? fromQuery = null)
    {
        RoomRecord? start;
        if (fromQuery is null)
        {
            start = store.GetCurrentRoom();
            if (start is null)
            {
                return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
            }
        }
        else
        {
            var rooms = store.ListRooms();
            start = rooms.FirstOrDefault(r => r.Name.Equals(fromQuery, StringComparison.OrdinalIgnoreCase))
                ?? rooms.FirstOrDefault(r => r.Name.Contains(fromQuery, StringComparison.OrdinalIgnoreCase));
            if (start is null)
            {
                return new RouteResult(false, null, [], $"No known room matching starting point '{fromQuery}'.");
            }
        }

        var allRooms = store.ListRooms();
        var destination = allRooms.FirstOrDefault(r => r.Name.Equals(destinationQuery, StringComparison.OrdinalIgnoreCase))
            ?? allRooms.FirstOrDefault(r => r.Name.Contains(destinationQuery, StringComparison.OrdinalIgnoreCase));

        if (destination is null)
        {
            return new RouteResult(false, null, [], $"No known room matching '{destinationQuery}'.{FrontierHint(start.Id)}");
        }

        if (destination.Id == start.Id)
        {
            return new RouteResult(true, destination.Name, [], $"You are already at '{destination.Name}'.");
        }

        var path = FindPath(start.Id, destination.Id);
        if (path is null)
        {
            return new RouteResult(false, destination.Name, [],
                $"'{destination.Name}' is known but no walked path from '{start.Name}' has been found yet.{FrontierHint(start.Id)}");
        }

        return new RouteResult(true, destination.Name, path,
            $"Route to '{destination.Name}': {string.Join(", ", path)} ({path.Count} step{(path.Count == 1 ? "" : "s")}).");
    }
```
  (`FindPath` and `FrontierHint` are unchanged — only `FindRoute`'s body changes, replacing its single `store.GetCurrentRoom()` read with the branch above and threading `start` through where `current` used to be.)
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RoutePlannerTests` — expect all pass (the 6 existing tests plus 3 new = 9).
- [ ] Expose the new parameter to the agent — modify the `plan_route` tool registration in `BoukenshaHost.cs`:
```csharp
        var routePlanner = new Knowledge.RoutePlanner(knowledgeStore);
        registry.Tool("plan_route",
            "Find a route between two previously-visited rooms by name. If 'from' is omitted, plans from your " +
            "current location. Returns step-by-step directions if a known walked path exists, or suggests unexplored exits if not.",
            new Dictionary<string, ToolParameter>
            {
                ["destination"] = new("string", "Name of the destination room"),
                ["from"] = new("string", "Name of the starting room (optional -- defaults to your current location)"),
            },
            args => Task.FromResult(routePlanner.FindRoute(
                args.GetValueOrDefault("destination") as string ?? "",
                args.GetValueOrDefault("from") as string).Message));
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Run: `dotnet test week2_capable/dotnet/Boukensha.slnx` — full suite passes.
- [ ] Commit.

---

## Task 2: `JourneyReader` (tested against real fixtures)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Data/JourneyReader.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Observability/JourneyReaderTests.cs`

**Consumes:** `ChangeLogReader` (existing).
**Produces:** `JourneyStep(DateTimeOffset At, string? SessionId, string? FromRoomName, string ToRoomName)`; `JourneyReader(ChangeLogReader changeLogReader)` with `ReadTrail(string changeLogPath, IReadOnlyList<RoomRecord> rooms) -> IReadOnlyList<JourneyStep>`.

- [ ] Write the failing tests, using real fixture lines captured from `.boukensha/knowledge_changes.jsonl`:
```csharp
using Boukensha.Core.Knowledge;
using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class JourneyReaderTests
{
    private const string FirstTransitionLine =
        """{"at":"2026-08-09T21:01:33.6771251+00:00","session_id":"20260809T210106Z-2674cc49","kind":"location_changed","before":null,"after":{"room_id":1}}""";

    private const string RealTransitionLine =
        """{"at":"2026-08-09T21:01:22.7835071+00:00","session_id":"20260809T210106Z-2674cc49","kind":"location_changed","before":{"room_id":2},"after":{"room_id":1}}""";

    private const string UnrelatedKindLine =
        """{"at":"2026-08-09T20:17:14.9622093+00:00","session_id":"20260809T201708Z-1bf1dc93","kind":"exit_recorded","before":{"room_id":2,"direction":"north","state":null},"after":{"room_id":2,"direction":"north","state":"frontier","hint":"The Grand Sewer"}}""";

    private static readonly IReadOnlyList<RoomRecord> Rooms =
    [
        new RoomRecord(1, "fp1", "The Sewer Pipe", "desc", 1),
        new RoomRecord(2, "fp2", "The Grand Sewer", "desc", 1),
    ];

    [Fact]
    public void ReadTrail_ResolvesRoomIdsToNamesAndIgnoresOtherKinds()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("boukensha_journey_reader_test").FullName, "knowledge_changes.jsonl");
        File.WriteAllLines(path, [UnrelatedKindLine, FirstTransitionLine, RealTransitionLine]);

        var trail = new JourneyReader(new ChangeLogReader()).ReadTrail(path, Rooms);

        Assert.Equal(2, trail.Count);
        Assert.Null(trail[0].FromRoomName);
        Assert.Equal("The Sewer Pipe", trail[0].ToRoomName);
        Assert.Equal("The Grand Sewer", trail[1].FromRoomName);
        Assert.Equal("The Sewer Pipe", trail[1].ToRoomName);
    }

    [Fact]
    public void ReadTrail_MissingFile_ReturnsEmpty()
    {
        var trail = new JourneyReader(new ChangeLogReader())
            .ReadTrail(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl"), Rooms);
        Assert.Empty(trail);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter JourneyReaderTests` — expect build failure (`JourneyReader` doesn't exist yet).
- [ ] Write `week2_capable/dotnet/src/Boukensha.Observability/Data/JourneyReader.cs`:
```csharp
using Boukensha.Core.Knowledge;

namespace Boukensha.Observability;

public sealed record JourneyStep(DateTimeOffset At, string? SessionId, string? FromRoomName, string ToRoomName);

public sealed class JourneyReader(ChangeLogReader changeLogReader)
{
    public IReadOnlyList<JourneyStep> ReadTrail(string changeLogPath, IReadOnlyList<RoomRecord> rooms)
    {
        var namesById = rooms.ToDictionary(r => r.Id, r => r.Name);

        var steps = new List<JourneyStep>();
        foreach (var entry in changeLogReader.ReadEntries(changeLogPath))
        {
            if (entry.Kind != "location_changed") continue;

            var fromId = entry.Before?["room_id"]?.GetValue<int>();
            var toId = entry.After?["room_id"]?.GetValue<int>();
            if (toId is null) continue;

            var fromName = fromId is not null && namesById.TryGetValue(fromId.Value, out var fn) ? fn : null;
            if (!namesById.TryGetValue(toId.Value, out var toName)) continue;

            steps.Add(new JourneyStep(entry.At, entry.SessionId, fromName, toName));
        }
        return steps;
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter JourneyReaderTests` — expect both pass.
- [ ] Commit.

---

## Task 3: `MapLayout` (pure BFS-grid algorithm, tested)

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Data/MapLayout.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Observability/MapLayoutTests.cs`

**Produces:** `RoomPosition(int RoomId, int X, int Y)`; `MapLayout.Calculate(IReadOnlyList<RoomRecord> rooms, IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> exitsByRoomId) -> IReadOnlyList<RoomPosition>`.

- [ ] Write the failing tests:
```csharp
using Boukensha.Core.Knowledge;
using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class MapLayoutTests
{
    private static RoomRecord Room(int id, string name, string firstSeenIso) =>
        new(id, $"fp{id}", name, "desc", 1) with { };

    [Fact]
    public void Calculate_PositionsRoomsByDirectionOffsetFromRoot()
    {
        var rooms = new List<RoomRecord>
        {
            new(1, "fp1", "Start", "d", 1),
            new(2, "fp2", "North", "d", 1),
            new(3, "fp3", "East", "d", 1),
        };
        var exits = new Dictionary<int, IReadOnlyList<ExitRecord>>
        {
            [1] = [new ExitRecord("north", "walked", "North", null, 2), new ExitRecord("east", "walked", "East", null, 3)],
            [2] = [],
            [3] = [],
        };

        var positions = MapLayout.Calculate(rooms, exits).ToDictionary(p => p.RoomId);

        Assert.Equal((0, 0), (positions[1].X, positions[1].Y));
        Assert.Equal((0, -1), (positions[2].X, positions[2].Y)); // north = y-1
        Assert.Equal((1, 0), (positions[3].X, positions[3].Y));  // east = x+1
    }

    [Fact]
    public void Calculate_CycleKeepsFirstAssignedPosition()
    {
        var rooms = new List<RoomRecord>
        {
            new(1, "fp1", "A", "d", 1),
            new(2, "fp2", "B", "d", 1),
            new(3, "fp3", "C", "d", 1),
        };
        // A -> B -> C -> A (a loop back to the start)
        var exits = new Dictionary<int, IReadOnlyList<ExitRecord>>
        {
            [1] = [new ExitRecord("north", "walked", "B", null, 2)],
            [2] = [new ExitRecord("east", "walked", "C", null, 3)],
            [3] = [new ExitRecord("south", "walked", "A", null, 1)],
        };

        var positions = MapLayout.Calculate(rooms, exits).ToDictionary(p => p.RoomId);

        Assert.Equal(3, positions.Count);
        Assert.Equal((0, 0), (positions[1].X, positions[1].Y));
    }

    [Fact]
    public void Calculate_DisconnectedRoom_GetsADifferentPositionNotOverlapping()
    {
        var rooms = new List<RoomRecord>
        {
            new(1, "fp1", "A", "d", 1),
            new(2, "fp2", "Isolated", "d", 1),
        };
        var exits = new Dictionary<int, IReadOnlyList<ExitRecord>>
        {
            [1] = [],
            [2] = [],
        };

        var positions = MapLayout.Calculate(rooms, exits).ToDictionary(p => p.RoomId);

        Assert.Equal(2, positions.Count);
        Assert.NotEqual((positions[1].X, positions[1].Y), (positions[2].X, positions[2].Y));
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter MapLayoutTests` — expect build failure (`MapLayout` doesn't exist yet).
- [ ] Write `week2_capable/dotnet/src/Boukensha.Observability/Data/MapLayout.cs`:
```csharp
using Boukensha.Core.Knowledge;

namespace Boukensha.Observability;

public sealed record RoomPosition(int RoomId, int X, int Y);

public static class MapLayout
{
    private static readonly IReadOnlyDictionary<string, (int Dx, int Dy)> DirectionOffsets = new Dictionary<string, (int, int)>
    {
        ["north"] = (0, -1),
        ["south"] = (0, 1),
        ["east"] = (1, 0),
        ["west"] = (-1, 0),
    };

    public static IReadOnlyList<RoomPosition> Calculate(
        IReadOnlyList<RoomRecord> rooms,
        IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> exitsByRoomId)
    {
        var positions = new Dictionary<int, (int X, int Y)>();
        var placedRoomIds = new HashSet<int>();
        var componentIndex = 0;

        // Process rooms by ascending id: SQLite's INTEGER PRIMARY KEY auto-increments,
        // so the lowest id is always the room created first -- the session's actual
        // starting room -- which becomes each component's layout root/origin. Every
        // room not yet reached by an earlier component's BFS starts a new component,
        // placed on its own row so components never overlap.
        foreach (var room in rooms.OrderBy(r => r.Id))
        {
            if (placedRoomIds.Contains(room.Id)) continue;

            var startY = componentIndex * 3;
            componentIndex++;
            BfsPlace(room.Id, 0, startY, exitsByRoomId, positions, placedRoomIds);
        }

        return positions.Select(kv => new RoomPosition(kv.Key, kv.Value.X, kv.Value.Y)).ToList();
    }

    private static void BfsPlace(
        int rootId,
        int rootX,
        int rootY,
        IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> exitsByRoomId,
        Dictionary<int, (int X, int Y)> positions,
        HashSet<int> placedRoomIds)
    {
        positions[rootId] = (rootX, rootY);
        placedRoomIds.Add(rootId);

        var queue = new Queue<int>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var roomId = queue.Dequeue();
            var (x, y) = positions[roomId];

            if (!exitsByRoomId.TryGetValue(roomId, out var exits)) continue;

            foreach (var exit in exits.Where(e => e.State == "walked" && e.ToRoomId is not null))
            {
                var nextId = exit.ToRoomId!.Value;
                if (placedRoomIds.Contains(nextId)) continue;

                var (dx, dy) = DirectionOffsets.GetValueOrDefault(exit.Direction, (0, 0));
                positions[nextId] = (x + dx, y + dy);
                placedRoomIds.Add(nextId);
                queue.Enqueue(nextId);
            }
        }
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter MapLayoutTests` — expect all 3 pass.
- [ ] Commit.

---

## Task 4: `/Knowledge/Map` page

**Files:**
- Create: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Knowledge/Map.cshtml` and `.cshtml.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Observability/Pages/Shared/_Layout.cshtml` (add a nav link)
- Modify: `week2_capable/dotnet/src/Boukensha.Observability/Program.cs` (register `JourneyReader` in DI)

**Consumes:** `KnowledgeStore.ListRooms`/`ListExits`/`GetCurrentRoom`, `MapLayout.Calculate`, `JourneyReader.ReadTrail` (Tasks 2–3).

- [ ] `JourneyReader` is a plain service class a `PageModel` will constructor-inject — ASP.NET Core's DI container requires every such service explicitly registered (unlike `PageModel` types themselves, which the Razor Pages framework instantiates specially). Add it alongside the other reader registrations in `Program.cs`:
```csharp
builder.Services.AddSingleton<JourneyReader>();
```
  (Insert next to the existing `builder.Services.AddSingleton<ChangeLogReader>();` line — `JourneyReader`'s constructor takes a `ChangeLogReader`, which DI will resolve from that same registration.)
- [ ] Write `Pages/Knowledge/Map.cshtml.cs`:
```csharp
using Boukensha.Core.Knowledge;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Knowledge;

public class MapModel(KnowledgeStore store, JourneyReader journeyReader, ObservabilityPaths paths) : PageModel
{
    public IReadOnlyList<RoomRecord> Rooms { get; private set; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> ExitsByRoom { get; private set; } = new Dictionary<int, IReadOnlyList<ExitRecord>>();
    public IReadOnlyDictionary<int, RoomPosition> Positions { get; private set; } = new Dictionary<int, RoomPosition>();
    public int? CurrentRoomId { get; private set; }
    public IReadOnlyList<JourneyStep> Trail { get; private set; } = [];

    public void OnGet()
    {
        Rooms = store.ListRooms();
        ExitsByRoom = Rooms.ToDictionary(r => r.Id, r => store.ListExits(r.Id));
        Positions = MapLayout.Calculate(Rooms, ExitsByRoom).ToDictionary(p => p.RoomId);
        CurrentRoomId = store.GetCurrentRoom()?.Id;
        Trail = journeyReader.ReadTrail(paths.ChangeLogPath, Rooms).OrderByDescending(s => s.At).ToList();
    }
}
```
- [ ] Write `Pages/Knowledge/Map.cshtml` (fixed cell size, SVG canvas sized to the position extent, walked exits as lines, frontier exits as dangling stubs, up/down as a text badge):
```html
@page
@model Boukensha.Observability.Pages.Knowledge.MapModel
@{
    ViewData["Title"] = "Knowledge Map";
    const int cell = 140;
    const int roomW = 100;
    const int roomH = 50;
    var minX = Model.Positions.Count > 0 ? Model.Positions.Values.Min(p => p.X) : 0;
    var minY = Model.Positions.Count > 0 ? Model.Positions.Values.Min(p => p.Y) : 0;
    var maxX = Model.Positions.Count > 0 ? Model.Positions.Values.Max(p => p.X) : 0;
    var maxY = Model.Positions.Count > 0 ? Model.Positions.Values.Max(p => p.Y) : 0;
    var width = (maxX - minX + 2) * cell;
    var height = (maxY - minY + 2) * cell;
    Func<int, int> screenX = x => (x - minX + 1) * cell;
    Func<int, int> screenY = y => (y - minY + 1) * cell;
}

<h1>Knowledge Map</h1>

<svg width="@width" height="@height" style="background:#1a1a1a; border:1px solid #333;">
    @foreach (var room in Model.Rooms)
    {
        if (!Model.Positions.TryGetValue(room.Id, out var pos)) continue;
        var cx = screenX(pos.X);
        var cy = screenY(pos.Y);

        foreach (var exit in Model.ExitsByRoom[room.Id])
        {
            if (exit.State == "walked" && exit.ToRoomId is not null && Model.Positions.TryGetValue(exit.ToRoomId.Value, out var toPos))
            {
                <line x1="@cx" y1="@cy" x2="@screenX(toPos.X)" y2="@screenY(toPos.Y)" stroke="#666" stroke-width="2" />
            }
        }
    }
    @foreach (var room in Model.Rooms)
    {
        if (!Model.Positions.TryGetValue(room.Id, out var pos)) continue;
        var cx = screenX(pos.X);
        var cy = screenY(pos.Y);
        var isCurrent = room.Id == Model.CurrentRoomId;
        var frontierDirs = Model.ExitsByRoom[room.Id].Where(e => e.State == "frontier").Select(e => e.Direction[0]).ToList();
        var verticalBadges = Model.ExitsByRoom[room.Id].Where(e => e.Direction is "up" or "down").Select(e => e.Direction == "up" ? "↑" : "↓");

        <rect x="@(cx - roomW / 2)" y="@(cy - roomH / 2)" width="@roomW" height="@roomH"
              fill="@(isCurrent ? "#264" : "#223")" stroke="@(isCurrent ? "#4f8" : "#556")" stroke-width="2" rx="4" />
        <text x="@cx" y="@(cy - 5)" text-anchor="middle" fill="#ddd" font-size="12">@room.Name</text>
        <text x="@cx" y="@(cy + 12)" text-anchor="middle" fill="#888" font-size="10">
            visits: @room.VisitCount @(frontierDirs.Count > 0 ? "· ?" + string.Join(",", frontierDirs) : "") @string.Join("", verticalBadges)
        </text>
    }
</svg>
@if (Model.Rooms.Count == 0)
{
    <p>No rooms known yet.</p>
}

<h2>Journey Trail</h2>
<table>
    <thead><tr><th>At</th><th>Session</th><th>From</th><th>To</th></tr></thead>
    <tbody>
    @foreach (var step in Model.Trail)
    {
        <tr>
            <td class="mono">@step.At.ToString("yyyy-MM-dd HH:mm:ss")</td>
            <td class="mono">@step.SessionId</td>
            <td>@(step.FromRoomName ?? "(unknown)")</td>
            <td>@step.ToRoomName</td>
        </tr>
    }
    </tbody>
</table>
@if (Model.Trail.Count == 0)
{
    <p>No journey history yet.</p>
}
```
- [ ] Add a `Map` nav link to `Pages/Shared/_Layout.cshtml`'s `<nav>` (alongside the existing `Knowledge`/`Changes`/`Live` links):
```html
        <a href="/Knowledge/Map">Map</a>
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 5: End-to-end verification

**Files:** none (verification only).

- [x] Ran: `dotnet test week2_capable/dotnet/Boukensha.slnx` — all 80 tests pass (72 from before this spec + 3 new `RoutePlannerTests` + 2 `JourneyReaderTests` + 3 `MapLayoutTests` = 80).
- [x] Ran: `dotnet clean week2_capable/dotnet/Boukensha.slnx && dotnet build week2_capable/dotnet/Boukensha.slnx` — 0 errors, 8 warnings, all the already-accepted NU1903 advisory, no new ones.
- [x] No live Anthropic call needed — verification was free, reading existing `.boukensha/` data from prior sub-projects' live runs.
- [x] **Discovered and fixed a real Razor/SVG collision during this task**: `<text>` is a reserved Razor markup-transition keyword, so an SVG `<text>` element *with attributes* fails to compile (`RZ1023`). Fixed by emitting those two elements via `Html.Raw` with explicit `HtmlEncode` on the interpolated content (room name, subtitle) — not something the design or unit tests could have caught, only compiling the actual `.cshtml`.
- [x] Started the app and `curl`ed `/Knowledge/Map`, then stopped it via PowerShell `Stop-Process` (per this session's own memory note — bash `kill` doesn't actually stop a background `dotnet run` here). Confirmed real data throughout: both known rooms ("The Sewer Pipe", "The Grand Sewer") rendered with correct visit counts (6 and 3) and frontier-exit hints (`?n` and `?e,s,w`); 1 `<svg`, 2 `<rect>` (one per room), 2 `<line>` (the walked connections between them); the Journey Trail table had 4 rows with real session ids and timestamps from `.boukensha/knowledge_changes.jsonl`.
- [x] Updated this plan's checkboxes and `docs/plans/week_2/specs/journey_agent.md`'s status line to reflect completion.
- [ ] Commit (final) — single commit for all of Tasks 1–5, matching this session's established batching preference.
