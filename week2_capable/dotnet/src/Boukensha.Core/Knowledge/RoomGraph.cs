namespace Boukensha.Core.Knowledge;

/// <summary>
/// Shared graph queries used by both RoutePlanner (known-route BFS) and
/// ExplorationPlanner (frontier-ranked walking), so there's exactly one
/// path-finding implementation and one name-matching rule instead of two
/// copies that could drift apart.
/// </summary>
public static class RoomGraph
{
    public static bool RoomMatchesQuery(RoomRecord room, string query) =>
        room.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
        || room.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

    public static double ExitConfidence(ExitRecord exit, string query)
    {
        if (exit.Hint is null) return 0.2;
        if (exit.Hint.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (exit.Hint.Contains(query, StringComparison.OrdinalIgnoreCase)
            || query.Contains(exit.Hint, StringComparison.OrdinalIgnoreCase)) return 0.6;
        return 0.2;
    }

    public static RoomRecord? FindBestMatch(IReadOnlyList<RoomRecord> rooms, string query) =>
        rooms.FirstOrDefault(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? rooms.FirstOrDefault(r => RoomMatchesQuery(r, query));

    public static IReadOnlyList<string>? FindPath(KnowledgeStore store, int startId, int targetId)
    {
        var visited = new HashSet<int> { startId };
        var queue = new Queue<(int RoomId, List<string> Path)>();
        queue.Enqueue((startId, []));

        while (queue.Count > 0)
        {
            var (roomId, path) = queue.Dequeue();
            if (roomId == targetId) return path;

            foreach (var exit in store.ListExits(roomId).Where(e => e.State == "walked" && e.ToRoomId is not null))
            {
                var nextId = exit.ToRoomId!.Value;
                if (visited.Add(nextId))
                {
                    queue.Enqueue((nextId, [.. path, exit.Direction]));
                }
            }
        }
        return null;
    }
}
