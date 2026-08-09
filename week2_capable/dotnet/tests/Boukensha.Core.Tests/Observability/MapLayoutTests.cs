using Boukensha.Core.Knowledge;
using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class MapLayoutTests
{
    [Fact]
    public void Calculate_PositionsRoomsByDirectionOffsetFromRoot()
    {
        var rooms = new List<RoomRecord>
        {
            new(1, "fp1", "Start", "d", 1),
            new(2, "fp2", "North", "d", 1),
            new(3, "fp3", "East", "d", 1),
        };
        var exits = new Dictionary<int, IReadOnlyList<ExitRecord>>
        {
            [1] = [new ExitRecord("north", "walked", "North", null, 2), new ExitRecord("east", "walked", "East", null, 3)],
            [2] = [],
            [3] = [],
        };

        var positions = MapLayout.Calculate(rooms, exits).ToDictionary(p => p.RoomId);

        Assert.Equal((0, 0), (positions[1].X, positions[1].Y));
        Assert.Equal((0, -1), (positions[2].X, positions[2].Y)); // north = y-1
        Assert.Equal((1, 0), (positions[3].X, positions[3].Y));  // east = x+1
    }

    [Fact]
    public void Calculate_CycleKeepsFirstAssignedPosition()
    {
        var rooms = new List<RoomRecord>
        {
            new(1, "fp1", "A", "d", 1),
            new(2, "fp2", "B", "d", 1),
            new(3, "fp3", "C", "d", 1),
        };
        // A -> B -> C -> A (a loop back to the start)
        var exits = new Dictionary<int, IReadOnlyList<ExitRecord>>
        {
            [1] = [new ExitRecord("north", "walked", "B", null, 2)],
            [2] = [new ExitRecord("east", "walked", "C", null, 3)],
            [3] = [new ExitRecord("south", "walked", "A", null, 1)],
        };

        var positions = MapLayout.Calculate(rooms, exits).ToDictionary(p => p.RoomId);

        Assert.Equal(3, positions.Count);
        Assert.Equal((0, 0), (positions[1].X, positions[1].Y));
    }

    [Fact]
    public void Calculate_DisconnectedRoom_GetsADifferentPositionNotOverlapping()
    {
        var rooms = new List<RoomRecord>
        {
            new(1, "fp1", "A", "d", 1),
            new(2, "fp2", "Isolated", "d", 1),
        };
        var exits = new Dictionary<int, IReadOnlyList<ExitRecord>>
        {
            [1] = [],
            [2] = [],
        };

        var positions = MapLayout.Calculate(rooms, exits).ToDictionary(p => p.RoomId);

        Assert.Equal(2, positions.Count);
        Assert.NotEqual((positions[1].X, positions[1].Y), (positions[2].X, positions[2].Y));
    }
}
