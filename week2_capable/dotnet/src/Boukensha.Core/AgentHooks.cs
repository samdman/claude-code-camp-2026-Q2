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
