using System.Text.Json.Nodes;

namespace Boukensha.Observability;

public sealed record ChangeEntry(DateTimeOffset At, string? SessionId, string Kind, JsonNode? Before, JsonNode? After);

public sealed class ChangeLogReader
{
    public IReadOnlyList<ChangeEntry> ReadEntries(string filePath)
    {
        if (!File.Exists(filePath)) return [];

        var entries = new List<ChangeEntry>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;

            var at = obj["at"]?.GetValue<string>() is { } atText && DateTimeOffset.TryParse(atText, out var parsed) ? parsed : DateTimeOffset.MinValue;
            entries.Add(new ChangeEntry(at, obj["session_id"]?.GetValue<string>(), obj["kind"]?.GetValue<string>() ?? "unknown", obj["before"], obj["after"]));
        }
        return entries;
    }
}
