namespace Boukensha.Core.Tasks;

public abstract class TaskBase : ITask
{
    private const int DefaultMaxIterations = 25;
    private const int DefaultMaxOutputTokens = 1024;

    public abstract string TaskName { get; }

    public string Provider(IReadOnlyDictionary<string, object?> settings) =>
        Fetch(settings, "provider") as string
        ?? throw new ArgumentException($"settings.yaml is missing tasks.{TaskName}.provider");

    public string Model(IReadOnlyDictionary<string, object?> settings) =>
        Fetch(settings, "model") as string
        ?? throw new ArgumentException($"settings.yaml is missing tasks.{TaskName}.model");

    public bool PromptOverride(IReadOnlyDictionary<string, object?> settings, string prompt = "system") =>
        Fetch(settings, "prompt_override") is IReadOnlyDictionary<string, object?> node
        && node.TryGetValue(prompt, out var value)
        && value is true;

    public string? Prompt(IReadOnlyDictionary<string, object?> settings, string name, string userPromptsDir, string defaultPromptsDir)
    {
        if (PromptOverride(settings, name))
        {
            var userPrompt = ReadFile(Path.Combine(userPromptsDir, TaskName, $"{name}.md"));
            if (userPrompt is not null) return userPrompt;
        }
        return ReadFile(Path.Combine(defaultPromptsDir, $"{name}.md"));
    }

    public string? SystemPrompt(IReadOnlyDictionary<string, object?> settings, string userPromptsDir, string defaultPromptsDir) =>
        Prompt(settings, "system", userPromptsDir, defaultPromptsDir);

    public int MaxIterations(IReadOnlyDictionary<string, object?> settings) =>
        IntegerSetting(settings, "max_iterations", DefaultMaxIterations);

    public int MaxOutputTokens(IReadOnlyDictionary<string, object?> settings) =>
        IntegerSetting(settings, "max_output_tokens", DefaultMaxOutputTokens);

    private static object? Fetch(IReadOnlyDictionary<string, object?> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : null;

    private static int IntegerSetting(IReadOnlyDictionary<string, object?> settings, string key, int defaultValue)
    {
        var value = Fetch(settings, key);
        return value is null ? defaultValue : Convert.ToInt32(value);
    }

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
