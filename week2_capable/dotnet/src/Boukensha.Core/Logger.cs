using System.Security.Cryptography;
using System.Text.Json;

namespace Boukensha.Core;

public sealed class Logger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _sessionId;
    private readonly List<Action<IReadOnlyDictionary<string, object?>>> _subscribers = [];
    private readonly Lock _lock = new();

    public string Path { get; }

    public Logger(string dir, string? sessionId = null, string? log = null, IReadOnlyDictionary<string, object?>? snapshot = null)
    {
        _sessionId = sessionId ?? GenerateSessionId();
        Path = log ?? System.IO.Path.Combine(dir, $"{_sessionId}.jsonl");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        var start = new Dictionary<string, object?> { ["phase"] = "session_start" };
        if (snapshot is not null)
        {
            foreach (var (key, value) in snapshot) start[key] = value;
        }
        WriteLog(start);
    }

    public void Turn(int n) => WriteLog(new() { ["phase"] = "turn", ["n"] = n });

    public void Iteration(int n, int max) => WriteLog(new() { ["phase"] = "iteration", ["n"] = n, ["max"] = max });

    public void LimitReached(string kind, int n, int max) =>
        WriteLog(new() { ["phase"] = "limit_reached", ["kind"] = kind, ["n"] = n, ["max"] = max });

    public void TurnEnd(string reason, int iterations, int? tokens = null) =>
        WriteLog(new() { ["phase"] = "turn_end", ["reason"] = reason, ["iterations"] = iterations, ["tokens"] = tokens });

    public void Prompt(IReadOnlyList<Message> messages, IReadOnlyDictionary<string, ToolDefinition> tools, int contextWindow) =>
        WriteLog(new()
        {
            ["phase"] = "prompt",
            ["messages"] = messages.Select(SerializeMessage).ToList(),
            ["message_count"] = messages.Count,
            ["tool_count"] = tools.Count,
            ["tools"] = tools.Keys.ToList(),
            ["context_window"] = contextWindow,
        });

    public void ToolCatalog(IReadOnlyDictionary<string, ToolDefinition> tools) =>
        WriteLog(new()
        {
            ["phase"] = "tool_catalog",
            ["tools"] = tools.Values.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.ToDictionary(p => p.Key, p => new Dictionary<string, object?>
                {
                    ["type"] = p.Value.Type,
                    ["description"] = p.Value.Description,
                }),
            }).ToList(),
        });

    public void Compaction(int before, int dropped, int contextWindow) =>
        WriteLog(new() { ["phase"] = "compaction", ["before"] = before, ["dropped"] = dropped, ["context_window"] = contextWindow });

    public void ToolCall(string name, IReadOnlyDictionary<string, object?> args, string task) =>
        WriteLog(new() { ["phase"] = "tool_call", ["name"] = name, ["args"] = args, ["task"] = task });

    public void ToolResult(string name, string result, string task, int durationMs, bool ok = true, string? error = null) =>
        WriteLog(new()
        {
            ["phase"] = "tool_result",
            ["name"] = name,
            ["result"] = result,
            ["task"] = task,
            ["duration_ms"] = durationMs,
            ["ok"] = ok,
            ["error"] = error,
        });

    public void Response(string text, IReadOnlyDictionary<string, object?>? usage, string? stopReason, string? task, string? backend, double? costUsd, int durationMs) =>
        WriteLog(new()
        {
            ["phase"] = "response",
            ["text"] = text,
            ["usage"] = usage,
            ["stop_reason"] = stopReason,
            ["task"] = task,
            ["provider"] = backend,
            ["cost_usd"] = costUsd,
            ["duration_ms"] = durationMs,
        });

    public void Reasoning(string text, bool redacted = false) =>
        WriteLog(new() { ["phase"] = "reasoning", ["text"] = text, ["redacted"] = redacted });

    public void Plan(string text) => WriteLog(new() { ["phase"] = "plan", ["text"] = text });

    public void Subscribe(Action<IReadOnlyDictionary<string, object?>> handler)
    {
        lock (_lock) _subscribers.Add(handler);
    }

    public void Dispose() => _writer.Dispose();

    private static Dictionary<string, object?> SerializeMessage(Message message) => new()
    {
        ["role"] = message.Role,
        ["content"] = message.Content.IsText ? message.Content.Text : message.Content.Blocks,
    };

    private void WriteLog(Dictionary<string, object?> evt)
    {
        evt["session_id"] = _sessionId;
        evt["at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        List<Action<IReadOnlyDictionary<string, object?>>> subscribersSnapshot;
        lock (_lock)
        {
            _writer.WriteLine(JsonSerializer.Serialize(evt));
            subscribersSnapshot = [.. _subscribers];
        }
        foreach (var subscriber in subscribersSnapshot) subscriber(evt);
    }

    public static string GenerateSessionId()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{timestamp}-{suffix}";
    }
}
