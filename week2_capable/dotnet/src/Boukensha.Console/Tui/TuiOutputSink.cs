using Boukensha.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Boukensha.Console.Tui;

public sealed class TuiOutputSink(Context context) : IDisposable
{
    private readonly List<string> _transcript = [];
    private readonly Lock _lock = new();
    private LiveDisplayContext? _liveContext;

    public void Start()
    {
        _ = AnsiConsole.Live(BuildLayout()).StartAsync(ctx =>
        {
            _liveContext = ctx;
            ctx.Refresh();
            return Task.CompletedTask;
        });
    }

    public void Output(string text)
    {
        lock (_lock)
        {
            _transcript.Add(text);
            if (_transcript.Count > 200) _transcript.RemoveAt(0);
        }
        Refresh();
    }

    public void OnLogEvent(IReadOnlyDictionary<string, object?> evt)
    {
        if (evt.TryGetValue("phase", out var phase) && phase as string == "compaction")
        {
            Output($"[grey][[context compacted — {evt["dropped"]} messages dropped to free space]][/]");
        }
        else
        {
            Refresh();
        }
    }

    public void Dispose() { /* AnsiConsole.Live tears itself down when StartAsync's callback returns */ }

    private void Refresh()
    {
        _liveContext?.UpdateTarget(BuildLayout());
        _liveContext?.Refresh();
    }

    private IRenderable BuildLayout()
    {
        var usagePct = context.UsagePct;
        var color = usagePct >= 85 ? "red" : usagePct >= 70 ? "yellow" : "grey";
        var gauge = new Panel(new Markup($"[{color}]context: {usagePct}% ({context.CurrentTokens}/{context.ContextWindow})[/]"))
            .Header("status");

        string transcriptText;
        lock (_lock) transcriptText = string.Join('\n', _transcript.TakeLast(40));

        var layout = new Layout("root").SplitRows(
            new Layout("conversation", new Panel(new Markup(Markup.Escape(transcriptText))).Header("boukensha")).Ratio(5),
            new Layout("status", gauge).Size(3));
        return layout;
    }
}
