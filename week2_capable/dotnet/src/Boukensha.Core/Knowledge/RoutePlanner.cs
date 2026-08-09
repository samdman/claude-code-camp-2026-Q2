namespace Boukensha.Core.Knowledge;

public sealed record RouteResult(bool Found, string? DestinationRoomName, IReadOnlyList<string> Directions, string Message);

public sealed class RoutePlanner(KnowledgeStore store)
{
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

    private IReadOnlyList<string>? FindPath(int startId, int targetId)
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

    private string FrontierHint(int roomId)
    {
        var frontier = store.ListExits(roomId).Where(e => e.State == "frontier").Select(e => e.Direction).ToList();
        return frontier.Count > 0
            ? $" Unexplored exits from here: {string.Join(", ", frontier)}."
            : " No unexplored exits known from here either.";
    }
}
