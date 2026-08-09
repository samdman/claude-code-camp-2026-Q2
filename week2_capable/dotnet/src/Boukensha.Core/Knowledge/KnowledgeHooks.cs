namespace Boukensha.Core.Knowledge;

public static class KnowledgeHooks
{
    public static void Register(AgentHooks hooks, KnowledgeStore store)
    {
        hooks.OnAfterToolCall((name, args, result, ok, _) =>
        {
            if (!ok) return Task.CompletedTask;

            switch (name)
            {
                case "look" when string.IsNullOrEmpty(args.GetValueOrDefault("target") as string):
                    UpdateRoomFromLookOrMove(store, result, direction: null, isTransition: false);
                    break;
                case "move":
                    UpdateRoomFromLookOrMove(store, result, direction: args.GetValueOrDefault("direction") as string, isTransition: true);
                    break;
                case "flee":
                    // Flees in a random available direction -- MudManager doesn't tell us which,
                    // so no LinkExit call, but the resulting room (or lack thereof) still updates location.
                    UpdateRoomFromLookOrMove(store, result, direction: null, isTransition: true);
                    break;
                case "check" when (args.GetValueOrDefault("kind") as string) == "exits":
                    var current = store.GetCurrentRoom();
                    if (current is not null)
                    {
                        store.RecordExits(current.Id, MudTextParser.ParseExitsBlock(result));
                    }
                    break;
            }

            return Task.CompletedTask;
        });

        hooks.OnBeforeAgentCall((context, _) =>
        {
            var here = store.BuildHereBlock();
            if (!string.IsNullOrEmpty(here)) context.AddMessage("user", here);
            return Task.CompletedTask;
        });
    }

    private static void UpdateRoomFromLookOrMove(KnowledgeStore store, string result, string? direction, bool isTransition)
    {
        var parsed = MudTextParser.ParseRoomBlock(result);
        if (parsed is null)
        {
            // A transition that lands somewhere unparseable (dark room) means the
            // player has left the previous room -- clear location rather than leave
            // it pointing at a room they're no longer in.
            if (isTransition) store.ClearCurrentRoom();
            return;
        }

        var previousRoomId = store.GetCurrentRoom()?.Id;
        var room = store.UpsertRoom(parsed.Value.Name, parsed.Value.Description);

        if (direction is not null && previousRoomId is not null)
        {
            store.LinkExit(previousRoomId.Value, MudTextParser.NormalizeDirection(direction), room.Id);
        }

        store.SetCurrentRoom(room.Id);
    }
}
