# Agent finish_task Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this session's established precedent.
>
> Spec: `docs/plans/week_2/specs/agent_finish_task.md`.

**Goal:** Make ending an `Agent` turn an explicit act — the loop only stops when the model calls a new `finish_task` tool (or an iteration/token budget forces `WrapUpAsync`, unchanged) — instead of stopping on any plain-text response, so the agent keeps working toward the user's goal instead of handing control back mid-task.

**Architecture:** `AgentHooks` gains an `OnNarration`/`RaiseNarration` event pair, following its existing `OnBeforeToolCall`/`OnAfterToolCall` shape. `Agent`'s constructor registers `finish_task` onto its `Registry` (guarded so it's idempotent), and `RunAsync`'s loop is restructured: a `finish_task` call in a `tool_use` response ends the loop with that call's `summary`; a plain-text response now raises the narration event and injects a nudge message instead of returning. `BoukenshaSession` exposes the shared `AgentHooks` so `Repl` can subscribe to narration and print it live.

**Tech Stack:** No new dependencies — pure C#/.NET additions to `Boukensha.Core`/`Boukensha.Console`. Tests use a hand-rolled `HttpMessageHandler` fake (no mocking library in this project) to script Anthropic-shaped JSON responses directly against `Client`, since this is the first test suite exercising `Agent.RunAsync` end-to-end.

## Global Constraints

- `max_iterations`/`max_turn_tokens` → `WrapUpAsync` remains the hard backstop, completely unchanged (`Agent.cs:57-66`, `103-125`) — it still calls the model once more with `tools: []` and returns plain text, regardless of `finish_task`.
- `finish_task` is registered by `Agent` itself (constructor-time, guarded by `Registry.Registered("finish_task")`), not by `BoukenshaHost` or any task — so it can never be missing and never double-registered.
- `HandleToolCallsAsync` (`Agent.cs:135-166`) is **not modified** — every `tool_use` block, including `finish_task`'s, still gets dispatched and gets a matching `tool_result`, satisfying the Anthropic API's per-turn contract.
- No settings.yaml toggle and no per-task opt-out — applies to every task built on `Agent.RunAsync` (see spec's "Scope").

---

## Task 1: `AgentHooks.OnNarration`/`RaiseNarration`

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/AgentHooks.cs`
- Test: `week2_capable/dotnet/tests/Boukensha.Core.Tests/AgentHooksTests.cs` (existing file — add a new `[Fact]`)

**Interfaces:**
- Produces: `AgentHooks.OnNarration(Func<string, CancellationToken, Task> handler) -> void`; `AgentHooks.RaiseNarration(string text, CancellationToken) -> Task`. Task 2 (`Agent`) and Task 3 (`Repl`) both consume these.

- [ ] **Step 1: Write the failing test**

Add to `week2_capable/dotnet/tests/Boukensha.Core.Tests/AgentHooksTests.cs`, immediately after `RaiseAfterToolCall_PassesNameArgsResultAndOk`:

```csharp
    [Fact]
    public async Task RaiseNarration_InvokesAllSubscribersWithText()
    {
        var hooks = new AgentHooks();
        var captured = new List<string>();
        hooks.OnNarration((text, _) => { captured.Add(text); return Task.CompletedTask; });

        await hooks.RaiseNarration("Still looking for the bakery...", CancellationToken.None);

        Assert.Equal(["Still looking for the bakery..."], captured);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RaiseNarration_InvokesAllSubscribersWithText`
Expected: build failure — `AgentHooks.OnNarration`/`RaiseNarration` don't exist yet.

- [ ] **Step 3: Implement `OnNarration`/`RaiseNarration`**

Modify `week2_capable/dotnet/src/Boukensha.Core/AgentHooks.cs` — replace the file in full:

```csharp
namespace Boukensha.Core;

public sealed class AgentHooks
{
    private readonly List<Func<Context, CancellationToken, Task>> _beforeAgentCall = [];
    private readonly List<Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task>> _beforeToolCall = [];
    private readonly List<Func<string, IReadOnlyDictionary<string, object?>, string, bool, CancellationToken, Task>> _afterToolCall = [];
    private readonly List<Func<string, CancellationToken, Task>> _narration = [];

    public void OnBeforeAgentCall(Func<Context, CancellationToken, Task> handler) => _beforeAgentCall.Add(handler);

    public void OnBeforeToolCall(Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task> handler) => _beforeToolCall.Add(handler);

    public void OnAfterToolCall(Func<string, IReadOnlyDictionary<string, object?>, string, bool, CancellationToken, Task> handler) => _afterToolCall.Add(handler);

    public void OnNarration(Func<string, CancellationToken, Task> handler) => _narration.Add(handler);

    public async Task RaiseBeforeAgentCall(Context context, CancellationToken cancellationToken)
    {
        foreach (var handler in _beforeAgentCall) await handler(context, cancellationToken);
    }

    public async Task RaiseBeforeToolCall(string name, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        foreach (var handler in _beforeToolCall) await handler(name, args, cancellationToken);
    }

    public async Task RaiseAfterToolCall(string name, IReadOnlyDictionary<string, object?> args, string result, bool ok, CancellationToken cancellationToken)
    {
        foreach (var handler in _afterToolCall) await handler(name, args, result, ok, cancellationToken);
    }

    public async Task RaiseNarration(string text, CancellationToken cancellationToken)
    {
        foreach (var handler in _narration) await handler(text, cancellationToken);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AgentHooksTests`
Expected: all 4 tests pass (3 existing + 1 new).

- [ ] **Step 5: Commit** — deferred to the final batched commit (Task 4), per this session's established cadence.

---

## Task 2: `Agent` — `finish_task` tool and loop restructuring

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/Agent.cs` (full rewrite)
- Test: Create `week2_capable/dotnet/tests/Boukensha.Core.Tests/AgentTests.cs`

**Interfaces:**
- Consumes: `AgentHooks.OnNarration`/`RaiseNarration` (Task 1); `Registry.Registered`/`Tool`/`DispatchAsync` (existing); `Client.CallAsync` (existing); `Context.AddMessage` (existing).
- Produces: no new public API on `Agent` beyond existing (`RunAsync` signature unchanged) — the behavior change is internal. Task 3 (`BoukenshaHost`/`Repl`) doesn't call anything new on `Agent` itself, only on `AgentHooks` (Task 1) via the session.

### Step 1: Write the failing tests

Create `week2_capable/dotnet/tests/Boukensha.Core.Tests/AgentTests.cs`. This is the first test suite exercising `Agent.RunAsync` end-to-end, so it includes a small fake `HttpMessageHandler` that returns scripted Anthropic-shaped JSON response bodies in order, and a `NewAgent` fixture wiring the real `AnthropicBackend`/`Context`/`Registry`/`PromptBuilder`/`Client`/`Logger` around it — the same "fake the external boundary, keep everything else real" discipline this session's other test suites already use.

```csharp
using System.Net;
using System.Text;
using Boukensha.Core;
using Boukensha.Core.Backends;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests;

public class AgentTests
{
    private sealed class FakeHandler(Queue<string> responses) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (responses.Count == 0)
            {
                throw new InvalidOperationException($"no scripted response left for call #{CallCount}");
            }
            var body = responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (Agent Agent, FakeHandler Handler, Context Context, Registry Registry, AgentHooks Hooks) NewAgent(
        IEnumerable<string> responses, int maxIterations = 10)
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), "system prompt", backend.ContextWindow);
        var registry = new Registry(context);
        var builder = new PromptBuilder(context, backend);
        var handler = new FakeHandler(new Queue<string>(responses));
        var client = new Client(builder, new HttpClient(handler));
        var logger = new Logger(Directory.CreateTempSubdirectory("boukensha_agent_test").FullName, sessionId: "test");
        var hooks = new AgentHooks();

        var agent = new Agent(context, registry, builder, client, logger, maxIterations: maxIterations, hooks: hooks);
        return (agent, handler, context, registry, hooks);
    }

    [Fact]
    public async Task RunAsync_FinishTaskCall_EndsLoopAndReturnsSummary_WithoutExtraModelCall()
    {
        var (agent, handler, _, _, _) = NewAgent([
            """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"finish_task","input":{"status":"done","summary":"Reached the bakery."}}],"usage":{"input_tokens":10,"output_tokens":5}}""",
        ]);

        var result = await agent.RunAsync();

        Assert.Equal("Reached the bakery.", result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunAsync_PlainTextWithoutFinishTask_DoesNotEndLoop_NarratesThenCallsModelAgain()
    {
        var (agent, handler, _, _, hooks) = NewAgent([
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"Still looking for the bakery..."}],"usage":{"input_tokens":8,"output_tokens":4}}""",
            """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"finish_task","input":{"status":"done","summary":"Found it."}}],"usage":{"input_tokens":10,"output_tokens":5}}""",
        ]);

        var narrated = new List<string>();
        hooks.OnNarration((text, _) => { narrated.Add(text); return Task.CompletedTask; });

        var result = await agent.RunAsync();

        Assert.Equal("Found it.", result);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(["Still looking for the bakery..."], narrated);
    }

    [Fact]
    public async Task RunAsync_PlainTextWithoutFinishTask_InjectsNudgeMessageBeforeNextCall()
    {
        var (agent, _, context, _, _) = NewAgent([
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"Still working on it."}],"usage":{"input_tokens":8,"output_tokens":4}}""",
            """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"finish_task","input":{"status":"done","summary":"Done."}}],"usage":{"input_tokens":10,"output_tokens":5}}""",
        ]);

        await agent.RunAsync();

        var narratedIndex = context.Messages.FindIndex(m => m.Role == "assistant" && m.Content.Text == "Still working on it.");
        Assert.True(narratedIndex >= 0, "expected the narrated text to have been added as an assistant message");
        var nudge = context.Messages[narratedIndex + 1];
        Assert.Equal("user", nudge.Role);
        Assert.Contains("finish_task", nudge.Content.Text);
    }

    [Fact]
    public async Task RunAsync_FinishTaskAlongsideOtherToolCalls_DispatchesAllAndEndsOnSummary()
    {
        var (agent, handler, context, registry, _) = NewAgent([
            """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"look","input":{}},{"type":"tool_use","id":"t2","name":"finish_task","input":{"status":"done","summary":"All done."}}],"usage":{"input_tokens":10,"output_tokens":5}}""",
        ]);
        registry.Tool("look", "look", null, _ => Task.FromResult("You see a room."));

        var result = await agent.RunAsync();

        Assert.Equal("All done.", result);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains(context.Messages, m => m.Role == "tool_result" && m.ToolUseId == "t1" && m.Content.Text == "You see a room.");
        Assert.Contains(context.Messages, m => m.Role == "tool_result" && m.ToolUseId == "t2");
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("need_input")]
    public async Task RunAsync_FinishTaskNonDoneStatus_EndsLoopSameAsDone(string status)
    {
        // Plain (non-interpolated) raw string + .Replace(), not $$"""...""" interpolation --
        // this JSON's trailing "}}" (closing "usage" then the outer object) is exactly the
        // kind of doubled-brace run that $$-style raw string interpolation would try to parse
        // as an interpolation hole, even though none is open there.
        var response = """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"finish_task","input":{"status":"STATUS_PLACEHOLDER","summary":"Reporting status."}}],"usage":{"input_tokens":10,"output_tokens":5}}"""
            .Replace("STATUS_PLACEHOLDER", status);

        var (agent, handler, _, _, _) = NewAgent([response]);

        var result = await agent.RunAsync();

        Assert.Equal("Reporting status.", result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunAsync_MaxIterationsReached_ForcesWrapUpTermination_EvenWithoutFinishTask()
    {
        var (agent, handler, _, _, _) = NewAgent([
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"Working on it (1)."}],"usage":{"input_tokens":8,"output_tokens":4}}""",
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"Working on it (2)."}],"usage":{"input_tokens":8,"output_tokens":4}}""",
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"I ran out of budget -- here's what I found."}],"usage":{"input_tokens":8,"output_tokens":4}}""",
        ], maxIterations: 2);

        var result = await agent.RunAsync();

        Assert.Equal("I ran out of budget -- here's what I found.", result);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public void Constructor_FinishTaskAlreadyRegistered_DoesNotThrowOrDoubleRegister()
    {
        var (_, _, _, registry, _) = NewAgent([]);
        Assert.True(registry.Registered("finish_task"));

        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), "system prompt", backend.ContextWindow);
        var builder = new PromptBuilder(context, backend);
        var client = new Client(builder, new HttpClient(new FakeHandler(new Queue<string>())));
        var logger = new Logger(Directory.CreateTempSubdirectory("boukensha_agent_test").FullName, sessionId: "test");
        var registryAlreadyHasIt = new Registry(context);
        registryAlreadyHasIt.Tool("finish_task", "pre-existing", null, _ => Task.FromResult("pre-existing"));

        var exception = Record.Exception(() => new Agent(context, registryAlreadyHasIt, builder, client, logger, maxIterations: 5));

        Assert.Null(exception);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AgentTests`
Expected: `RunAsync_FinishTaskCall_EndsLoopAndReturnsSummary_WithoutExtraModelCall` and similar fail because `finish_task` isn't registered yet (the model's `tool_use` call to it throws `UnknownToolException` from `Registry.DispatchAsync`, surfaced as a tool error result, and the loop then calls the model again rather than stopping) — confirming the current "any plain text ends the turn" behavior is what's under test.

### Step 2: Implement the loop restructuring

Replace `week2_capable/dotnet/src/Boukensha.Core/Agent.cs` in full:

```csharp
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Boukensha.Core;

public sealed class Agent
{
    private const int DefaultMaxIterations = 25;
    private const int WrapUpOutputTokens = 400;
    private const string FinishTaskToolName = "finish_task";
    private const string WrapUpDirective =
        "You are almost out of iterations or context budget for this turn. " +
        "Stop calling tools now and give the user your best final answer based on what you've learned so far.";
    private const string NudgeDirective =
        "Continue working toward the goal, or call finish_task if you're done, blocked, or need my input.";

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

        if (!_registry.Registered(FinishTaskToolName))
        {
            _registry.Tool(FinishTaskToolName,
                "Call this to end your turn. Use status=done once you've completed the user's request, " +
                "status=blocked if you've genuinely tried and cannot proceed, or status=need_input if you need " +
                "a decision or missing detail from the user before you can continue. Plain text alone does not " +
                "end your turn -- you must call this tool. The summary becomes your final reply to the user.",
                new Dictionary<string, ToolParameter>
                {
                    ["status"] = new("string", "One of: done, blocked, need_input"),
                    ["summary"] = new("string", "The final message to show the user"),
                },
                _ => Task.FromResult("Acknowledged."));
        }
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

            var stopwatch = Stopwatch.StartNew();
            var response = await _client.CallAsync(_maxOutputTokens ?? 1024, cancellationToken: cancellationToken);
            stopwatch.Stop();
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            LogReasoning(parsed.Content);

            if (parsed.StopReason == "tool_use")
            {
                var finishCall = parsed.Content.OfType<ToolUseBlock>().FirstOrDefault(b => b.Name == FinishTaskToolName);
                await HandleToolCallsAsync(parsed.Content, cancellationToken);

                if (finishCall is not null)
                {
                    var summary = finishCall.Input.GetValueOrDefault("summary") as string ?? "(no summary provided)";
                    var status = finishCall.Input.GetValueOrDefault("status") as string ?? "done";
                    _logger.TurnEnd($"finish_task:{status}", _iteration, _context.TurnTokens);
                    return summary;
                }

                continue;
            }

            var text = ExtractText(parsed.Content);
            if (string.IsNullOrWhiteSpace(text)) text = FallbackMessage("empty_response");
            LogResponse(text, response, parsed.StopReason, (int)stopwatch.ElapsedMilliseconds);
            _context.AddMessage("assistant", text);
            await _hooks.RaiseNarration(text, cancellationToken);
            _context.AddMessage("user", NudgeDirective);
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
            var stopwatch = Stopwatch.StartNew();
            var response = await _client.CallAsync(WrapUpOutputTokens, tools: [], cancellationToken: cancellationToken);
            stopwatch.Stop();
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            text = ExtractText(parsed.Content);
            if (string.IsNullOrWhiteSpace(text)) text = FallbackMessage(reason);
            LogResponse(text, response, parsed.StopReason, (int)stopwatch.ElapsedMilliseconds);
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
        "empty_response" => "(no response)",
        _ => "I had to stop before finishing this turn.",
    };

    private async Task HandleToolCallsAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        var preamble = ExtractText(content);
        if (!string.IsNullOrWhiteSpace(preamble)) _logger.Plan(preamble);

        _context.AddMessage("assistant", content);

        foreach (var block in content.OfType<ToolUseBlock>())
        {
            _logger.ToolCall(block.Name, block.Input, _context.Task.TaskName);
            await _hooks.RaiseBeforeToolCall(block.Name, block.Input, cancellationToken);

            var stopwatch = Stopwatch.StartNew();
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
            stopwatch.Stop();
            _logger.ToolResult(block.Name, result, _context.Task.TaskName, (int)stopwatch.ElapsedMilliseconds, ok, error);
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

    private void LogResponse(string text, JsonNode response, string stopReason, int durationMs)
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
        _logger.Response(text, usage, stopReason, _context.Task.TaskName, BackendName(), cost, durationMs);
    }

    private string BackendName() =>
        System.Text.RegularExpressions.Regex
            .Replace(_builder.Backend.GetType().Name.Replace("Backend", ""), "([a-z0-9])([A-Z])", "$1_$2")
            .ToLowerInvariant();

    private static string ExtractText(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextBlock>().Select(b => b.Text));
}
```

(Only three things actually changed from the original: the constructor now registers `finish_task`; the `tool_use` branch now detects and ends on a `finish_task` call; the plain-text branch narrates and nudges instead of returning. Everything else — `WrapUpAsync`, `HandleToolCallsAsync`, logging helpers — is copied unchanged.)

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AgentTests`
Expected: all 7 tests pass.

- [ ] **Step 4: Verify no regressions in the rest of the suite**

Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests`
Expected: full suite passes — nothing else calls `Agent` directly in existing tests, but this confirms the `Agent.cs` rewrite didn't break compilation anywhere else.

- [ ] **Step 5: Commit** — deferred to the final batched commit (Task 4).

---

## Task 3: Wire narration into `BoukenshaSession`/`Repl`, update the system prompt

**Files:**
- Modify: `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Console/Repl.cs`
- Modify: `week2_capable/dotnet/src/Boukensha.Core/prompts/system.md`

**Interfaces:**
- Consumes: `AgentHooks.OnNarration` (Task 1).
- Produces: `BoukenshaSession.Hooks -> AgentHooks` (new public property). No test file — this is composition-root wiring exercised by the manual live-verification step below, matching how `BoukenshaHost.cs` wiring has been verified in prior sub-projects this session (no dedicated `BoukenshaHostTests`).

- [ ] **Step 1: Expose `Hooks` on `BoukenshaSession`**

Modify `week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs` — add `AgentHooks hooks` to the primary constructor parameter list and a public property, immediately after the existing `Knowledge.KnowledgeStore knowledgeStore` parameter:

```csharp
public sealed class BoukenshaSession(
    Context context,
    Registry registry,
    Func<Agent> agentFactory,
    Logger logger,
    IReadOnlyList<McpClient> mcpClients,
    string provider,
    string model,
    Knowledge.KnowledgeStore knowledgeStore,
    AgentHooks hooks) : IAsyncDisposable
{
    public Context Context { get; } = context;
    public Registry Registry { get; } = registry;
    public Func<Agent> AgentFactory { get; } = agentFactory;
    public Logger Logger { get; } = logger;
    public string Provider { get; } = provider;
    public string Model { get; } = model;
    public IReadOnlyList<string> McpServerNames { get; } = mcpClients.Select(c => c.Name).ToList();
    public Knowledge.KnowledgeStore Knowledge { get; } = knowledgeStore;
    public AgentHooks Hooks { get; } = hooks;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in mcpClients) await client.DisposeAsync();
        Logger.Dispose();
        Knowledge.Dispose();
    }
}
```

Update the return statement at the end of `BuildAsync` (currently `return new BoukenshaSession(context, registry, AgentFactory, logger, mcpClients, backendName, model, knowledgeStore);`):

```csharp
        return new BoukenshaSession(context, registry, AgentFactory, logger, mcpClients, backendName, model, knowledgeStore, agentHooks);
```

- [ ] **Step 2: Subscribe to narration once in `Repl.StartAsync`**

Modify `week2_capable/dotnet/src/Boukensha.Console/Repl.cs` — add a subscription at the top of `StartAsync`, before the banner is printed:

```csharp
    public async Task StartAsync()
    {
        session.Hooks.OnNarration((text, _) => { Output(text); return Task.CompletedTask; });
        Output(Banner());
        while (true)
        {
```

(Subscribing here — not in `RunTurnAsync`, which runs once per user input line — is what keeps this a one-time subscription; `AgentHooks` has no unsubscribe, so subscribing per-turn would print every prior turn's narration again on each new turn.)

- [ ] **Step 3: Update the system prompt**

Modify `week2_capable/dotnet/src/Boukensha.Core/prompts/system.md` — append a paragraph:

```markdown
You are Boukensha, an autonomous agent playing a MUD (multi-user dungeon).

Use the tools available to you to explore, navigate, and interact with the
world. Prefer inspecting a room fully before moving on, and be concise in
your final answers to the user.

When you are done working on the user's request, call finish_task to end
your turn -- plain text alone does not end it. Use status=done once you've
completed the request, status=blocked if you've genuinely tried and cannot
proceed, or status=need_input if you need a decision or missing detail from
the user before continuing. The summary you provide becomes your reply.
```

- [ ] **Step 4: Verify the full solution builds**

Run: `dotnet build week2_capable/dotnet/Boukensha.slnx`
Expected: success, no new warnings.

- [ ] **Step 5: Commit** — deferred to the final batched commit (Task 4).

---

## Task 4: Full-suite verification and commit

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test week2_capable/dotnet/Boukensha.slnx`
Expected: all tests pass — the pre-existing suite plus this plan's additions (1 `AgentHooksTests` + 7 `AgentTests`, all new).

- [ ] **Step 2: Manual live verification**

Run the console app against the live MUD and give it a multi-step goal it's likely to narrate partway through (e.g. "go to the bakery" from a room with unexplored territory, same scenario the base exploration feature was verified against). Confirm:
- The agent keeps working (you see narration printed, then further tool calls) instead of handing control back after the first plain-text response.
- It eventually prints a real final answer once it calls `finish_task`, and the session log's `turn_end` event shows a `finish_task:<status>` reason.
- If you interrupt it with an unrelated, trivially-answerable question, it still calls `finish_task` (with `status=done`) rather than looping forever on something it could answer immediately — confirms the nudge/prompt wording doesn't make trivial turns unnecessarily expensive.

- [ ] **Step 3: Update spec status**

Modify `docs/plans/week_2/specs/agent_finish_task.md` line 3 — change `Status: draft` to `Status: implemented (pending live verification)` once Steps 1–2 are green, following this session's established spec-status convention.

- [ ] **Step 4: Commit**

```bash
git add week2_capable/dotnet/src/Boukensha.Core/AgentHooks.cs \
        week2_capable/dotnet/src/Boukensha.Core/Agent.cs \
        week2_capable/dotnet/src/Boukensha.Core/BoukenshaHost.cs \
        week2_capable/dotnet/src/Boukensha.Core/prompts/system.md \
        week2_capable/dotnet/src/Boukensha.Console/Repl.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/AgentHooksTests.cs \
        week2_capable/dotnet/tests/Boukensha.Core.Tests/AgentTests.cs \
        docs/plans/week_2/specs/agent_finish_task.md \
        docs/plans/week_2/plans/agent_finish_task_plan.md
git commit -m "$(cat <<'EOF'
dotnet: require finish_task to end an agent turn

Agent.RunAsync no longer treats a plain-text response as the end of a
turn. Ending now requires an explicit finish_task tool call (status:
done/blocked/need_input + summary), registered automatically by Agent
itself so it can never be missing. A plain-text response instead
narrates (via a new AgentHooks.OnNarration event, surfaced live in
Repl) and nudges the loop to continue. max_iterations/max_turn_tokens
still force WrapUpAsync as the hard backstop, unchanged.
EOF
)"
```

- [ ] **Step 5: Verify**

Run: `git status`
Expected: clean working tree.

---

## Notes for the implementer

- If live verification (Task 4, Step 2) finds the model frequently forgets to call `finish_task` even with the system-prompt addition, the fix is prompt wording, not loop logic — the loop already bounds worst-case behavior via `max_iterations` regardless.
- `Boukensha.Console`'s TUI mode (`TuiOutputSink`, wired via `session.Logger.Subscribe`) already streams every logged response phase, narrated or not. Once this ships, check live whether TUI mode ends up showing narrated text twice (once via the log stream, once via the new `OnNarration` → `Output` path) — if so, that's a small follow-up to `TuiOutputSink`, not something this plan's scope covers.
