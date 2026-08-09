using Boukensha.Core.Knowledge;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Knowledge;

public class IndexModel(KnowledgeStore store) : PageModel
{
    public IReadOnlyList<RoomRecord> Rooms { get; private set; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> ExitsByRoom { get; private set; } = new Dictionary<int, IReadOnlyList<ExitRecord>>();
    public int? CurrentRoomId { get; private set; }

    public void OnGet()
    {
        Rooms = store.ListRooms();
        CurrentRoomId = store.GetCurrentRoom()?.Id;
        ExitsByRoom = Rooms.ToDictionary(r => r.Id, r => store.ListExits(r.Id));
    }
}
