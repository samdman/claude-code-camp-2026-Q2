using System.Text.Json.Nodes;

namespace Boukensha.Observability;

public sealed record SessionSummary(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Task,
    string? Provider,
    string? Model,
    int TurnCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    double TotalCostUsd,
    string FilePath);

public sealed record SessionEvent(string Phase, DateTimeOffset At, JsonObject Raw);

public sealed class SessionLogReader
{
    public IReadOnlyList<SessionSummary> ListSessions(string sessionsDir)
    {
        if (!Directory.Exists(sessionsDir)) return [];

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.jsonl"))
        {
            var summary = Summarize(file);
            if (summary is not null) summaries.Add(summary);
        }
        return summaries.OrderByDescending(s => s.StartedAt).ToList();
    }

    public IReadOnlyList<SessionEvent> ReadEvents(string filePath)
    {
        var events = new List<SessionEvent>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;
            var phase = obj["phase"]?.GetValue<string>() ?? "unknown";
            events.Add(new SessionEvent(phase, ParseAt(obj), obj));
        }
        return events;
    }

    private static SessionSummary? Summarize(string filePath)
    {
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        string? sessionId = null, task = null, provider = null, model = null;
        var turnCount = 0;
        long totalInput = 0, totalOutput = 0;
        double totalCost = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject obj) continue;

            var at = ParseAt(obj);
            startedAt ??= at;
            endedAt = at;

            switch (obj["phase"]?.GetValue<string>())
            {
                case "session_start":
                    sessionId = obj["session_id"]?.GetValue<string>();
                    task = obj["task"]?.GetValue<string>();
                    provider = obj["provider"]?.GetValue<string>();
                    model = obj["model"]?.GetValue<string>();
                    break;
                case "turn":
                    turnCount++;
                    break;
                case "response":
                    if (obj["usage"] is JsonObject usage)
                    {
                        totalInput += usage["input_tokens"]?.GetValue<long>() ?? 0;
                        totalOutput += usage["output_tokens"]?.GetValue<long>() ?? 0;
                    }
                    totalCost += obj["cost_usd"]?.GetValue<double>() ?? 0.0;
                    break;
            }
        }

        if (sessionId is null || startedAt is null) return null;
        return new SessionSummary(sessionId, startedAt.Value, endedAt, task, provider, model, turnCount, totalInput, totalOutput, totalCost, filePath);
    }

    private static DateTimeOffset ParseAt(JsonObject obj) =>
        obj["at"]?.GetValue<string>() is { } at && DateTimeOffset.TryParse(at, out var parsed) ? parsed : DateTimeOffset.MinValue;
}
