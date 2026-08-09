namespace Boukensha.Core;

public sealed record ToolParameter(string Type, string? Description = null);

public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, ToolParameter> Parameters,
    Func<IReadOnlyDictionary<string, object?>, Task<string>> Handler);
