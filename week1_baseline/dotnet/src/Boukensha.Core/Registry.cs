namespace Boukensha.Core;

public sealed class Registry(Context context)
{
    public ToolDefinition Tool(
        string name,
        string description,
        IReadOnlyDictionary<string, ToolParameter>? parameters,
        Func<IReadOnlyDictionary<string, object?>, Task<string>> handler)
    {
        var tool = new ToolDefinition(name, description, parameters ?? new Dictionary<string, ToolParameter>(), handler);
        context.RegisterTool(tool);
        return tool;
    }

    public bool Registered(string name) => context.Tools.ContainsKey(name);

    public async Task<string> DispatchAsync(string name, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (!context.Tools.TryGetValue(name, out var tool))
        {
            throw new UnknownToolException($"unknown tool: {name}");
        }
        return await tool.Handler(args ?? new Dictionary<string, object?>());
    }
}
