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
