namespace Boukensha.Core.Knowledge;

public sealed class ExplorationPlanner(KnowledgeStore store, Registry registry, AgentHooks hooks, Logger logger)
{
    public async Task<RouteResult> ExploreTowardsAsync(string destinationQuery, int maxSteps, double confidenceThreshold)
    {
        var startRoom = store.GetCurrentRoom();
        if (startRoom is null)
        {
            return new RouteResult(false, null, [], "Current location is unknown -- look around first.");
        }

        var discovered = new HashSet<int>();
        var unresolved = new HashSet<(int RoomId, string Direction)>();
        var pathTaken = new List<string>();
        var stepsUsed = 0;
        var stepIndex = 0;

        while (stepsUsed < maxSteps)
        {
            var current = store.GetCurrentRoom();
            if (current is null)
            {
                return await RetreatAsync(startRoom, "unresolved_position", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            var candidate = NextFrontierCandidate(current.Id, destinationQuery, unresolved);
            if (candidate is null)
            {
                return await RetreatAsync(startRoom, "exhausted", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            var (targetRoomId, direction, hint, confidence) = candidate.Value;
            stepIndex++;

            if (confidence < confidenceThreshold)
            {
                logger.ExplorationStep(stepIndex, targetRoomId, direction, hint, confidence, explored: false);
                return await RetreatAsync(startRoom, "stuck", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            logger.ExplorationStep(stepIndex, targetRoomId, direction, hint, confidence, explored: true);

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
                    pathTaken.Add(stepDirection);
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
                // cannot tell "genuinely rejected, stayed put" apart from "did move,
                // into an unlit room" -- both fail to parse as a room block -- so it
                // has already (correctly) cleared position to unknown rather than
                // guess. Retreat still tries recall (which doesn't depend on knowing
                // current position at all) before giving up.
                return await RetreatAsync(startRoom, "unresolved_position", pathTaken, discovered, stepsUsed, destinationQuery, confidenceThreshold);
            }

            pathTaken.Add(direction);
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
        return new RouteResult(false, null, [],
            $"Still exploring for '{destinationQuery}': {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found, " +
            $"{frontiersRemaining} frontier{(frontiersRemaining == 1 ? "" : "s")} remaining. Call plan_route again to continue.");
    }

    private (int RoomId, string Direction, string? Hint, double Confidence)? NextFrontierCandidate(
        int currentRoomId, string destinationQuery, HashSet<(int RoomId, string Direction)> unresolved)
    {
        (int RoomId, string Direction, string? Hint, double Confidence, int Distance)? best = null;

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

                var confidence = RoomGraph.ExitConfidence(exit, destinationQuery);

                if (best is null
                    || confidence > best.Value.Confidence
                    || (confidence == best.Value.Confidence && distance < best.Value.Distance))
                {
                    best = (room.Id, exit.Direction, exit.Hint, confidence, distance);
                }
            }
        }

        return best is null ? null : (best.Value.RoomId, best.Value.Direction, best.Value.Hint, best.Value.Confidence);
    }

    private async Task<RouteResult> RetreatAsync(
        RoomRecord startRoom, string reason, List<string> pathTaken, HashSet<int> discovered,
        int stepsUsed, string destinationQuery, double confidenceThreshold)
    {
        var recalled = await TryRecallAsync(startRoom, pathTaken);
        var frontiersRemaining = CountFrontiers();

        logger.ExplorationRetreat(reason, stepsUsed, discovered.Count, frontiersRemaining, recalled);

        if (reason == "exhausted")
        {
            return new RouteResult(false, null, [],
                $"Explored the full known map ({discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found) -- no room matching '{destinationQuery}' exists.");
        }

        var leadSentence = reason == "stuck"
            ? $"No promising leads for '{destinationQuery}' (best candidate confidence below {confidenceThreshold:0.0})."
            : "Lost track of position after an unresolved move.";
        var recalledClause = recalled ? $" Recalled back to '{startRoom.Name}'." : "";

        return new RouteResult(false, null, [],
            $"{leadSentence}{recalledClause} {discovered.Count} new room{(discovered.Count == 1 ? "" : "s")} found, " +
            $"{frontiersRemaining} frontier{(frontiersRemaining == 1 ? "" : "s")} remain unexplored. " +
            "Call plan_route again to keep exploring, or try a different name for the destination.");
    }

    private async Task<bool> TryRecallAsync(RoomRecord startRoom, List<string> pathTaken)
    {
        await DispatchAsync("send_raw", new Dictionary<string, object?> { ["command"] = "recall" });

        var afterRecall = store.GetCurrentRoom();
        if (afterRecall is not null && afterRecall.Id == startRoom.Id) return true;
        if (afterRecall is null) return false; // position was already/still unknown -- nothing to retrace from

        // Recall didn't land back at the origin (unavailable, on cooldown, an
        // unparseable response, or it teleported somewhere other than startRoom)
        // -- retrace this call's own moves in reverse instead of consulting the
        // graph. See the spec for why RoomGraph.FindPath is not used here.
        for (var i = pathTaken.Count - 1; i >= 0; i--)
        {
            await DispatchAsync("move", new Dictionary<string, object?> { ["direction"] = MudTextParser.OppositeDirection(pathTaken[i]) });
        }

        return store.GetCurrentRoom()?.Id == startRoom.Id;
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
