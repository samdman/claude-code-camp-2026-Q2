using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class SessionLogReaderTests
{
    private const string SessionStartLine =
        """{"phase":"session_start","task":"player","provider":"anthropic","model":"claude-haiku-4-5","context_window":200000,"max_turn_tokens":60000,"system":"You are Boukensha.","session_id":"20260809T201708Z-1bf1dc93","at":"2026-08-10T08:17:08+12:00"}""";

    private const string TurnLine =
        """{"phase":"turn","n":1,"session_id":"20260809T201708Z-1bf1dc93","at":"2026-08-10T08:17:12+12:00"}""";

    private const string ResponseLine =
        """{"phase":"response","text":"done","usage":{"input_tokens":4488,"output_tokens":106},"stop_reason":"end_turn","task":"player","provider":"anthropic","cost_usd":0.005018,"duration_ms":2270,"session_id":"20260809T201708Z-1bf1dc93","at":"2026-08-10T08:17:23+12:00"}""";

    private static string WriteFixture(params string[] lines)
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_session_reader_test").FullName;
        var path = Path.Combine(dir, "20260809T201708Z-1bf1dc93.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ListSessions_SummarizesTaskProviderModelTurnCountAndTokens()
    {
        var path = WriteFixture(SessionStartLine, TurnLine, ResponseLine);
        var sessionsDir = Path.GetDirectoryName(path)!;

        var summaries = new SessionLogReader().ListSessions(sessionsDir);

        var summary = Assert.Single(summaries);
        Assert.Equal("20260809T201708Z-1bf1dc93", summary.SessionId);
        Assert.Equal("player", summary.Task);
        Assert.Equal("anthropic", summary.Provider);
        Assert.Equal("claude-haiku-4-5", summary.Model);
        Assert.Equal(1, summary.TurnCount);
        Assert.Equal(4488, summary.TotalInputTokens);
        Assert.Equal(106, summary.TotalOutputTokens);
        Assert.Equal(0.005018, summary.TotalCostUsd, 6);
    }

    [Fact]
    public void ListSessions_NewestFirst()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_session_reader_test").FullName;
        File.WriteAllLines(Path.Combine(dir, "a.jsonl"),
            [SessionStartLine.Replace("20260809T201708Z-1bf1dc93", "session-a").Replace("2026-08-10T08:17:08+12:00", "2026-08-10T08:00:00+12:00")]);
        File.WriteAllLines(Path.Combine(dir, "b.jsonl"),
            [SessionStartLine.Replace("20260809T201708Z-1bf1dc93", "session-b").Replace("2026-08-10T08:17:08+12:00", "2026-08-10T09:00:00+12:00")]);

        var summaries = new SessionLogReader().ListSessions(dir);

        Assert.Equal("session-b", summaries[0].SessionId);
        Assert.Equal("session-a", summaries[1].SessionId);
    }

    [Fact]
    public void ListSessions_EmptyDirectory_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_session_reader_test").FullName;
        Assert.Empty(new SessionLogReader().ListSessions(dir));
    }

    [Fact]
    public void ReadEvents_ParsesEveryLineWithPhaseAndTimestamp()
    {
        var path = WriteFixture(SessionStartLine, TurnLine, ResponseLine);

        var events = new SessionLogReader().ReadEvents(path);

        Assert.Equal(3, events.Count);
        Assert.Equal("session_start", events[0].Phase);
        Assert.Equal("turn", events[1].Phase);
        Assert.Equal("response", events[2].Phase);
        Assert.Equal("player", events[0].Raw["task"]!.GetValue<string>());
    }
}
