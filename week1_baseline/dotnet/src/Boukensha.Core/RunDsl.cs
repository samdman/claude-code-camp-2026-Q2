namespace Boukensha.Core;

public sealed class RunDsl(Registry registry)
{
    public ToolDefinition Tool(
        string name,
        string description,
        IReadOnlyDictionary<string, ToolParameter>? parameters,
        Func<IReadOnlyDictionary<string, object?>, Task<string>> handler) =>
        registry.Tool(name, description, parameters, handler);
}
