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
