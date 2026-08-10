using Boukensha.Core.Backends;
using Boukensha.Core.Mcp;
using Boukensha.Core.Tasks;

namespace Boukensha.Core;

public sealed record BoukenshaOptions(
    string? System = null,
    string? Model = null,
    string? Backend = null,
    string? ApiKey = null,
    string? Log = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    string? WorkingDir = null,
    bool DisableWorkingDir = false,
    Action<RunDsl>? Configure = null);

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

public static class BoukenshaHost
{
    public static async Task<BoukenshaSession> BuildAsync(BoukenshaOptions options, CancellationToken cancellationToken = default)
    {
        var config = new Config();
        ITask task = new PlayerTask();
        var taskSettings = config.Tasks(task.TaskName)
            ?? throw new ArgumentException($"settings.yaml has no tasks.{task.TaskName} entry");

        var system = options.System ?? task.SystemPrompt(taskSettings, config.UserPromptsDir, Config.PromptsDir);
        var model = options.Model ?? task.Model(taskSettings);
        var backendName = options.Backend ?? task.Provider(taskSettings);
        var apiKey = options.ApiKey ?? ResolveApiKey(backendName);

        ILlmBackend backend = backendName switch
        {
            "anthropic" => new AnthropicBackend(apiKey ?? throw new ArgumentException("ANTHROPIC_API_KEY is not set"), model),
            _ => throw new ArgumentException($"unsupported backend '{backendName}' (only 'anthropic' is ported so far)"),
        };

        var contextWindow = options.ContextWindow ?? backend.ContextWindow;
        var workingDir = options.DisableWorkingDir ? null : (options.WorkingDir ?? Directory.GetCurrentDirectory());

        var context = new Context(task, system, contextWindow, workingDir, config.AgentCompactionThreshold);
        var registry = new Registry(context);

        var sessionId = Logger.GenerateSessionId();

        var logger = new Logger(Path.Combine(config.Dir, "sessions"), sessionId: sessionId, log: options.Log, snapshot: new Dictionary<string, object?>
        {
            ["task"] = task.TaskName,
            ["provider"] = backendName,
            ["model"] = model,
            ["context_window"] = contextWindow,
            ["max_turn_tokens"] = config.AgentMaxTurnTokens,
            ["system"] = system,
        });

        var knowledgeStore = new Knowledge.KnowledgeStore(Path.Combine(config.Dir, "knowledge.db"), sessionId: sessionId);
        var agentHooks = new AgentHooks();
        Knowledge.KnowledgeHooks.Register(agentHooks, knowledgeStore);

        var routePlanner = new Knowledge.RoutePlanner(knowledgeStore);
        var explorationPlanner = new Knowledge.ExplorationPlanner(knowledgeStore, registry, agentHooks, logger);
        registry.Tool("plan_route",
            "Find a route between two previously-visited rooms by name. If 'from' is omitted, plans from your " +
            "current location, automatically exploring unmapped territory if the destination isn't known yet -- " +
            "if the result says 'still exploring', call plan_route again with the same destination to continue. " +
            "Returns step-by-step directions once found, or suggests unexplored exits if 'from' was given " +
            "explicitly and couldn't be resolved.",
            new Dictionary<string, ToolParameter>
            {
                ["destination"] = new("string", "Name of the destination room"),
                ["from"] = new("string", "Name of the starting room (optional -- defaults to your current location)"),
            },
            async args =>
            {
                var destination = args.GetValueOrDefault("destination") as string ?? "";
                var from = args.GetValueOrDefault("from") as string;
                var result = routePlanner.FindRoute(destination, from);
                if (!result.Found && from is null)
                {
                    result = await explorationPlanner.ExploreTowardsAsync(destination, config.AgentExplorationMaxSteps, config.AgentExplorationConfidenceThreshold);
                }
                return result.Message;
            });

        var mcpClients = new List<McpClient>();
        foreach (var (serverName, rawOptions) in config.McpServers)
        {
            if (rawOptions is not IReadOnlyDictionary<string, object?> serverConfig) continue;

            var command = serverConfig.GetValueOrDefault("command") as string
                ?? throw new ArgumentException($"mcp_servers.{serverName}.command is required");
            var args = (serverConfig.GetValueOrDefault("args") as IEnumerable<object?>)
                ?.Select(a => a?.ToString() ?? string.Empty).ToList();
            var env = (serverConfig.GetValueOrDefault("env") as IReadOnlyDictionary<string, object?>)
                ?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            var required = serverConfig.GetValueOrDefault("required") is not false;
            var prefix = serverConfig.GetValueOrDefault("prefix") as string;

            var client = new McpClient(serverName, command, args, env);
            try
            {
                await client.StartAsync(cancellationToken);
                await McpToolRegistrar.RegisterAsync(registry, client, prefix, cancellationToken);
                mcpClients.Add(client);
            }
            catch (McpClient.McpException) when (!required)
            {
                await Console.Error.WriteLineAsync($"[warning] MCP server '{serverName}' is unavailable and was skipped (not required)");
                await client.DisposeAsync();
            }
            catch
            {
                foreach (var started in mcpClients) await started.DisposeAsync();
                await client.DisposeAsync();
                logger.Dispose();
                knowledgeStore.Dispose();
                throw;
            }
        }

        options.Configure?.Invoke(new RunDsl(registry));
        logger.ToolCatalog(context.Tools);

        var builder = new PromptBuilder(context, backend);
        var httpClient = new HttpClient();
        var apiClient = new Client(builder, httpClient);
        var resolvedMaxOutputTokens = options.MaxOutputTokens ?? task.MaxOutputTokens(taskSettings);

        Agent AgentFactory() => new(
            context, registry, builder, apiClient, logger, taskSettings,
            maxOutputTokens: resolvedMaxOutputTokens,
            maxTurnTokens: config.AgentMaxTurnTokens,
            hooks: agentHooks);

        return new BoukenshaSession(context, registry, AgentFactory, logger, mcpClients, backendName, model, knowledgeStore, agentHooks);
    }

    private static string? ResolveApiKey(string backend) => backend switch
    {
        "anthropic" => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        "openai" => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        "gemini" => Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
        _ => Environment.GetEnvironmentVariable("OLLAMA_API_KEY"),
    };
}
