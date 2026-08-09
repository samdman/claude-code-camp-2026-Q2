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
