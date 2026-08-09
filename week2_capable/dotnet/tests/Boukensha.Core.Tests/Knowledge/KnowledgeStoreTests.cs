using Boukensha.Core.Knowledge;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class KnowledgeStoreTests
{
    private static KnowledgeStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName, "knowledge.db"));

    [Fact]
    public void UpsertRoom_SameFingerprintIncrementsVisitCountInsteadOfDuplicating()
    {
        using var store = NewStore();

        var first = store.UpsertRoom("The Sewer Pipe", "You are in what reminds you of a foul sewer.");
        var second = store.UpsertRoom("The Sewer Pipe", "You are in what reminds you of a foul sewer.");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, first.VisitCount);
        Assert.Equal(2, second.VisitCount);
    }

    [Fact]
    public void UpsertRoom_DifferentDescriptionIsADifferentRoom()
    {
        using var store = NewStore();

        var a = store.UpsertRoom("The Sewer Pipe", "description A");
        var b = store.UpsertRoom("The Sewer Pipe", "description B");

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void GetCurrentRoom_NullUntilSet()
    {
        using var store = NewStore();
        Assert.Null(store.GetCurrentRoom());

        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        Assert.Equal(room.Id, store.GetCurrentRoom()!.Id);
    }

    [Fact]
    public void ClearCurrentRoom_MakesLocationUnknownAgain()
    {
        using var store = NewStore();
        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        store.ClearCurrentRoom();

        Assert.Null(store.GetCurrentRoom());
    }

    [Fact]
    public void LinkExit_ThenBuildHereBlock_ShowsWalkedDestinationWithCheckmark()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("The Sewer Pipe", "start description");
        var dest = store.UpsertRoom("The Grand Sewer", "dest description");
        store.SetCurrentRoom(start.Id);

        store.LinkExit(start.Id, "south", dest.Id);

        var here = store.BuildHereBlock();
        Assert.Contains("[here] The Sewer Pipe (visit 1)", here);
        Assert.Contains("s→The Grand Sewer ✓", here);
    }

    [Fact]
    public void RecordExits_FrontierExitShowsQuestionMark()
    {
        using var store = NewStore();
        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        store.RecordExits(room.Id, new Dictionary<string, string?> { ["north"] = null, ["south"] = "The Grand Sewer" });

        var here = store.BuildHereBlock();
        Assert.Contains("n→?", here);
    }

    [Fact]
    public void RecordExits_DoesNotOverwriteAlreadyWalkedExit()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("The Sewer Pipe", "start description");
        var dest = store.UpsertRoom("The Grand Sewer", "dest description");
        store.SetCurrentRoom(start.Id);
        store.LinkExit(start.Id, "south", dest.Id);

        // A later `check exits` call re-reports the same direction as a hint --
        // must not clobber the already-walked link.
        store.RecordExits(start.Id, new Dictionary<string, string?> { ["south"] = "The Grand Sewer" });

        var here = store.BuildHereBlock();
        Assert.Contains("s→The Grand Sewer ✓", here);
    }

    [Fact]
    public void BuildHereBlock_EmptyWhenNoCurrentRoom()
    {
        using var store = NewStore();
        Assert.Equal(string.Empty, store.BuildHereBlock());
    }

    [Fact]
    public void UpsertRoom_FirstCreation_WritesChangeJournalEntryWithNullBefore()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"), sessionId: "sess-1");

        store.UpsertRoom("The Sewer Pipe", "description");

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        Assert.Single(lines);
        Assert.Contains("\"kind\":\"room_upserted\"", lines[0]);
        Assert.Contains("\"session_id\":\"sess-1\"", lines[0]);
        Assert.Contains("\"before\":null", lines[0]);
    }

    [Fact]
    public void UpsertRoom_Revisit_WritesChangeWithPreviousVisitCountAsBefore()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));

        store.UpsertRoom("The Sewer Pipe", "description");
        store.UpsertRoom("The Sewer Pipe", "description");

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"visit_count\":1", lines[1]);
    }

    [Fact]
    public void LinkExit_WritesChangeJournalEntryWithWalkedAfterState()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));
        var start = store.UpsertRoom("A", "a");
        var dest = store.UpsertRoom("B", "b");

        store.LinkExit(start.Id, "south", dest.Id);

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        var linkLine = Assert.Single(lines, l => l.Contains("\"kind\":\"exit_linked\""));
        Assert.Contains("\"state\":\"walked\"", linkLine);
    }

    [Fact]
    public void RecordExits_AlreadyWalkedExit_WritesNoChangeEntry()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));
        var start = store.UpsertRoom("A", "a");
        var dest = store.UpsertRoom("B", "b");
        store.LinkExit(start.Id, "south", dest.Id);
        var beforeCount = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl")).Length;

        store.RecordExits(start.Id, new Dictionary<string, string?> { ["south"] = "B" });

        var afterCount = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl")).Length;
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public void SetCurrentRoom_WritesLocationChangedEntry()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));
        var room = store.UpsertRoom("A", "a");

        store.SetCurrentRoom(room.Id);

        var lines = File.ReadAllLines(Path.Combine(dir, "knowledge_changes.jsonl"));
        Assert.Contains(lines, l => l.Contains("\"kind\":\"location_changed\""));
    }

    [Fact]
    public void ClearCurrentRoom_WhenAlreadyUnknown_WritesNoChangeEntry()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_knowledge_test").FullName;
        using var store = new KnowledgeStore(Path.Combine(dir, "knowledge.db"));

        store.ClearCurrentRoom();

        // The change journal file is created lazily on first write, so a no-op
        // ClearCurrentRoom on a fresh store may leave it not existing at all.
        var changeLogPath = Path.Combine(dir, "knowledge_changes.jsonl");
        var lines = File.Exists(changeLogPath) ? File.ReadAllLines(changeLogPath) : [];
        Assert.Empty(lines);
    }
}
