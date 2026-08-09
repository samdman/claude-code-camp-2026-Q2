using Boukensha.Core.Tasks;

namespace Boukensha.Core;

public sealed class Context
{
    public ITask Task { get; }
    public string? System { get; }
    public int ContextWindow { get; }
    public string? WorkingDir { get; }
    public double CompactionThreshold { get; }
    public List<Message> Messages { get; } = [];
    public Dictionary<string, ToolDefinition> Tools { get; } = [];
    public int CurrentTokens { get; private set; }
    public int TurnTokens { get; private set; }

    public Context(ITask task, string? system = null, int contextWindow = 200_000, string? workingDir = null, double compactionThreshold = 0.85)
    {
        Task = task;
        System = system;
        ContextWindow = contextWindow;
        WorkingDir = string.IsNullOrEmpty(workingDir) ? null : Path.GetFullPath(workingDir);
        CompactionThreshold = compactionThreshold;
    }

    public int ToolCount => Tools.Count;
    public double UsageFraction => ContextWindow <= 0 ? 0.0 : (double)CurrentTokens / ContextWindow;
    public int UsagePct => (int)Math.Round(UsageFraction * 100);

    public void RegisterTool(ToolDefinition tool) => Tools[tool.Name] = tool;

    public void AddMessage(string role, string content, string? toolUseId = null) =>
        Messages.Add(new Message(role, MessageContent.Of(content), toolUseId));

    public void AddMessage(string role, IReadOnlyList<ContentBlock> content, string? toolUseId = null) =>
        Messages.Add(new Message(role, MessageContent.Of(content), toolUseId));

    public void UpdateTokens(int tokens) => CurrentTokens = tokens;

    public void ResetTurnTokens() => TurnTokens = 0;

    public void AddTurnTokens(int inputTokens, int outputTokens) => TurnTokens += inputTokens + outputTokens;

    public bool NeedsCompaction(double? threshold = null) => UsageFraction >= (threshold ?? CompactionThreshold);

    public int CompactMessages()
    {
        var dropCount = Math.Min((int)Math.Ceiling(Messages.Count * 0.40), Math.Max(Messages.Count - 2, 0));
        if (dropCount > 0)
        {
            Messages.RemoveRange(0, dropCount);
        }
        CurrentTokens = 0;
        return dropCount;
    }

    public void ClearMessages()
    {
        Messages.Clear();
        CurrentTokens = 0;
    }

    public override string ToString() =>
        $"#<Context task={Task.TaskName} messages={Messages.Count} tools={Tools.Count} usage={UsagePct}%>";
}
