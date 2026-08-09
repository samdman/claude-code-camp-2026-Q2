using System.Text.Json.Nodes;

namespace Boukensha.Observability;

public sealed record TelnetEntry(DateTimeOffset At, string Direction, string Text);

public sealed class TelnetLogReader
{
    public IReadOnlyList<TelnetEntry> ReadEntries(string filePath)
    {
        if (!File.Exists(filePath)) return [];

        var entries = new List<TelnetEntry>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;

            var at = obj["at"]?.GetValue<string>() is { } atText && DateTimeOffset.TryParse(atText, out var parsed) ? parsed : DateTimeOffset.MinValue;
            entries.Add(new TelnetEntry(at, obj["direction"]?.GetValue<string>() ?? "unknown", obj["text"]?.GetValue<string>() ?? string.Empty));
        }
        return entries;
    }
}
