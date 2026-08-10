using Boukensha.Core.Knowledge;
using Xunit;

namespace Boukensha.Core.Tests.Knowledge;

public class MudTextParserTests
{
    private const string LitRoomLook =
        "\x1B[0;33mThe Sewer Pipe\x1B[0m\r\n" +
        "   You are in what reminds you of a foul sewer, as if you liked being here!\r\n" +
        "You can see two exits leading either north or south.\r\n" +
        "\x1B[0;36m[ Exits: n s ]\x1B[0m\r\n" +
        "\x1B[0;33mThe small hairy Spider is here, busy with its web.\r\n\x1B[0m\r\n" +
        "21H 100M 84V (news) (motd) > ";

    private const string DarkRoomLook =
        "It is pitch black...\r\n\x1B[0;33m\x1B[0m\r\n21H 100M 85V (news) (motd) > ";

    // Captured live: a closed door shows as "(w)" in the compact exits line, distinct
    // from an open exit like "n".
    private const string ClosedDoorRoomLook =
        "\x1B[0;33mThe South End Of The Grand Pipe\x1B[0m\r\n" +
        "   You stand in water to your knees.  A doorway leads west from here.  The\r\n" +
        "pipe stretches north.\r\n" +
        "\x1B[0;36m[ Exits: n \x1B[0;31m(w)\x1B[0;36m ]\x1B[0m\r\n\r\n" +
        "13H 100M 66V (news) (motd) > ";

    private const string ExitsBlock =
        "Obvious exits:\r\nnorth - Too dark to tell.\r\nsouth - The Grand Sewer\r\n\r\n21H 100M 84V (news) (motd) > ";

    [Fact]
    public void StripAnsi_RemovesColorCodes()
    {
        var stripped = MudTextParser.StripAnsi("\x1B[0;33mThe Sewer Pipe\x1B[0m");
        Assert.Equal("The Sewer Pipe", stripped);
    }

    [Theory]
    [InlineData("n", "north")]
    [InlineData("e", "east")]
    [InlineData("s", "south")]
    [InlineData("w", "west")]
    [InlineData("u", "up")]
    [InlineData("d", "down")]
    [InlineData("north", "north")]
    [InlineData("D", "down")]
    public void NormalizeDirection_MapsLettersAndPassesThroughFullWords(string input, string expected)
    {
        Assert.Equal(expected, MudTextParser.NormalizeDirection(input));
    }

    [Fact]
    public void ParseRoomBlock_ExtractsNameDescriptionAndExitLetters()
    {
        var parsed = MudTextParser.ParseRoomBlock(LitRoomLook);

        Assert.NotNull(parsed);
        Assert.Equal("The Sewer Pipe", parsed!.Value.Name);
        Assert.Equal("You are in what reminds you of a foul sewer, as if you liked being here!", parsed.Value.Description);
        Assert.Equal(["n", "s"], parsed.Value.ExitLetters);
    }

    [Fact]
    public void ParseRoomBlock_ReturnsNullForDarkRoom()
    {
        Assert.Null(MudTextParser.ParseRoomBlock(DarkRoomLook));
    }

    [Fact]
    public void ParseRoomBlock_ParsesRoomWithClosedDoorExit()
    {
        var parsed = MudTextParser.ParseRoomBlock(ClosedDoorRoomLook);

        Assert.NotNull(parsed);
        Assert.Equal("The South End Of The Grand Pipe", parsed!.Value.Name);
        Assert.Equal("You stand in water to your knees.  A doorway leads west from here.  The", parsed.Value.Description);
        Assert.Equal(["n", "w"], parsed.Value.ExitLetters);
    }

    [Fact]
    public void ParseExitsBlock_ExtractsDirectionsAndDestinations()
    {
        var exits = MudTextParser.ParseExitsBlock(ExitsBlock);

        Assert.Equal(2, exits.Count);
        Assert.Null(exits["north"]);
        Assert.Equal("The Grand Sewer", exits["south"]);
    }
}
