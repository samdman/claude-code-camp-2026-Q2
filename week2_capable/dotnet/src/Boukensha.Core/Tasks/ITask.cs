namespace Boukensha.Core.Tasks;

public interface ITask
{
    string TaskName { get; }
    string Provider(IReadOnlyDictionary<string, object?> settings);
    string Model(IReadOnlyDictionary<string, object?> settings);
    bool PromptOverride(IReadOnlyDictionary<string, object?> settings, string prompt = "system");
    string? Prompt(IReadOnlyDictionary<string, object?> settings, string name, string userPromptsDir, string defaultPromptsDir);
    string? SystemPrompt(IReadOnlyDictionary<string, object?> settings, string userPromptsDir, string defaultPromptsDir);
    int MaxIterations(IReadOnlyDictionary<string, object?> settings);
    int MaxOutputTokens(IReadOnlyDictionary<string, object?> settings);
}
