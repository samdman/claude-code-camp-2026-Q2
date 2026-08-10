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
            start = RoomGraph.FindBestMatch(store.ListRooms(), fromQuery);
            if (start is null)
            {
                return new RouteResult(false, null, [], $"No known room matching starting point '{fromQuery}'.");
            }
        }

        var destination = RoomGraph.FindBestMatch(store.ListRooms(), destinationQuery);

        if (destination is null)
        {
            return new RouteResult(false, null, [], $"No known room matching '{destinationQuery}'.{FrontierHint(start.Id)}");
        }

        if (destination.Id == start.Id)
        {
            return new RouteResult(true, destination.Name, [], $"You are already at '{destination.Name}'.");
        }

        var path = RoomGraph.FindPath(store, start.Id, destination.Id);
        if (path is null)
        {
            return new RouteResult(false, destination.Name, [],
                $"'{destination.Name}' is known but no walked path from '{start.Name}' has been found yet.{FrontierHint(start.Id)}");
        }

        return new RouteResult(true, destination.Name, path,
            $"Route to '{destination.Name}': {string.Join(", ", path)} ({path.Count} step{(path.Count == 1 ? "" : "s")}).");
    }

    private string FrontierHint(int roomId)
    {
        var frontier = store.ListExits(roomId).Where(e => e.State == "frontier").Select(e => e.Direction).ToList();
        return frontier.Count > 0
            ? $" Unexplored exits from here: {string.Join(", ", frontier)}."
            : " No unexplored exits known from here either.";
    }
}
