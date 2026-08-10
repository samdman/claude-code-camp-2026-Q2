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
