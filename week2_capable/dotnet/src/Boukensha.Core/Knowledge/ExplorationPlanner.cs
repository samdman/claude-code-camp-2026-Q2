namespace Boukensha.Core.Knowledge;

public sealed class ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks)
{
    public async Task<RouteResult> ExploreTowardsAsync(string destinationQuery, int maxSteps)
    {
        var startRoom = store.GetCurrentRoom();
        if (startRoom is null)
        {
            return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
        }

        var discovered = new HashSet<int>();
        var unresolved = new HashSet<(int RoomId, string Direction)>();
        var stepsUsed = 0;

        while (stepsUsed < maxSteps)
        {
            var current = store.GetCurrentRoom();
            if (current is null) break;

            var candidate = NextFrontierCandidate(current.Id, unresolved);
            if (candidate is null) break;

            var (targetRoomId, direction) = candidate.Value;

            if (targetRoomId != current.Id)
            {
                var approach = RoomGraph.FindPath(store, current.Id, targetRoomId);
                if (approach is null)
                {
                    unresolved.Add((targetRoomId, direction));
                    continue;
                }

                var ranOutOfBudget = false;
                foreach (var stepDirection in approach)
                {
                    if (stepsUsed >= maxSteps) { ranOutOfBudget = true; break; }
                    await DispatchAsync("move", new Dictionary<string, object?> { ["direction"] = stepDirection });
                    stepsUsed++;
                }
                if (ranOutOfBudget) break;
            }

            if (stepsUsed >= maxSteps) break;

            // After any approach-walking above, we're now standing in targetRoomId's
            // room regardless of where `current` was at the top of this iteration.
            var beforeMoveRoomId = targetRoomId;
            await DispatchAsync("move", new Dictionary<string, object?> { ["direction"] = direction });
            stepsUsed++;

            var afterMove = store.GetCurrentRoom();
            if (afterMove is null || afterMove.Id == beforeMoveRoomId)
            {
                // The exit didn't resolve to a recognizable new room. KnowledgeHooks
                // cannot tell "genuinely rejected, stayed put" ("Alas, you cannot go
                // that way") apart from "did move, into an unlit room" ("It is pitch
                // black...") -- both fail to parse as a room block -- so it has already
                // (correctly) cleared position to unknown rather than guess. Overwriting
                // that with an assumed "stayed put" would desync the knowledge store from
                // the real game state whenever it was actually the dark-room case; live
                // verification against a real dungeon with dark rooms hit exactly this,
                // producing an endless "0 new rooms" loop. So: stop here rather than
                // continue on a possibly-wrong position -- the next plan_route call will
                // surface "current location unknown -- look around first" via
                // RoutePlanner's own existing guard, prompting a normal recovery look.
                unresolved.Add((targetRoomId, direction));
                break;
            }

            if (afterMove.VisitCount == 1) discovered.Add(afterMove.Id);

            await DispatchAsync("check", new Dictionary<string, object?> { ["kind"] = "exits" });

            if (RoomGraph.RoomMatchesQuery(afterMove, destinationQuery))
            {
                var route = RoomGraph.FindPath(store, startRoom.Id, afterMove.Id) ?? [];
                return new RouteResult(true, afterMove.Name, route,
                    $"Route to '{afterMove.Name}': {string.Join(", ", route)} ({route.Count} step{(route.Count == 1 ? "" : "s")}). " +
                    $"Discovered {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} while exploring.");
            }
        }

        var frontiersRemaining = CountFrontiers();
        return frontiersRemaining == 0
            ? new RouteResult(false, null, [],
                $"Explored the full known map ({discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found) -- no room matching '{destinationQuery}' exists.")
            : new RouteResult(false, null, [],
                $"Still exploring for '{destinationQuery}': {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found, " +
                $"{frontiersRemaining} frontier{(frontiersRemaining == 1 ? "" : "s")} remaining. Call plan_route again to continue.");
    }

    private (int RoomId, string Direction)? NextFrontierCandidate(int currentRoomId, HashSet<(int RoomId, string Direction)> unresolved)
    {
        (int RoomId, string Direction, int Distance)? best = null;

        foreach (var room in store.ListRooms())
        {
            foreach (var exit in store.ListExits(room.Id).Where(e => e.State == "frontier"))
            {
                if (unresolved.Contains((room.Id, exit.Direction))) continue;

                int distance;
                if (room.Id == currentRoomId)
                {
                    distance = 0;
                }
                else
                {
                    var path = RoomGraph.FindPath(store, currentRoomId, room.Id);
                    if (path is null) continue;
                    distance = path.Count;
                }

                if (best is null || distance < best.Value.Distance)
                {
                    best = (room.Id, exit.Direction, distance);
                }
            }
        }

        return best is null ? null : (best.Value.RoomId, best.Value.Direction);
    }

    private int CountFrontiers() =>
        store.ListRooms().Sum(room => store.ListExits(room.Id).Count(e => e.State == "frontier"));

    private async Task<string> DispatchAsync(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        string result;
        var ok = true;
        try
        {
            result = await registry.DispatchAsync(toolName, args);
        }
        catch (Exception e)
        {
            ok = false;
            result = $"ERROR: {e.GetType().Name}: {e.Message}";
        }
        await hooks.RaiseAfterToolCall(toolName, args, result, ok, CancellationToken.None);
        return result;
    }
}
