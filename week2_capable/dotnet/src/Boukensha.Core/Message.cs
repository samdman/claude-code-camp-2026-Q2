namespace Boukensha.Core;

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, IReadOnlyDictionary<string, object?> Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content) : ContentBlock;

public sealed record ReasoningBlock(string Text, bool Redacted = false, string? Signature = null) : ContentBlock;

public sealed class MessageContent
{
    public string? Text { get; }
    public IReadOnlyList<ContentBlock>? Blocks { get; }
    public bool IsText => Text is not null;

    private MessageContent(string? text, IReadOnlyList<ContentBlock>? blocks)
    {
        Text = text;
        Blocks = blocks;
    }

    public static MessageContent Of(string text) => new(text, null);
    public static MessageContent Of(IReadOnlyList<ContentBlock> blocks) => new(null, blocks);
}

public sealed record Message(string Role, MessageContent Content, string? ToolUseId = null);
