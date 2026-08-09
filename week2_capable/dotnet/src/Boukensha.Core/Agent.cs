using System.Text.Json.Nodes;

namespace Boukensha.Core;

public sealed class Agent
{
    private const int DefaultMaxIterations = 25;
    private const int WrapUpOutputTokens = 400;
    private const string WrapUpDirective =
        "You are almost out of iterations or context budget for this turn. " +
        "Stop calling tools now and give the user your best final answer based on what you've learned so far.";

    private readonly Context _context;
    private readonly Registry _registry;
    private readonly PromptBuilder _builder;
    private readonly Client _client;
    private readonly Logger _logger;
    private readonly int _maxIterations;
    private readonly int _maxTurnTokens;
    private readonly int? _maxOutputTokens;
    private readonly AgentHooks _hooks;
    private int _iteration;

    public Agent(
        Context context,
        Registry registry,
        PromptBuilder builder,
        Client client,
        Logger logger,
        IReadOnlyDictionary<string, object?>? taskSettings = null,
        int? maxIterations = null,
        int? maxTurnTokens = null,
        int? maxOutputTokens = null,
        AgentHooks? hooks = null)
    {
        _context = context;
        _registry = registry;
        _builder = builder;
        _client = client;
        _logger = logger;
        _maxIterations = maxIterations
            ?? (taskSettings is not null ? context.Task.MaxIterations(taskSettings) : DefaultMaxIterations);
        _maxTurnTokens = maxTurnTokens ?? 0;
        _maxOutputTokens = maxOutputTokens
            ?? (taskSettings is not null ? context.Task.MaxOutputTokens(taskSettings) : null);
        _hooks = hooks ?? new AgentHooks();
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        _context.ResetTurnTokens();
        CompactIfNeeded();

        while (true)
        {
            if (_maxIterations > 0 && _iteration >= _maxIterations)
            {
                _logger.LimitReached("iterations", _iteration, _maxIterations);
                return await WrapUpAsync("max_iterations", cancellationToken);
            }
            if (_maxTurnTokens > 0 && _context.TurnTokens >= _maxTurnTokens)
            {
                _logger.LimitReached("turn_tokens", _context.TurnTokens, _maxTurnTokens);
                return await WrapUpAsync("max_turn_tokens", cancellationToken);
            }

            _iteration++;
            _logger.Iteration(_iteration, _maxIterations);
            await _hooks.RaiseBeforeAgentCall(_context, cancellationToken);
            _logger.Prompt(_context.Messages, _context.Tools, _context.ContextWindow);

            var response = await _client.CallAsync(_maxOutputTokens ?? 1024, cancellationToken: cancellationToken);
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            LogReasoning(parsed.Content);

            if (parsed.StopReason == "tool_use")
            {
                await HandleToolCallsAsync(parsed.Content, cancellationToken);
                continue;
            }

            var text = ExtractText(parsed.Content);
            LogResponse(text, response, parsed.StopReason);
            _logger.TurnEnd("completed", _iteration, _context.TurnTokens);
            _context.AddMessage("assistant", text);
            return text;
        }
    }

    private void CompactIfNeeded()
    {
        if (!_context.NeedsCompaction()) return;
        var before = _context.CurrentTokens;
        var dropped = _context.CompactMessages();
        _logger.Compaction(before, dropped, _context.ContextWindow);
    }

    private async Task<string> WrapUpAsync(string reason, CancellationToken cancellationToken)
    {
        _context.AddMessage("user", WrapUpDirective);
        string text;
        try
        {
            var response = await _client.CallAsync(WrapUpOutputTokens, tools: [], cancellationToken: cancellationToken);
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            text = ExtractText(parsed.Content);
            if (string.IsNullOrWhiteSpace(text)) text = FallbackMessage(reason);
            LogResponse(text, response, parsed.StopReason);
        }
        catch (ApiException)
        {
            text = FallbackMessage(reason);
        }
        _context.AddMessage("assistant", text);
        _logger.TurnEnd(reason, _iteration, _context.TurnTokens);
        return text;
    }

    private static string FallbackMessage(string reason) => reason switch
    {
        "max_iterations" => "I ran out of iterations before finishing this turn.",
        "max_turn_tokens" => "I ran out of token budget before finishing this turn.",
        _ => "I had to stop before finishing this turn.",
    };

    private async Task HandleToolCallsAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        var preamble = ExtractText(content);
        if (!string.IsNullOrWhiteSpace(preamble)) _logger.Plan(preamble);

        _context.AddMessage("assistant", content);

        foreach (var block in content.OfType<ToolUseBlock>())
        {
            _logger.ToolCall(block.Name, block.Input);
            await _hooks.RaiseBeforeToolCall(block.Name, block.Input, cancellationToken);

            string result;
            bool ok = true;
            string? error = null;
            try
            {
                result = await _registry.DispatchAsync(block.Name, block.Input);
            }
            catch (Exception e)
            {
                ok = false;
                error = e.Message;
                result = $"ERROR: {e.GetType().Name}: {e.Message}";
            }
            _logger.ToolResult(block.Name, result, ok, error);
            await _hooks.RaiseAfterToolCall(block.Name, block.Input, result, ok, cancellationToken);
            _context.AddMessage("tool_result", result, block.Id);
        }
    }

    private void RecordUsage(JsonNode response)
    {
        var usage = response["usage"];
        if (usage is null) return;
        var input = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var output = usage["output_tokens"]?.GetValue<int>() ?? 0;
        _context.AddTurnTokens(input, output);
        _context.UpdateTokens(input);
    }

    private void LogReasoning(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content.OfType<ReasoningBlock>())
        {
            if (!block.Redacted && string.IsNullOrEmpty(block.Text)) continue;
            _logger.Reasoning(block.Text, block.Redacted);
        }
    }

    private void LogResponse(string text, JsonNode response, string stopReason)
    {
        var usage = response["usage"] is JsonObject u ? JsonUtil.ToObject(u) as IReadOnlyDictionary<string, object?> : null;
        double? cost = null;
        if (usage is not null
            && usage.TryGetValue("input_tokens", out var i)
            && usage.TryGetValue("output_tokens", out var o)
            && i is not null && o is not null)
        {
            cost = _builder.Backend.EstimateCost(Convert.ToInt32(i), Convert.ToInt32(o));
        }
        _logger.Response(text, usage, stopReason, _context.Task.TaskName, BackendName(), cost);
    }

    private string BackendName() =>
        System.Text.RegularExpressions.Regex
            .Replace(_builder.Backend.GetType().Name.Replace("Backend", ""), "([a-z0-9])([A-Z])", "$1_$2")
            .ToLowerInvariant();

    private static string ExtractText(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextBlock>().Select(b => b.Text));
}
