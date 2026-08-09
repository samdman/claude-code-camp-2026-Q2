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
}
