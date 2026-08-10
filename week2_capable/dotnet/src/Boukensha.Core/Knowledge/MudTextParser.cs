using System.Text.RegularExpressions;

namespace Boukensha.Core.Knowledge;

public static class MudTextParser
{
    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);
    // "(x)" marks a closed-door exit (e.g. "[ Exits: n (w) ]") -- still a real,
    // known exit direction, just not currently walkable.
    private static readonly Regex ExitsLinePattern = new(@"^\[\s*Exits:\s*([a-z\s()]*)\]$", RegexOptions.Compiled);
    private static readonly Regex ExitEntryPattern = new(@"^(\w+)\s*-\s*(.+)$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> DirectionLetters = new Dictionary<string, string>
    {
        ["n"] = "north",
        ["e"] = "east",
        ["s"] = "south",
        ["w"] = "west",
        ["u"] = "up",
        ["d"] = "down",
    };

    private static readonly HashSet<string> FullDirections = ["north", "east", "south", "west", "up", "down"];

    public static string StripAnsi(string raw) => AnsiPattern.Replace(raw, string.Empty);

    public static string NormalizeDirection(string directionOrLetter)
    {
        var trimmed = directionOrLetter.Trim().ToLowerInvariant();
        return DirectionLetters.TryGetValue(trimmed, out var full) ? full : trimmed;
    }

    public static (string Name, string Description, IReadOnlyList<string> ExitLetters)? ParseRoomBlock(string raw)
    {
        var clean = StripAnsi(raw).Replace("\r\n", "\n");
        if (clean.Contains("It is pitch black")) return null;

        var lines = clean.Split('\n');
        if (lines.Length == 0) return null;

        var name = lines[0].Trim();
        if (name.Length == 0) return null;

        var descriptionLines = new List<string>();
        var exitLetters = new List<string>();
        var foundExitsLine = false;

        foreach (var line in lines.Skip(1))
        {
            var trimmedLine = line.Trim();
            var match = ExitsLinePattern.Match(trimmedLine);
            if (match.Success)
            {
                exitLetters = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(letter => letter.Trim('(', ')')).ToList();
                foundExitsLine = true;
                break;
            }
            if (trimmedLine.Length > 0) descriptionLines.Add(trimmedLine);
        }

        if (!foundExitsLine) return null;

        var description = descriptionLines.Count > 0 ? descriptionLines[0] : string.Empty;
        return (name, description, exitLetters);
    }

    public static IReadOnlyDictionary<string, string?> ParseExitsBlock(string raw)
    {
        var clean = StripAnsi(raw).Replace("\r\n", "\n");
        var result = new Dictionary<string, string?>();

        foreach (var line in clean.Split('\n'))
        {
            var match = ExitEntryPattern.Match(line.Trim());
            if (!match.Success) continue;

            var direction = NormalizeDirection(match.Groups[1].Value);
            if (!FullDirections.Contains(direction)) continue;

            var destination = match.Groups[2].Value.Trim();
            result[direction] = destination.Equals("Too dark to tell.", StringComparison.OrdinalIgnoreCase) ? null : destination;
        }

        return result;
    }
}
