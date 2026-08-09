using Boukensha.Core.Knowledge;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Knowledge;

public class MapModel(KnowledgeStore store, JourneyReader journeyReader, ObservabilityPaths paths) : PageModel
{
    public IReadOnlyList<RoomRecord> Rooms { get; private set; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<ExitRecord>> ExitsByRoom { get; private set; } = new Dictionary<int, IReadOnlyList<ExitRecord>>();
    public IReadOnlyDictionary<int, RoomPosition> Positions { get; private set; } = new Dictionary<int, RoomPosition>();
    public int? CurrentRoomId { get; private set; }
    public IReadOnlyList<JourneyStep> Trail { get; private set; } = [];

    public void OnGet()
    {
        Rooms = store.ListRooms();
        ExitsByRoom = Rooms.ToDictionary(r => r.Id, r => store.ListExits(r.Id));
        Positions = MapLayout.Calculate(Rooms, ExitsByRoom).ToDictionary(p => p.RoomId);
        CurrentRoomId = store.GetCurrentRoom()?.Id;
        Trail = journeyReader.ReadTrail(paths.ChangeLogPath, Rooms).OrderByDescending(s => s.At).ToList();
    }
}
