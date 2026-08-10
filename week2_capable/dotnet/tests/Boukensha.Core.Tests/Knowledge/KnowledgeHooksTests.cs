using Boukensha.Core;
using Boukensha.Core.Knowledge;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class KnowledgeHooksTests
{
    private static KnowledgeStore NewStore() =>
        new(Path.Combine(Directory.CreateTempSubdirectory("boukensha_knowledge_hooks_test").FullName, "knowledge.db"));

    [Fact]
    public async Task Register_BeforeAgentCall_DoesNotInjectDuplicateHereBlockWhileStationary()
    {
        using var store = NewStore();
        var room = store.UpsertRoom("The Sewer Pipe", "description");
        store.SetCurrentRoom(room.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);

        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);
        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);
        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);

        Assert.Single(context.Messages);
    }

    [Fact]
    public async Task Register_BeforeAgentCall_InjectsAgainWhenCurrentRoomChanges()
    {
        using var store = NewStore();
        var roomA = store.UpsertRoom("A", "a");
        var roomB = store.UpsertRoom("B", "b");
        store.SetCurrentRoom(roomA.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);

        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);
        store.SetCurrentRoom(roomB.Id);
        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);

        Assert.Equal(2, context.Messages.Count);
    }

    [Fact]
    public async Task Register_BeforeAgentCall_NoCurrentRoom_InjectsNothing()
    {
        using var store = NewStore();
        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);
        var context = new Context(new PlayerTask(), contextWindow: 1000);

        await hooks.RaiseBeforeAgentCall(context, CancellationToken.None);

        Assert.Empty(context.Messages);
    }

    [Fact]
    public async Task Register_AfterToolCall_RecallSuccess_UpdatesCurrentRoom()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);

        var args = new Dictionary<string, object?> { ["command"] = "recall" };
        await hooks.RaiseAfterToolCall("send_raw", args, "Temple\nA sacred hall.\n[ Exits: n ]", true, CancellationToken.None);

        var current = store.GetCurrentRoom();
        Assert.NotNull(current);
        Assert.Equal("Temple", current!.Name);
    }

    [Fact]
    public async Task Register_AfterToolCall_RecallRejected_LeavesCurrentRoomUnchanged()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);

        var args = new Dictionary<string, object?> { ["command"] = "recall" };
        await hooks.RaiseAfterToolCall("send_raw", args, "You have too much on your mind to recall.", true, CancellationToken.None);

        // Unlike a dark room, a rejected recall is unambiguous -- the character
        // never left, so position must stay exactly as it was, not clear to unknown.
        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id);
    }

    [Fact]
    public async Task Register_AfterToolCall_SendRawNonRecallCommand_IsIgnored()
    {
        using var store = NewStore();
        var start = store.UpsertRoom("Start", "the starting room");
        store.SetCurrentRoom(start.Id);

        var hooks = new AgentHooks();
        KnowledgeHooks.Register(hooks, store);

        var args = new Dictionary<string, object?> { ["command"] = "who" };
        await hooks.RaiseAfterToolCall("send_raw", args, "Temple\nA sacred hall.\n[ Exits: n ]", true, CancellationToken.None);

        Assert.Equal(start.Id, store.GetCurrentRoom()!.Id);
    }
}
