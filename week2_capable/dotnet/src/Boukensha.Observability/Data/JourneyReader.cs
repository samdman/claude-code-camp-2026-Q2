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
