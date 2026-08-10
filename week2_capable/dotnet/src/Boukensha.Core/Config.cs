using YamlDotNet.Serialization;

namespace Boukensha.Core;

public sealed class Config
{
    public static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".boukensha");

    public static readonly string PromptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

    public string Dir { get; }
    public IReadOnlyDictionary<string, object?> Settings { get; }

    public Config()
    {
        var envDir = Environment.GetEnvironmentVariable("BOUKENSHA_DIR");
        Dir = Path.GetFullPath(string.IsNullOrEmpty(envDir) ? DefaultDir : envDir).Replace('\\', '/');

        LoadDotEnv(Path.Combine(Dir, ".env"));

        var settingsPath = Path.Combine(Dir, "settings.yaml");
        Settings = File.Exists(settingsPath) ? LoadYaml(settingsPath) : new Dictionary<string, object?>();
    }

    public IReadOnlyDictionary<string, object?>? Tasks(string? name = null)
    {
        var allTasks = Dig("tasks") as IReadOnlyDictionary<string, object?>;
        if (name is null) return allTasks;
        return allTasks is not null && allTasks.TryGetValue(name, out var task)
            ? task as IReadOnlyDictionary<string, object?>
            : null;
    }

    public string UserPromptsDir => Path.Combine(Dir, "prompts");

    public IReadOnlyDictionary<string, object?> McpServers =>
        Dig("mcp_servers") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();

    public int AgentMaxTurnTokens => Convert.ToInt32(Dig("agent", "max_turn_tokens") ?? 60_000);

    public double AgentCompactionThreshold => Convert.ToDouble(Dig("agent", "compaction_threshold") ?? 0.85);

    public int AgentExplorationMaxSteps => Convert.ToInt32(Dig("agent", "exploration_max_steps") ?? 30);

    public double AgentExplorationConfidenceThreshold => Convert.ToDouble(Dig("agent", "exploration_confidence_threshold") ?? 0.5);

    public object? Dig(params string[] keys)
    {
        object? current = Settings;
        foreach (var key in keys)
        {
            if (current is not IReadOnlyDictionary<string, object?> dict || !dict.TryGetValue(key, out current))
            {
                return null;
            }
        }
        return current;
    }

    public override string ToString() => $"#<Boukensha::Config dir={Dir} tasks={Tasks()?.Count ?? 0}>";

    private static void LoadDotEnv(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static Dictionary<string, object?> LoadYaml(string path)
    {
        var deserializer = new DeserializerBuilder().Build();
        var raw = deserializer.Deserialize<object?>(File.ReadAllText(path));
        return Normalize(raw) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    private static object? Normalize(object? node) => node switch
    {
        null => null,
        Dictionary<object, object> map => map.ToDictionary(
            kv => kv.Key?.ToString() ?? string.Empty,
            kv => Normalize(kv.Value)),
        List<object> list => list.Select(Normalize).ToList(),
        _ => node,
    };
}
