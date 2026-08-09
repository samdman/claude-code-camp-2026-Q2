# Boukensha .NET Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline execution) — matches this project's established precedent (see `docs/plans/python_port/IMPLEMENTATION.md`'s "Execution Workflow Notes": user prefers direct in-session execution over subagent-driven round trips, and does not want a commit after every task).

**Goal:** Build `week2_capable/dotnet/` — a .NET 10/C# 14 port of `boukensha` at capability parity with `python/12_context` (MCP tool-hosting + Tasks + context/token management), per the design in `docs/plans/week_2/dotnet_port.md`.

**Architecture:** `Boukensha.Core` class library (agent loop, context, MCP client, Anthropic backend, config) + `Boukensha.Console` host (REPL + optional Spectre.Console TUI) + `Boukensha.Core.Tests` (xUnit, covering `Context` compaction, `Registry` dispatch, MCP JSON-RPC framing, and `AnthropicBackend` payload/parse round-tripping).

**Tech Stack:** .NET 10 SDK (confirmed installed: 10.0.302), C# 14, YamlDotNet (settings.yaml), Spectre.Console (TUI), xUnit (tests). No MCP SDK, no Polly — hand-rolled per the design's dependency-minimalism decision.

## Global Constraints

- Target framework: `net10.0` everywhere (library, console, tests).
- `Nullable` and `ImplicitUsings` enabled on every project (template default — verify, don't disable).
- Only Anthropic backend ships this pass; `ILlmBackend` must stay a real interface so OpenAI/Gemini/Ollama/OllamaCloud can be added later without touching `Agent`/`PromptBuilder`.
- `.boukensha/settings.yaml` / `.env` format must be read as-is (shared config directory across Ruby/Python/.NET) — no `appsettings.json` fork.
- No dependency beyond YamlDotNet + Spectre.Console + xUnit; everything else (JSON, HTTP, process spawning) is BCL-only.
- MCP JSON-RPC framing is newline-delimited JSON (not LSP `Content-Length` framing), matching Ruby/Python.
- Verification bar is functional parity, not byte-for-byte transcript diffing.

---

## Decisions logged during execution (per user: pick the most recommended approach, log it here)

- **`Tool` renamed to `ToolDefinition`** — `Registry.Tool(...)`/`RunDsl.Tool(...)` (PascalCased from Ruby/Python's `tool(...)`) would otherwise collide with a type of the same name inside the same class (C# resolves an unqualified `Tool` to the method group first). Documented in the design doc's mapping table too.
- **`Message.Content` modeled as a small `MessageContent` wrapper** around either a `string` or `IReadOnlyList<ContentBlock>`, with a `ContentBlock` discriminated union (`TextBlock`/`ToolUseBlock`/`ToolResultBlock`/`ReasoningBlock`) — replaces Python's loose `str | list[dict]`.
- **`Tasks::Base`'s classmethods-over-a-dict become instance methods on `ITask`/`TaskBase`**, taking `IReadOnlyDictionary<string, object?> settings` explicitly — closer fit for C# than Python's duck-typed classmethod pattern.
- **`models.py`'s standalone table is dropped** — each `ILlmBackend` owns its own model catalog exclusively, fixing the two-sources-of-truth smell flagged in research (Python's `models.py` disagreed with `backends/anthropic.py` on Sonnet/Opus context window).
- **MCP JSON-RPC framing/parsing pulled into a pure static `JsonRpc` class**, separate from `McpClient`'s process-orchestration code — makes the framing logic unit-testable without mocking a `Process`, satisfying the design doc's "MCP JSON-RPC framing (mocked process I/O)" test-scope line without needing an `IMcpTransport` abstraction layer.
- **`Logger` takes its session directory as an explicit constructor parameter**, not a lazy `new Config()` default — Python needed a lazy import there only to dodge a circular-import ordering problem that doesn't exist in C#; threading `dir` through explicitly from `BoukenshaHost` is simpler and keeps `Logger` decoupled from `Config`.
- **Default prompts ship as `Content`/`None` files copied to the build output** (`Boukensha.Core/prompts/*.md`, `CopyToOutputDirectory`), with `Config.PromptsDir = Path.Combine(AppContext.BaseDirectory, "prompts")` — avoids Python's fragile "climb three parents from this source file" trick, which has no clean equivalent once C# is compiled to a different output directory.
- **Client HTTP retry is hand-rolled** (`HttpClient` + manual exponential backoff over a fixed retryable-status-code set), not Polly — consistent with the hand-rolled-MCP precedent; Polly remains a reasonable later upgrade if retry logic grows more complex.
- **Solution file is `Boukensha.slnx`, not `Boukensha.sln`** — the .NET 10 SDK's `dotnet new sln` template now defaults to the newer XML-based `.slnx` format. Discovered when Task 1's first `dotnet build Boukensha.sln` failed with `MSB1009: Project file does not exist`; all commands in this plan use the real generated filename.
- **No dedicated `Client` unit tests this pass** — the design doc's test scope names `Context`/`Registry`/MCP framing/`AnthropicBackend` specifically; `Client`'s retry loop gets exercised functionally in Task 18's live/dry-run verification instead, matching Python's own precedent of not unit-testing its retry loop either (mocked smoke check only, not a committed test).

---

## File Structure

```
week2_capable/dotnet/
  Boukensha.slnx
  src/
    Boukensha.Core/
      Boukensha.Core.csproj
      Errors.cs                  # 4 exception types
      Message.cs                 # ContentBlock union, MessageContent, Message record
      Tool.cs                    # ToolParameter, ToolDefinition
      JsonUtil.cs                # JsonNode/JsonElement <-> plain object? conversion
      Context.cs
      Registry.cs
      Logger.cs
      Config.cs
      PromptBuilder.cs
      Client.cs
      Agent.cs
      RunDsl.cs
      BoukenshaHost.cs           # composition root + BoukenshaOptions/BoukenshaSession
      prompts/
        system.md                # shipped default system prompt
      Backends/
        ILlmBackend.cs
        AnthropicBackend.cs
      Mcp/
        JsonRpc.cs
        McpClient.cs
        McpToolRegistrar.cs
      Tasks/
        ITask.cs
        TaskBase.cs
        PlayerTask.cs
    Boukensha.Console/
      Boukensha.Console.csproj
      Program.cs
      Repl.cs
      Tui/
        TuiOutputSink.cs
  tests/
    Boukensha.Core.Tests/
      Boukensha.Core.Tests.csproj
      ContextTests.cs
      RegistryTests.cs
      Mcp/JsonRpcTests.cs
      Backends/AnthropicBackendTests.cs
```

---

## Task 1: Solution & project scaffolding

**Files:**
- Create: `week2_capable/dotnet/Boukensha.slnx`
- Create: `week2_capable/dotnet/src/Boukensha.Core/Boukensha.Core.csproj`
- Create: `week2_capable/dotnet/src/Boukensha.Console/Boukensha.Console.csproj`
- Create: `week2_capable/dotnet/tests/Boukensha.Core.Tests/Boukensha.Core.Tests.csproj`
- Modify: `.gitignore` (repo root) — add .NET build artifacts

**Steps:**

- [ ] Scaffold projects via the .NET CLI:
```bash
cd week2_capable/dotnet
dotnet new sln -n Boukensha
dotnet new classlib -n Boukensha.Core -o src/Boukensha.Core -f net10.0
dotnet new console -n Boukensha.Console -o src/Boukensha.Console -f net10.0
dotnet new xunit -n Boukensha.Core.Tests -o tests/Boukensha.Core.Tests -f net10.0
dotnet sln add src/Boukensha.Core/Boukensha.Core.csproj src/Boukensha.Console/Boukensha.Console.csproj tests/Boukensha.Core.Tests/Boukensha.Core.Tests.csproj
dotnet add src/Boukensha.Console/Boukensha.Console.csproj reference src/Boukensha.Core/Boukensha.Core.csproj
dotnet add tests/Boukensha.Core.Tests/Boukensha.Core.Tests.csproj reference src/Boukensha.Core/Boukensha.Core.csproj
dotnet add src/Boukensha.Core/Boukensha.Core.csproj package YamlDotNet
dotnet add src/Boukensha.Console/Boukensha.Console.csproj package Spectre.Console
```
- [ ] Delete the template-generated `Class1.cs` in `Boukensha.Core` (superseded by the files in later tasks).
- [ ] Add to `week2_capable/dotnet/src/Boukensha.Core/Boukensha.Core.csproj`, inside the existing `<Project>`:
```xml
  <ItemGroup>
    <None Include="prompts\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```
- [ ] Append to the repo root `.gitignore` (it already ignores `bin/` repo-wide, but not MSBuild's `obj/`):
```
obj/
*.user
.vs/
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` — expect success (empty library, empty console `Hello, World!`, template test project).
- [ ] Commit.

---

## Task 2: Foundational types — errors, message content, tool definition, JSON utilities

**Files:**
- Create: `src/Boukensha.Core/Errors.cs`
- Create: `src/Boukensha.Core/Message.cs`
- Create: `src/Boukensha.Core/Tool.cs`
- Create: `src/Boukensha.Core/JsonUtil.cs`

**Interfaces produced:** `UnknownToolException`, `ApiException`, `LoopException`, `UnsupportedModelException`; `ContentBlock`/`TextBlock`/`ToolUseBlock`/`ToolResultBlock`/`ReasoningBlock`; `MessageContent` (factory methods `Of(string)`/`Of(IReadOnlyList<ContentBlock>)`, `IsText`, `Text`, `Blocks`); `Message(string Role, MessageContent Content, string? ToolUseId = null)`; `ToolParameter(string Type, string? Description = null)`; `ToolDefinition(string Name, string Description, IReadOnlyDictionary<string, ToolParameter> Parameters, Func<IReadOnlyDictionary<string, object?>, Task<string>> Handler)`; `JsonUtil.ToObject(JsonNode?)`, `JsonUtil.ToObject(JsonElement)`, `JsonUtil.ToJsonNode(object?)`.

- [ ] Write `src/Boukensha.Core/Errors.cs`:
```csharp
namespace Boukensha.Core;

public sealed class UnknownToolException(string message) : Exception(message);

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
    public ApiException(string message, Exception inner) : base(message, inner) { }
}

public sealed class LoopException(string message) : Exception(message);

public sealed class UnsupportedModelException(string message) : Exception(message);
```

- [ ] Write `src/Boukensha.Core/Message.cs`:
```csharp
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
```

- [ ] Write `src/Boukensha.Core/Tool.cs`:
```csharp
namespace Boukensha.Core;

public sealed record ToolParameter(string Type, string? Description = null);

public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, ToolParameter> Parameters,
    Func<IReadOnlyDictionary<string, object?>, Task<string>> Handler);
```

- [ ] Write `src/Boukensha.Core/JsonUtil.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Boukensha.Core;

public static class JsonUtil
{
    public static object? ToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => ToObject(value.GetValue<JsonElement>()),
        JsonArray array => array.Select(ToObject).ToList(),
        JsonObject obj => obj.ToDictionary(kv => kv.Key, kv => ToObject(kv.Value)),
        _ => null,
    };

    public static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        _ => null,
    };

    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        IReadOnlyDictionary<string, object?> dict =>
            new JsonObject(dict.Select(kv => KeyValuePair.Create(kv.Key, ToJsonNode(kv.Value))!)),
        System.Collections.IEnumerable list and not string =>
            new JsonArray(list.Cast<object?>().Select(ToJsonNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}
```

- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 3: Tasks — `ITask`/`TaskBase`/`PlayerTask`

**Files:**
- Create: `src/Boukensha.Core/Tasks/ITask.cs`
- Create: `src/Boukensha.Core/Tasks/TaskBase.cs`
- Create: `src/Boukensha.Core/Tasks/PlayerTask.cs`

**Interfaces produced:** `ITask { string TaskName; string Provider(settings); string Model(settings); bool PromptOverride(settings, prompt="system"); string? Prompt(settings, name, userPromptsDir, defaultPromptsDir); string? SystemPrompt(settings, userPromptsDir, defaultPromptsDir); int MaxIterations(settings); int MaxOutputTokens(settings); }`; `PlayerTask : TaskBase` with `TaskName => "player"`.

- [ ] Write `src/Boukensha.Core/Tasks/ITask.cs`:
```csharp
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
```

- [ ] Write `src/Boukensha.Core/Tasks/TaskBase.cs`:
```csharp
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
```

- [ ] Write `src/Boukensha.Core/Tasks/PlayerTask.cs`:
```csharp
namespace Boukensha.Core.Tasks;

public sealed class PlayerTask : TaskBase
{
    public override string TaskName => "player";
}
```

- [ ] Write the default shipped prompt, `src/Boukensha.Core/prompts/system.md`:
```markdown
You are Boukensha, an autonomous agent playing a MUD (multi-user dungeon).

Use the tools available to you to explore, navigate, and interact with the
world. Prefer inspecting a room fully before moving on, and be concise in
your final answers to the user.
```

- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 4: `Context` (with compaction tests)

**Files:**
- Create: `src/Boukensha.Core/Context.cs`
- Test: `tests/Boukensha.Core.Tests/ContextTests.cs`

**Consumes:** `Tasks.ITask`, `Message`, `MessageContent`, `ContentBlock`, `ToolDefinition` (Tasks 2–3).
**Produces:** `Context(ITask task, string? system = null, int contextWindow = 200_000, string? workingDir = null, double compactionThreshold = 0.85)` with `Task`, `System`, `ContextWindow`, `WorkingDir`, `Messages: List<Message>`, `Tools: Dictionary<string, ToolDefinition>`, `CurrentTokens`, `TurnTokens`, `ToolCount`, `UsageFraction`, `UsagePct`; methods `RegisterTool(ToolDefinition)`, `AddMessage(string, string, string? = null)`, `AddMessage(string, IReadOnlyList<ContentBlock>, string? = null)`, `UpdateTokens(int)`, `ResetTurnTokens()`, `AddTurnTokens(int, int)`, `NeedsCompaction(double? = null)`, `int CompactMessages()`, `ClearMessages()`.

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/ContextTests.cs`:
```csharp
using Boukensha.Core;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests;

public class ContextTests
{
    private static Context NewContext(int contextWindow = 100) =>
        new(new PlayerTask(), "system prompt", contextWindow);

    [Fact]
    public void UsageFraction_ReflectsCurrentTokensOverContextWindow()
    {
        var context = NewContext(contextWindow: 200);
        context.UpdateTokens(50);
        Assert.Equal(0.25, context.UsageFraction, 3);
        Assert.Equal(25, context.UsagePct);
    }

    [Fact]
    public void NeedsCompaction_TrueAtOrAboveThreshold()
    {
        var context = NewContext(contextWindow: 100);
        context.UpdateTokens(85);
        Assert.True(context.NeedsCompaction());
    }

    [Fact]
    public void CompactMessages_DropsOldest40PercentAndResetsCurrentTokens()
    {
        var context = NewContext();
        for (var i = 0; i < 10; i++) context.AddMessage("user", $"message {i}");
        context.UpdateTokens(999);

        var dropped = context.CompactMessages();

        Assert.Equal(4, dropped); // ceil(10 * 0.40) = 4
        Assert.Equal(6, context.Messages.Count);
        Assert.Equal("message 4", context.Messages[0].Content.Text);
        Assert.Equal(0, context.CurrentTokens);
    }

    [Fact]
    public void CompactMessages_AlwaysKeepsAtLeastTwoMessages()
    {
        var context = NewContext();
        context.AddMessage("user", "one");
        context.AddMessage("assistant", "two");
        context.AddMessage("user", "three");

        var dropped = context.CompactMessages();

        Assert.True(context.Messages.Count >= 2);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void AddTurnTokens_AccumulatesSeparatelyFromCurrentTokens()
    {
        var context = NewContext();
        context.UpdateTokens(40);
        context.AddTurnTokens(10, 5);
        context.AddTurnTokens(10, 5);

        Assert.Equal(40, context.CurrentTokens);
        Assert.Equal(30, context.TurnTokens);
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ContextTests` — expect build failure (`Context` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/Context.cs`:
```csharp
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
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter ContextTests` — expect all pass.
- [ ] Commit.

---

## Task 5: `Registry` (with dispatch tests)

**Files:**
- Create: `src/Boukensha.Core/Registry.cs`
- Test: `tests/Boukensha.Core.Tests/RegistryTests.cs`

**Consumes:** `Context`, `ToolDefinition`, `UnknownToolException` (Tasks 2, 4).
**Produces:** `Registry(Context context)` with `ToolDefinition Tool(string name, string description, IReadOnlyDictionary<string, ToolParameter>? parameters, Func<IReadOnlyDictionary<string, object?>, Task<string>> handler)`, `bool Registered(string name)`, `Task<string> DispatchAsync(string name, IReadOnlyDictionary<string, object?>? args = null)`.

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/RegistryTests.cs`:
```csharp
using Boukensha.Core;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests;

public class RegistryTests
{
    private static (Context, Registry) Build()
    {
        var context = new Context(new PlayerTask(), contextWindow: 100);
        return (context, new Registry(context));
    }

    [Fact]
    public async Task DispatchAsync_InvokesRegisteredToolHandler()
    {
        var (_, registry) = Build();
        registry.Tool("echo", "echoes input", new Dictionary<string, ToolParameter> { ["text"] = new("string") },
            args => Task.FromResult((string)args["text"]!));

        var result = await registry.DispatchAsync("echo", new Dictionary<string, object?> { ["text"] = "hi" });

        Assert.Equal("hi", result);
    }

    [Fact]
    public async Task DispatchAsync_UnknownTool_ThrowsUnknownToolException()
    {
        var (_, registry) = Build();

        await Assert.ThrowsAsync<UnknownToolException>(() => registry.DispatchAsync("missing"));
    }

    [Fact]
    public void Registered_ReflectsToolRegistration()
    {
        var (_, registry) = Build();

        Assert.False(registry.Registered("echo"));
        registry.Tool("echo", "echoes input", null, _ => Task.FromResult(""));
        Assert.True(registry.Registered("echo"));
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RegistryTests` — expect build failure (`Registry` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/Registry.cs`:
```csharp
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
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter RegistryTests` — expect all pass.
- [ ] Commit.

---

## Task 6: `Logger`

**Files:**
- Create: `src/Boukensha.Core/Logger.cs`

**Consumes:** `Message`, `MessageContent`, `ToolDefinition` (Task 2, 4).
**Produces:** `Logger(string dir, string? sessionId = null, string? log = null, IReadOnlyDictionary<string, object?>? snapshot = null) : IDisposable` with `Path`; event methods `Turn(int)`, `Iteration(int,int)`, `LimitReached(string,int,int)`, `TurnEnd(string,int,int?=null)`, `Prompt(IReadOnlyList<Message>, IReadOnlyDictionary<string,ToolDefinition>, int)`, `Compaction(int,int,int)`, `ToolCall(string, IReadOnlyDictionary<string,object?>)`, `ToolResult(string,string,bool=true,string?=null)`, `Response(string, IReadOnlyDictionary<string,object?>?, string?, string?, string?, double?)`, `Reasoning(string, bool=false)`, `Plan(string)`; `Subscribe(Action<IReadOnlyDictionary<string,object?>>)`; `Dispose()`.

- [ ] Write `src/Boukensha.Core/Logger.cs`:
```csharp
using System.Security.Cryptography;
using System.Text.Json;

namespace Boukensha.Core;

public sealed class Logger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _sessionId;
    private readonly List<Action<IReadOnlyDictionary<string, object?>>> _subscribers = [];
    private readonly Lock _lock = new();

    public string Path { get; }

    public Logger(string dir, string? sessionId = null, string? log = null, IReadOnlyDictionary<string, object?>? snapshot = null)
    {
        _sessionId = sessionId ?? GenerateSessionId();
        Path = log ?? System.IO.Path.Combine(dir, $"{_sessionId}.jsonl");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

        var start = new Dictionary<string, object?> { ["phase"] = "session_start" };
        if (snapshot is not null)
        {
            foreach (var (key, value) in snapshot) start[key] = value;
        }
        WriteLog(start);
    }

    public void Turn(int n) => WriteLog(new() { ["phase"] = "turn", ["n"] = n });

    public void Iteration(int n, int max) => WriteLog(new() { ["phase"] = "iteration", ["n"] = n, ["max"] = max });

    public void LimitReached(string kind, int n, int max) =>
        WriteLog(new() { ["phase"] = "limit_reached", ["kind"] = kind, ["n"] = n, ["max"] = max });

    public void TurnEnd(string reason, int iterations, int? tokens = null) =>
        WriteLog(new() { ["phase"] = "turn_end", ["reason"] = reason, ["iterations"] = iterations, ["tokens"] = tokens });

    public void Prompt(IReadOnlyList<Message> messages, IReadOnlyDictionary<string, ToolDefinition> tools, int contextWindow) =>
        WriteLog(new()
        {
            ["phase"] = "prompt",
            ["messages"] = messages.Select(SerializeMessage).ToList(),
            ["message_count"] = messages.Count,
            ["tool_count"] = tools.Count,
            ["tools"] = tools.Keys.ToList(),
            ["context_window"] = contextWindow,
        });

    public void Compaction(int before, int dropped, int contextWindow) =>
        WriteLog(new() { ["phase"] = "compaction", ["before"] = before, ["dropped"] = dropped, ["context_window"] = contextWindow });

    public void ToolCall(string name, IReadOnlyDictionary<string, object?> args) =>
        WriteLog(new() { ["phase"] = "tool_call", ["name"] = name, ["args"] = args });

    public void ToolResult(string name, string result, bool ok = true, string? error = null) =>
        WriteLog(new() { ["phase"] = "tool_result", ["name"] = name, ["result"] = result, ["ok"] = ok, ["error"] = error });

    public void Response(string text, IReadOnlyDictionary<string, object?>? usage, string? stopReason, string? task, string? backend, double? costUsd) =>
        WriteLog(new()
        {
            ["phase"] = "response",
            ["text"] = text,
            ["usage"] = usage,
            ["stop_reason"] = stopReason,
            ["task"] = task,
            ["provider"] = backend,
            ["cost_usd"] = costUsd,
        });

    public void Reasoning(string text, bool redacted = false) =>
        WriteLog(new() { ["phase"] = "reasoning", ["text"] = text, ["redacted"] = redacted });

    public void Plan(string text) => WriteLog(new() { ["phase"] = "plan", ["text"] = text });

    public void Subscribe(Action<IReadOnlyDictionary<string, object?>> handler)
    {
        lock (_lock) _subscribers.Add(handler);
    }

    public void Dispose() => _writer.Dispose();

    private static Dictionary<string, object?> SerializeMessage(Message message) => new()
    {
        ["role"] = message.Role,
        ["content"] = message.Content.IsText ? message.Content.Text : message.Content.Blocks,
    };

    private void WriteLog(Dictionary<string, object?> evt)
    {
        evt["session_id"] = _sessionId;
        evt["at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        List<Action<IReadOnlyDictionary<string, object?>>> subscribersSnapshot;
        lock (_lock)
        {
            _writer.WriteLine(JsonSerializer.Serialize(evt));
            subscribersSnapshot = [.. _subscribers];
        }
        foreach (var subscriber in subscribersSnapshot) subscriber(evt);
    }

    private static string GenerateSessionId()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{timestamp}-{suffix}";
    }
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 7: `Config`

**Files:**
- Create: `src/Boukensha.Core/Config.cs`

**Produces:** `Config()` (parameterless — resolves `BOUKENSHA_DIR`/`~/.boukensha`, loads `.env` and `settings.yaml`) with `Dir`, `Settings`, `Tasks(string? = null)`, `UserPromptsDir`, `McpServers`, `AgentMaxTurnTokens`, `AgentCompactionThreshold`, `Dig(params string[])`; static `Config.DefaultDir`, `Config.PromptsDir`.

- [ ] Write `src/Boukensha.Core/Config.cs`:
```csharp
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
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 8: MCP JSON-RPC framing (`JsonRpc`, tested) + `McpClient`

**Files:**
- Create: `src/Boukensha.Core/Mcp/JsonRpc.cs`
- Create: `src/Boukensha.Core/Mcp/McpClient.cs`
- Test: `tests/Boukensha.Core.Tests/Mcp/JsonRpcTests.cs`

**Produces:** `JsonRpc.BuildRequest(int,string,JsonNode)`, `JsonRpc.BuildNotification(string,JsonNode)`, `JsonRpc.TryParseResponse(string,int,out JsonObject?)`, `JsonRpc.ExtractToolText(JsonObject)`, `JsonRpc.IsToolError(JsonObject)`. `McpClient(string name, string command, IReadOnlyList<string>? args=null, IReadOnlyDictionary<string,string>? env=null) : IAsyncDisposable` with `Name`, `StartAsync(CancellationToken=default)`, `Task<JsonArray> ToolsListAsync(...)`, `Task<string> ToolsCallAsync(string, IReadOnlyDictionary<string,object?>, ...)`, nested `McpClient.McpException`.

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/Mcp/JsonRpcTests.cs`:
```csharp
using System.Text.Json.Nodes;
using Boukensha.Core.Mcp;
using Xunit;

namespace Boukensha.Core.Tests.Mcp;

public class JsonRpcTests
{
    [Fact]
    public void BuildRequest_ProducesJsonRpc20Envelope()
    {
        var json = JsonRpc.BuildRequest(1, "tools/list", new JsonObject());
        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"id\":1", json);
        Assert.Contains("\"method\":\"tools/list\"", json);
    }

    [Fact]
    public void TryParseResponse_MatchesOnExpectedId()
    {
        var ok = JsonRpc.TryParseResponse("""{"jsonrpc":"2.0","id":3,"result":{}}""", 3, out var message);
        Assert.True(ok);
        Assert.NotNull(message);
    }

    [Fact]
    public void TryParseResponse_IgnoresMismatchedId()
    {
        var ok = JsonRpc.TryParseResponse("""{"jsonrpc":"2.0","id":4,"result":{}}""", 3, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParseResponse_IgnoresMalformedJson()
    {
        var ok = JsonRpc.TryParseResponse("not json", 3, out _);
        Assert.False(ok);
    }

    [Fact]
    public void ExtractToolText_ConcatenatesTextBlocks()
    {
        var result = JsonNode.Parse(
            """{"content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}]}""")!.AsObject();
        Assert.Equal("hello world", JsonRpc.ExtractToolText(result));
    }

    [Fact]
    public void IsToolError_ReadsIsErrorFlag()
    {
        var result = JsonNode.Parse("""{"isError":true}""")!.AsObject();
        Assert.True(JsonRpc.IsToolError(result));
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter JsonRpcTests` — expect build failure (`JsonRpc` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/Mcp/JsonRpc.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Boukensha.Core.Mcp;

public static class JsonRpc
{
    public static string BuildRequest(int id, string method, JsonNode @params) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }.ToJsonString();

    public static string BuildNotification(string method, JsonNode @params) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }.ToJsonString();

    public static bool TryParseResponse(string line, int expectedId, out JsonObject? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed?["id"] is null) return false;
        if (parsed["id"]!.GetValue<int>() != expectedId) return false;

        message = parsed;
        return true;
    }

    public static string ExtractToolText(JsonObject result) =>
        string.Concat((result["content"] as JsonArray ?? [])
            .Where(block => block?["type"]?.GetValue<string>() == "text")
            .Select(block => block!["text"]!.GetValue<string>()));

    public static bool IsToolError(JsonObject result) => result["isError"]?.GetValue<bool>() == true;
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter JsonRpcTests` — expect all pass.
- [ ] Write `src/Boukensha.Core/Mcp/McpClient.cs`:
```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Boukensha.Core.Mcp;

public sealed class McpClient(
    string name,
    string command,
    IReadOnlyList<string>? args = null,
    IReadOnlyDictionary<string, string>? env = null) : IAsyncDisposable
{
    public sealed class McpException(string message) : Exception(message);

    private const string ProtocolVersion = "2024-11-05";

    public string Name { get; } = name;

    private Process? _process;
    private int _nextId;
    private readonly StringBuilder _stderrBuffer = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args ?? []) startInfo.ArgumentList.Add(arg);
        foreach (var (key, value) in env ?? new Dictionary<string, string>()) startInfo.Environment[key] = value;

        try
        {
            _process = Process.Start(startInfo) ?? throw new McpException($"failed to start MCP server '{Name}' ({command})");
        }
        catch (Exception e) when (e is not McpException)
        {
            throw new McpException($"failed to start MCP server '{Name}' ({command}): {e.Message}");
        }

        _ = Task.Run(DrainStderrAsync, cancellationToken);

        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "boukensha", ["version"] = "0.1.0" },
        }, cancellationToken);
        await WriteAsync(JsonRpc.BuildNotification("notifications/initialized", new JsonObject()), cancellationToken);
    }

    public async Task<JsonArray> ToolsListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), cancellationToken);
        return result?["tools"] as JsonArray ?? [];
    }

    public async Task<string> ToolsCallAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = JsonUtil.ToJsonNode(arguments),
        }, cancellationToken) ?? new JsonObject();

        var text = JsonRpc.ExtractToolText(result);
        if (JsonRpc.IsToolError(result))
        {
            throw new McpException($"tool '{toolName}' on '{Name}' failed: {text}");
        }
        return text;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try { _process.StandardInput.Close(); } catch { /* already closed */ }
        try { _process.StandardOutput.Close(); } catch { /* already closed */ }
        try
        {
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch { /* best-effort shutdown */ }
        _process.Dispose();
        _process = null;
    }

    private async Task<JsonObject?> RequestAsync(string method, JsonNode @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        await WriteAsync(JsonRpc.BuildRequest(id, method, @params), cancellationToken);
        var response = await ReadResponseAsync(id, cancellationToken);
        if (response["error"] is JsonObject error)
        {
            throw new McpException($"{Name}: {error["message"]}");
        }
        return response["result"] as JsonObject;
    }

    private async Task WriteAsync(string line, CancellationToken cancellationToken)
    {
        if (_process is null) throw new McpException($"MCP server '{Name}' has not been started");
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new McpException($"MCP server '{Name}' closed its input unexpectedly: {e.Message}");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<JsonObject> ReadResponseAsync(int expectedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _process!.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new McpException($"MCP server '{Name}' closed its output unexpectedly (stderr: {_stderrBuffer})");
            }
            if (JsonRpc.TryParseResponse(line, expectedId, out var message))
            {
                return message!;
            }
        }
    }

    private async Task DrainStderrAsync()
    {
        if (_process is null) return;
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync()) is not null)
            {
                lock (_stderrBuffer) _stderrBuffer.AppendLine(line);
            }
        }
        catch { /* process ended */ }
    }
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 9: `McpToolRegistrar`

**Files:**
- Create: `src/Boukensha.Core/Mcp/McpToolRegistrar.cs`

**Consumes:** `Registry`, `McpClient`, `ToolParameter` (Tasks 2, 5, 8).
**Produces:** `McpToolRegistrar.RegisterAsync(Registry, McpClient, string? prefix, CancellationToken=default)`.

- [ ] Write `src/Boukensha.Core/Mcp/McpToolRegistrar.cs`:
```csharp
using System.Text.Json.Nodes;

namespace Boukensha.Core.Mcp;

public static class McpToolRegistrar
{
    public static async Task RegisterAsync(Registry registry, McpClient client, string? prefix, CancellationToken cancellationToken = default)
    {
        var tools = await client.ToolsListAsync(cancellationToken);
        foreach (var tool in tools)
        {
            if (tool is not JsonObject obj) continue;
            var rawName = obj["name"]!.GetValue<string>();
            var toolName = string.IsNullOrEmpty(prefix) ? rawName : $"{prefix}_{rawName}";

            if (registry.Registered(toolName))
            {
                throw new ArgumentException(
                    $"tool name collision: '{toolName}' from MCP server '{client.Name}' is already registered. " +
                    "Configure a different 'prefix' for this server in settings.yaml.");
            }

            var parameters = new Dictionary<string, ToolParameter>();
            if (obj["inputSchema"]?["properties"] is JsonObject properties)
            {
                foreach (var (paramName, schema) in properties)
                {
                    parameters[paramName] = new ToolParameter(
                        schema?["type"]?.GetValue<string>() ?? "string",
                        schema?["description"]?.GetValue<string>());
                }
            }

            var description = obj["description"]?.GetValue<string>() ?? string.Empty;
            registry.Tool(toolName, description, parameters, args => client.ToolsCallAsync(rawName, args));
        }
    }
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 10: `ILlmBackend` + `AnthropicBackend` (with payload/parse round-trip tests)

**Files:**
- Create: `src/Boukensha.Core/Backends/ILlmBackend.cs`
- Create: `src/Boukensha.Core/Backends/AnthropicBackend.cs`
- Test: `tests/Boukensha.Core.Tests/Backends/AnthropicBackendTests.cs`

**Consumes:** `Context`, `Message`, `MessageContent`, `ContentBlock`/subtypes, `ToolDefinition`, `JsonUtil`, `UnsupportedModelException` (Tasks 2, 4).
**Produces:** `ParsedResponse(string StopReason, IReadOnlyList<ContentBlock> Content)`; `ILlmBackend { string Model; int ContextWindow; IReadOnlyDictionary<string,string> Headers; string Url; JsonArray ToMessages(IReadOnlyList<Message>); JsonArray ToTools(IReadOnlyDictionary<string,ToolDefinition>); JsonObject ToPayload(Context, int, JsonArray?=null); ParsedResponse ParseResponse(JsonNode); double? EstimateCost(int,int); }`; `AnthropicBackend(string apiKey, string model) : ILlmBackend`.

- [ ] Write `src/Boukensha.Core/Backends/ILlmBackend.cs`:
```csharp
using System.Text.Json.Nodes;

namespace Boukensha.Core.Backends;

public sealed record ParsedResponse(string StopReason, IReadOnlyList<ContentBlock> Content);

public interface ILlmBackend
{
    string Model { get; }
    int ContextWindow { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    string Url { get; }
    JsonArray ToMessages(IReadOnlyList<Message> messages);
    JsonArray ToTools(IReadOnlyDictionary<string, ToolDefinition> tools);
    JsonObject ToPayload(Context context, int maxOutputTokens, JsonArray? toolsOverride = null);
    ParsedResponse ParseResponse(JsonNode response);
    double? EstimateCost(int inputTokens, int outputTokens);
}
```

- [ ] Write the failing tests, `tests/Boukensha.Core.Tests/Backends/AnthropicBackendTests.cs`:
```csharp
using System.Text.Json.Nodes;
using Boukensha.Core;
using Boukensha.Core.Backends;
using Boukensha.Core.Tasks;
using Xunit;

namespace Boukensha.Core.Tests.Backends;

public class AnthropicBackendTests
{
    [Fact]
    public void Constructor_RejectsUnsupportedModel()
    {
        Assert.Throws<UnsupportedModelException>(() => new AnthropicBackend("key", "not-a-real-model"));
    }

    [Fact]
    public void ToPayload_IncludesSystemModelAndMessages()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), "be helpful", backend.ContextWindow);
        context.AddMessage("user", "hello");

        var payload = backend.ToPayload(context, 512);

        Assert.Equal("claude-haiku-4-5", payload["model"]!.GetValue<string>());
        Assert.Equal("be helpful", payload["system"]!.GetValue<string>());
        Assert.Equal(512, payload["max_tokens"]!.GetValue<int>());
        Assert.Single(payload["messages"]!.AsArray());
    }

    [Fact]
    public void ParseResponse_NormalizesThinkingBlockToReasoningBlock()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var response = JsonNode.Parse(
            """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"pondering","signature":"sig-1"}]}""")!;

        var parsed = backend.ParseResponse(response);

        var reasoning = Assert.IsType<ReasoningBlock>(Assert.Single(parsed.Content));
        Assert.Equal("pondering", reasoning.Text);
        Assert.False(reasoning.Redacted);
        Assert.Equal("sig-1", reasoning.Signature);
    }

    [Fact]
    public void ParseResponse_ToolUseSetsStopReason()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var response = JsonNode.Parse(
            """{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"look","input":{}}]}""")!;

        var parsed = backend.ParseResponse(response);

        Assert.Equal("tool_use", parsed.StopReason);
        Assert.IsType<ToolUseBlock>(Assert.Single(parsed.Content));
    }

    [Fact]
    public void AssistantContentRoundTrip_PreservesThinkingSignature()
    {
        var backend = new AnthropicBackend("key", "claude-haiku-4-5");
        var context = new Context(new PlayerTask(), contextWindow: backend.ContextWindow);
        context.AddMessage("assistant", new ContentBlock[]
        {
            new ReasoningBlock("pondering", false, "sig-1"),
            new TextBlock("done"),
        });

        var messages = backend.ToMessages(context.Messages);
        var content = messages[0]!["content"]!.AsArray();

        Assert.Equal("thinking", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("sig-1", content[0]!["signature"]!.GetValue<string>());
        Assert.Equal("pondering", content[0]!["thinking"]!.GetValue<string>());
    }
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AnthropicBackendTests` — expect build failure (`AnthropicBackend` doesn't exist yet).
- [ ] Write `src/Boukensha.Core/Backends/AnthropicBackend.cs`:
```csharp
using System.Text.Json.Nodes;

namespace Boukensha.Core.Backends;

public sealed record AnthropicModelInfo(int ContextWindow, double? InputCostPerMillion, double? OutputCostPerMillion);

public sealed class AnthropicBackend : ILlmBackend
{
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";

    private static readonly IReadOnlyDictionary<string, AnthropicModelInfo> ModelCatalog = new Dictionary<string, AnthropicModelInfo>
    {
        ["claude-haiku-4-5"] = new(200_000, 1.0, 5.0),
        ["claude-sonnet-4-6"] = new(1_000_000, 3.0, 15.0),
        ["claude-opus-4-8"] = new(1_000_000, 5.0, 25.0),
    };

    private readonly string _apiKey;
    private readonly AnthropicModelInfo _modelInfo;

    public AnthropicBackend(string apiKey, string model)
    {
        _apiKey = apiKey;
        if (!ModelCatalog.ContainsKey(model))
        {
            throw new UnsupportedModelException(
                $"unsupported model '{model}'. Supported: {string.Join(", ", ModelCatalog.Keys.OrderBy(m => m))}");
        }
        Model = model;
        _modelInfo = ModelCatalog[Model];
    }

    public string Model { get; }
    public int ContextWindow => _modelInfo.ContextWindow;

    public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>
    {
        ["Content-Type"] = "application/json",
        ["x-api-key"] = _apiKey,
        ["anthropic-version"] = "2023-06-01",
    };

    public string Url => BaseUrl;

    public JsonArray ToMessages(IReadOnlyList<Message> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(message.Role switch
            {
                "tool_result" => new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolUseId,
                        ["content"] = message.Content.Text,
                    }),
                },
                "assistant" => new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = AssistantContent(message.Content),
                },
                _ => new JsonObject
                {
                    ["role"] = message.Role,
                    ["content"] = message.Content.Text,
                },
            });
        }
        return array;
    }

    public JsonArray ToTools(IReadOnlyDictionary<string, ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools.Values)
        {
            var properties = new JsonObject();
            foreach (var (paramName, parameter) in tool.Parameters)
            {
                properties[paramName] = new JsonObject
                {
                    ["type"] = parameter.Type,
                    ["description"] = parameter.Description,
                };
            }
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(tool.Parameters.Keys.Select(k => (JsonNode)k).ToArray()),
                },
            });
        }
        return array;
    }

    public JsonObject ToPayload(Context context, int maxOutputTokens, JsonArray? toolsOverride = null) => new()
    {
        ["model"] = Model,
        ["system"] = context.System,
        ["max_tokens"] = maxOutputTokens,
        ["tools"] = toolsOverride ?? ToTools(context.Tools),
        ["messages"] = ToMessages(context.Messages),
    };

    public ParsedResponse ParseResponse(JsonNode response)
    {
        var stopReason = response["stop_reason"]?.GetValue<string>() == "tool_use" ? "tool_use" : "end_turn";
        var blocks = (response["content"] as JsonArray ?? []).Select(NormalizeBlock).ToList();
        return new ParsedResponse(stopReason, blocks);
    }

    public double? EstimateCost(int inputTokens, int outputTokens)
    {
        if (_modelInfo.InputCostPerMillion is null || _modelInfo.OutputCostPerMillion is null) return null;
        return (inputTokens * _modelInfo.InputCostPerMillion.Value + outputTokens * _modelInfo.OutputCostPerMillion.Value) / 1_000_000.0;
    }

    private static ContentBlock NormalizeBlock(JsonNode? node)
    {
        var type = node?["type"]?.GetValue<string>();
        return type switch
        {
            "thinking" => new ReasoningBlock(node!["thinking"]!.GetValue<string>(), false, node["signature"]?.GetValue<string>()),
            "redacted_thinking" => new ReasoningBlock(string.Empty, true, node!["data"]?.GetValue<string>()),
            "tool_use" => new ToolUseBlock(
                node!["id"]!.GetValue<string>(),
                node["name"]!.GetValue<string>(),
                JsonUtil.ToObject(node["input"]) as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>()),
            _ => new TextBlock(node?["text"]?.GetValue<string>() ?? string.Empty),
        };
    }

    private static JsonArray AssistantContent(MessageContent content)
    {
        if (content.IsText) return new JsonArray(content.Text);

        var array = new JsonArray();
        foreach (var block in content.Blocks!) array.Add(DenormalizeBlock(block));
        return array;
    }

    private static JsonNode DenormalizeBlock(ContentBlock block) => block switch
    {
        ReasoningBlock { Redacted: true } r => new JsonObject { ["type"] = "redacted_thinking", ["data"] = r.Signature },
        ReasoningBlock r => new JsonObject { ["type"] = "thinking", ["thinking"] = r.Text, ["signature"] = r.Signature },
        ToolUseBlock t => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["input"] = JsonUtil.ToJsonNode(t.Input),
        },
        TextBlock t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        _ => throw new NotSupportedException($"cannot serialize block of type {block.GetType()}"),
    };
}
```
- [ ] Run: `dotnet test week2_capable/dotnet/tests/Boukensha.Core.Tests --filter AnthropicBackendTests` — expect all pass.
- [ ] Commit.

---

## Task 11: `PromptBuilder`

**Files:**
- Create: `src/Boukensha.Core/PromptBuilder.cs`

**Consumes:** `Context`, `Backends.ILlmBackend`, `Backends.ParsedResponse` (Tasks 4, 10).
**Produces:** `PromptBuilder(Context, ILlmBackend)` with `Backend`, `ToMessages()`, `ToTools()`, `ToApiPayload(int=1024, JsonArray?=null)`, `ParseResponse(JsonNode)`, `Headers`, `Url`.

- [ ] Write `src/Boukensha.Core/PromptBuilder.cs`:
```csharp
using System.Text.Json.Nodes;
using Boukensha.Core.Backends;

namespace Boukensha.Core;

public sealed class PromptBuilder(Context context, ILlmBackend backend)
{
    public ILlmBackend Backend { get; } = backend;

    public JsonArray ToMessages() => Backend.ToMessages(context.Messages);

    public JsonArray ToTools() => Backend.ToTools(context.Tools);

    public JsonObject ToApiPayload(int maxOutputTokens = 1024, JsonArray? tools = null) =>
        Backend.ToPayload(context, maxOutputTokens, tools);

    public ParsedResponse ParseResponse(JsonNode response) => Backend.ParseResponse(response);

    public IReadOnlyDictionary<string, string> Headers => Backend.Headers;

    public string Url => Backend.Url;
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 12: `Client` (HTTP transport)

**Files:**
- Create: `src/Boukensha.Core/Client.cs`

**Consumes:** `PromptBuilder`, `ApiException` (Tasks 2, 11).
**Produces:** `Client(PromptBuilder, HttpClient)` with `Task<JsonNode> CallAsync(int maxOutputTokens=1024, JsonArray? tools=null, CancellationToken=default)`.

- [ ] Write `src/Boukensha.Core/Client.cs`:
```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Boukensha.Core;

public sealed class Client(PromptBuilder builder, HttpClient httpClient)
{
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout, HttpStatusCode.Conflict, (HttpStatusCode)429,
        HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
    ];

    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(500);

    public async Task<JsonNode> CallAsync(int maxOutputTokens = 1024, JsonArray? tools = null, CancellationToken cancellationToken = default)
    {
        var payload = builder.ToApiPayload(maxOutputTokens, tools);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, builder.Url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                foreach (var (key, value) in builder.Headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                lastError = e;
                if (attempt > MaxRetries) throw new ApiException($"request failed after {attempt} attempts: {e.Message}", e);
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            if (RetryableStatusCodes.Contains(response.StatusCode) && attempt <= MaxRetries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException($"request failed after {attempt} attempt(s): {(int)response.StatusCode} {body}");
            }
            return JsonNode.Parse(body) ?? throw new ApiException("received empty response body");
        }

        throw new ApiException($"request failed after {MaxRetries + 1} attempts", lastError ?? new InvalidOperationException("unknown error"));
    }

    private static TimeSpan RetryDelay(int attempt) => BaseRetryDelay * Math.Pow(2, attempt - 1);
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 13: `Agent`

**Files:**
- Create: `src/Boukensha.Core/Agent.cs`

**Consumes:** `Context`, `Registry`, `PromptBuilder`, `Client`, `Logger`, `ContentBlock`/subtypes, `ApiException`, `JsonUtil`, `ITask` (Tasks 2–12).
**Produces:** `Agent(Context, Registry, PromptBuilder, Client, Logger, IReadOnlyDictionary<string,object?>? taskSettings=null, int? maxIterations=null, int? maxTurnTokens=null, int? maxOutputTokens=null)` with `Task<string> RunAsync(CancellationToken=default)`.

- [ ] Write `src/Boukensha.Core/Agent.cs`:
```csharp
using System.Text.Json.Nodes;
using Boukensha.Core.Backends;

namespace Boukensha.Core;

public sealed class Agent
{
    private const int DefaultMaxIterations = 25;
    private const int WrapUpOutputTokens = 400;
    private const string WrapUpDirective =
        "You are almost out of iterations or context budget for this turn. " +
        "Stop calling tools now and give the user your best final answer based on what you've learned so far.";

    private readonly Context _context;
    private readonly Registry _registry;
    private readonly PromptBuilder _builder;
    private readonly Client _client;
    private readonly Logger _logger;
    private readonly int _maxIterations;
    private readonly int _maxTurnTokens;
    private readonly int? _maxOutputTokens;
    private int _iteration;

    public Agent(
        Context context,
        Registry registry,
        PromptBuilder builder,
        Client client,
        Logger logger,
        IReadOnlyDictionary<string, object?>? taskSettings = null,
        int? maxIterations = null,
        int? maxTurnTokens = null,
        int? maxOutputTokens = null)
    {
        _context = context;
        _registry = registry;
        _builder = builder;
        _client = client;
        _logger = logger;
        _maxIterations = maxIterations
            ?? (taskSettings is not null ? context.Task.MaxIterations(taskSettings) : DefaultMaxIterations);
        _maxTurnTokens = maxTurnTokens ?? 0;
        _maxOutputTokens = maxOutputTokens
            ?? (taskSettings is not null ? context.Task.MaxOutputTokens(taskSettings) : null);
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        _context.ResetTurnTokens();
        CompactIfNeeded();

        while (true)
        {
            if (_maxIterations > 0 && _iteration >= _maxIterations)
            {
                _logger.LimitReached("iterations", _iteration, _maxIterations);
                return await WrapUpAsync("max_iterations", cancellationToken);
            }
            if (_maxTurnTokens > 0 && _context.TurnTokens >= _maxTurnTokens)
            {
                _logger.LimitReached("turn_tokens", _context.TurnTokens, _maxTurnTokens);
                return await WrapUpAsync("max_turn_tokens", cancellationToken);
            }

            _iteration++;
            _logger.Iteration(_iteration, _maxIterations);
            _logger.Prompt(_context.Messages, _context.Tools, _context.ContextWindow);

            var response = await _client.CallAsync(_maxOutputTokens ?? 1024, cancellationToken: cancellationToken);
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            LogReasoning(parsed.Content);

            if (parsed.StopReason == "tool_use")
            {
                await HandleToolCallsAsync(parsed.Content);
                continue;
            }

            var text = ExtractText(parsed.Content);
            LogResponse(text, response, parsed.StopReason);
            _logger.TurnEnd("completed", _iteration, _context.TurnTokens);
            _context.AddMessage("assistant", text);
            return text;
        }
    }

    private void CompactIfNeeded()
    {
        if (!_context.NeedsCompaction()) return;
        var before = _context.CurrentTokens;
        var dropped = _context.CompactMessages();
        _logger.Compaction(before, dropped, _context.ContextWindow);
    }

    private async Task<string> WrapUpAsync(string reason, CancellationToken cancellationToken)
    {
        _context.AddMessage("user", WrapUpDirective);
        string text;
        try
        {
            var response = await _client.CallAsync(WrapUpOutputTokens, tools: [], cancellationToken: cancellationToken);
            var parsed = _builder.ParseResponse(response);
            RecordUsage(response);
            text = ExtractText(parsed.Content);
            if (string.IsNullOrWhiteSpace(text)) text = FallbackMessage(reason);
            LogResponse(text, response, parsed.StopReason);
        }
        catch (ApiException)
        {
            text = FallbackMessage(reason);
        }
        _context.AddMessage("assistant", text);
        _logger.TurnEnd(reason, _iteration, _context.TurnTokens);
        return text;
    }

    private static string FallbackMessage(string reason) => reason switch
    {
        "max_iterations" => "I ran out of iterations before finishing this turn.",
        "max_turn_tokens" => "I ran out of token budget before finishing this turn.",
        _ => "I had to stop before finishing this turn.",
    };

    private async Task HandleToolCallsAsync(IReadOnlyList<ContentBlock> content)
    {
        var preamble = ExtractText(content);
        if (!string.IsNullOrWhiteSpace(preamble)) _logger.Plan(preamble);

        _context.AddMessage("assistant", content);

        foreach (var block in content.OfType<ToolUseBlock>())
        {
            _logger.ToolCall(block.Name, block.Input);
            string result;
            bool ok = true;
            string? error = null;
            try
            {
                result = await _registry.DispatchAsync(block.Name, block.Input);
            }
            catch (Exception e)
            {
                ok = false;
                error = e.Message;
                result = $"ERROR: {e.GetType().Name}: {e.Message}";
            }
            _logger.ToolResult(block.Name, result, ok, error);
            _context.AddMessage("tool_result", result, block.Id);
        }
    }

    private void RecordUsage(JsonNode response)
    {
        var usage = response["usage"];
        if (usage is null) return;
        var input = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var output = usage["output_tokens"]?.GetValue<int>() ?? 0;
        _context.AddTurnTokens(input, output);
        _context.UpdateTokens(input);
    }

    private void LogReasoning(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content.OfType<ReasoningBlock>())
        {
            if (!block.Redacted && string.IsNullOrEmpty(block.Text)) continue;
            _logger.Reasoning(block.Text, block.Redacted);
        }
    }

    private void LogResponse(string text, JsonNode response, string stopReason)
    {
        var usage = response["usage"] is JsonObject u ? JsonUtil.ToObject(u) as IReadOnlyDictionary<string, object?> : null;
        double? cost = null;
        if (usage is not null
            && usage.TryGetValue("input_tokens", out var i)
            && usage.TryGetValue("output_tokens", out var o)
            && i is not null && o is not null)
        {
            cost = _builder.Backend.EstimateCost(Convert.ToInt32(i), Convert.ToInt32(o));
        }
        _logger.Response(text, usage, stopReason, _context.Task.TaskName, BackendName(), cost);
    }

    private string BackendName() =>
        System.Text.RegularExpressions.Regex
            .Replace(_builder.Backend.GetType().Name.Replace("Backend", ""), "([a-z0-9])([A-Z])", "$1_$2")
            .ToLowerInvariant();

    private static string ExtractText(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextBlock>().Select(b => b.Text));
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 14: `RunDsl`

**Files:**
- Create: `src/Boukensha.Core/RunDsl.cs`

**Consumes:** `Registry` (Task 5).
**Produces:** `RunDsl(Registry)` with `ToolDefinition Tool(string, string, IReadOnlyDictionary<string,ToolParameter>?, Func<IReadOnlyDictionary<string,object?>,Task<string>>)`.

- [ ] Write `src/Boukensha.Core/RunDsl.cs`:
```csharp
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
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 15: `BoukenshaHost` (composition root)

**Files:**
- Create: `src/Boukensha.Core/BoukenshaHost.cs`

**Consumes:** everything from Tasks 2–14.
**Produces:** `BoukenshaOptions(string? System=null, string? Model=null, string? Backend=null, string? ApiKey=null, string? Log=null, int? ContextWindow=null, int? MaxOutputTokens=null, string? WorkingDir=null, bool DisableWorkingDir=false, Action<RunDsl>? Configure=null)`; `BoukenshaSession : IAsyncDisposable` with `Context`, `Registry`, `AgentFactory: Func<Agent>`, `Logger`, `Provider`, `Model`, `McpServerNames`; `BoukenshaHost.BuildAsync(BoukenshaOptions, CancellationToken=default) -> Task<BoukenshaSession>`.

- [ ] Write `src/Boukensha.Core/BoukenshaHost.cs`:
```csharp
using Boukensha.Core.Backends;
using Boukensha.Core.Mcp;
using Boukensha.Core.Tasks;

namespace Boukensha.Core;

public sealed record BoukenshaOptions(
    string? System = null,
    string? Model = null,
    string? Backend = null,
    string? ApiKey = null,
    string? Log = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    string? WorkingDir = null,
    bool DisableWorkingDir = false,
    Action<RunDsl>? Configure = null);

public sealed class BoukenshaSession(
    Context context,
    Registry registry,
    Func<Agent> agentFactory,
    Logger logger,
    IReadOnlyList<McpClient> mcpClients,
    string provider,
    string model) : IAsyncDisposable
{
    public Context Context { get; } = context;
    public Registry Registry { get; } = registry;
    public Func<Agent> AgentFactory { get; } = agentFactory;
    public Logger Logger { get; } = logger;
    public string Provider { get; } = provider;
    public string Model { get; } = model;
    public IReadOnlyList<string> McpServerNames { get; } = mcpClients.Select(c => c.Name).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in mcpClients) await client.DisposeAsync();
        Logger.Dispose();
    }
}

public static class BoukenshaHost
{
    public static async Task<BoukenshaSession> BuildAsync(BoukenshaOptions options, CancellationToken cancellationToken = default)
    {
        var config = new Config();
        ITask task = new PlayerTask();
        var taskSettings = config.Tasks(task.TaskName)
            ?? throw new ArgumentException($"settings.yaml has no tasks.{task.TaskName} entry");

        var system = options.System ?? task.SystemPrompt(taskSettings, config.UserPromptsDir, Config.PromptsDir);
        var model = options.Model ?? task.Model(taskSettings);
        var backendName = options.Backend ?? task.Provider(taskSettings);
        var apiKey = options.ApiKey ?? ResolveApiKey(backendName);

        ILlmBackend backend = backendName switch
        {
            "anthropic" => new AnthropicBackend(apiKey ?? throw new ArgumentException("ANTHROPIC_API_KEY is not set"), model),
            _ => throw new ArgumentException($"unsupported backend '{backendName}' (only 'anthropic' is ported so far)"),
        };

        var contextWindow = options.ContextWindow ?? backend.ContextWindow;
        var workingDir = options.DisableWorkingDir ? null : (options.WorkingDir ?? Directory.GetCurrentDirectory());

        var context = new Context(task, system, contextWindow, workingDir, config.AgentCompactionThreshold);
        var registry = new Registry(context);

        var logger = new Logger(Path.Combine(config.Dir, "sessions"), log: options.Log, snapshot: new Dictionary<string, object?>
        {
            ["task"] = task.TaskName,
            ["provider"] = backendName,
            ["model"] = model,
            ["context_window"] = contextWindow,
            ["max_turn_tokens"] = config.AgentMaxTurnTokens,
        });

        var mcpClients = new List<McpClient>();
        foreach (var (serverName, rawOptions) in config.McpServers)
        {
            if (rawOptions is not IReadOnlyDictionary<string, object?> serverConfig) continue;

            var command = serverConfig.GetValueOrDefault("command") as string
                ?? throw new ArgumentException($"mcp_servers.{serverName}.command is required");
            var args = (serverConfig.GetValueOrDefault("args") as IEnumerable<object?>)
                ?.Select(a => a?.ToString() ?? string.Empty).ToList();
            var env = (serverConfig.GetValueOrDefault("env") as IReadOnlyDictionary<string, object?>)
                ?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            var required = serverConfig.GetValueOrDefault("required") is not false;
            var prefix = serverConfig.GetValueOrDefault("prefix") as string;

            var client = new McpClient(serverName, command, args, env);
            try
            {
                await client.StartAsync(cancellationToken);
                await McpToolRegistrar.RegisterAsync(registry, client, prefix, cancellationToken);
                mcpClients.Add(client);
            }
            catch (McpClient.McpException) when (!required)
            {
                await Console.Error.WriteLineAsync($"[warning] MCP server '{serverName}' is unavailable and was skipped (not required)");
                await client.DisposeAsync();
            }
            catch
            {
                foreach (var started in mcpClients) await started.DisposeAsync();
                await client.DisposeAsync();
                throw;
            }
        }

        options.Configure?.Invoke(new RunDsl(registry));

        var builder = new PromptBuilder(context, backend);
        var httpClient = new HttpClient();
        var apiClient = new Client(builder, httpClient);
        var resolvedMaxOutputTokens = options.MaxOutputTokens ?? task.MaxOutputTokens(taskSettings);

        Agent AgentFactory() => new(
            context, registry, builder, apiClient, logger, taskSettings,
            maxOutputTokens: resolvedMaxOutputTokens,
            maxTurnTokens: config.AgentMaxTurnTokens);

        return new BoukenshaSession(context, registry, AgentFactory, logger, mcpClients, backendName, model);
    }

    private static string? ResolveApiKey(string backend) => backend switch
    {
        "anthropic" => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        "openai" => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        "gemini" => Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
        _ => Environment.GetEnvironmentVariable("OLLAMA_API_KEY"),
    };
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds.
- [ ] Commit.

---

## Task 16: `Boukensha.Console` — `Repl`

**Files:**
- Create: `src/Boukensha.Console/Repl.cs`

**Consumes:** `BoukenshaSession`, `ApiException`, `LoopException` (Task 15).
**Produces:** `Repl(BoukenshaSession, string provider, string model, string version, string configDir, IReadOnlyList<string> mcpServerNames)` with `OnOutput(Action<string>)`, `Banner()`, `HandleCommand(string) -> string?`, `RunTurnAsync(string)`, `StartAsync()`.

- [ ] Write `src/Boukensha.Console/Repl.cs`:
```csharp
using Boukensha.Core;

namespace Boukensha.Console;

public sealed class Repl(
    BoukenshaSession session,
    string provider,
    string model,
    string version,
    string configDir,
    IReadOnlyList<string> mcpServerNames)
{
    private const string Prompt = "boukensha> ";
    private static readonly string Help = string.Join('\n',
        "Commands:",
        "  /help     show this message",
        "  /clear    clear the conversation",
        "  /compact  manually compact the context",
        "  /exit     quit (also /quit)");

    private int _turn;
    private Action<string>? _outputSink;

    public void OnOutput(Action<string> sink) => _outputSink = sink;

    public string Banner()
    {
        var configStatus = Directory.Exists(configDir) ? "found" : "missing";
        var servers = mcpServerNames.Count > 0 ? string.Join(", ", mcpServerNames) : "(none configured)";
        return string.Join('\n',
            $"boukensha v{version}",
            $"config: {configDir} ({configStatus})",
            $"provider/model: {provider}/{model}",
            $"mcp servers: {servers}");
    }

    public string? HandleCommand(string input) => input switch
    {
        "/exit" or "/quit" => Quit(),
        "/help" => Command(Help),
        "/clear" => ClearContext(),
        "/compact" => CompactContext(),
        _ => null,
    };

    private string Quit()
    {
        Output("Goodbye.");
        return "quit";
    }

    private string Command(string text)
    {
        Output(text);
        return "command";
    }

    private string ClearContext()
    {
        session.Context.ClearMessages();
        _turn = 0;
        return Command("(cleared)");
    }

    private string CompactContext()
    {
        var dropped = session.Context.CompactMessages();
        return Command($"(compacted context — {dropped} messages dropped)");
    }

    public async Task RunTurnAsync(string input)
    {
        _turn++;
        session.Logger.Turn(_turn);
        session.Context.AddMessage("user", input);
        var agent = session.AgentFactory();
        try
        {
            var result = await agent.RunAsync();
            Output(string.Empty);
            Output(result);
        }
        catch (Exception e) when (e is ApiException or LoopException)
        {
            Output($"[error] {e.Message}");
        }
    }

    public async Task StartAsync()
    {
        Output(Banner());
        while (true)
        {
            if (_outputSink is null) System.Console.Write(Prompt);
            var line = System.Console.ReadLine();
            if (line is null) break;

            line = line.Trim();
            if (line.Length == 0) continue;

            var commandResult = HandleCommand(line);
            if (commandResult == "quit") break;
            if (commandResult is not null) continue;

            await RunTurnAsync(line);
        }
    }

    private void Output(string text)
    {
        if (_outputSink is not null) _outputSink(text);
        else System.Console.WriteLine(text);
    }
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds (expect it to fail only on the still-missing `Program.cs` entrypoint reference — resolved in Task 17).
- [ ] Commit.

---

## Task 17: `Boukensha.Console` — TUI + `Program.cs`

**Files:**
- Create: `src/Boukensha.Console/Tui/TuiOutputSink.cs`
- Modify: `src/Boukensha.Console/Program.cs`

**Consumes:** `Context`, `BoukenshaHost`, `BoukenshaOptions`, `Repl` (Tasks 4, 15, 16).
**Produces:** `TuiOutputSink(Context) : IDisposable` with `Start()`, `Output(string)`, `OnLogEvent(IReadOnlyDictionary<string,object?>)`; console entrypoint honoring `--no-tui` / `BOUKENSHA_TUI=0`.

- [ ] Write `src/Boukensha.Console/Tui/TuiOutputSink.cs`:
```csharp
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
```
- [ ] Rewrite `src/Boukensha.Console/Program.cs`:
```csharp
using Boukensha.Core;
using Boukensha.Console;
using Boukensha.Console.Tui;

const string Version = "0.1.0";

var noTui = args.Contains("--no-tui") || Environment.GetEnvironmentVariable("BOUKENSHA_TUI") == "0";

await using var session = await BoukenshaHost.BuildAsync(new BoukenshaOptions());
var config = new Config();
var repl = new Repl(session, session.Provider, session.Model, Version, config.Dir, session.McpServerNames);

if (noTui)
{
    await repl.StartAsync();
}
else
{
    using var tui = new TuiOutputSink(session.Context);
    tui.Start();
    session.Logger.Subscribe(tui.OnLogEvent);
    repl.OnOutput(tui.Output);
    await repl.StartAsync();
}
```
- [ ] Verify: `dotnet build week2_capable/dotnet/Boukensha.slnx` succeeds. **This task is the most likely to need iteration** — `Spectre.Console`'s `Live`/`Layout` API surface can differ slightly by installed version; if `AnsiConsole.Live(...)`, `LiveDisplayContext.UpdateTarget`/`Refresh`, or `Layout.SplitRows`/`.Ratio`/`.Size` don't match the installed package's actual signatures, fix by consulting the compiler errors and the installed package's IntelliSense/decompiled signatures directly (`dotnet-claude-kit`'s conventions favor compiler-driven fixes over guessing) — adjust `TuiOutputSink.cs` until it compiles, keeping the same responsibility (live transcript panel + context-usage gauge, updated on `Output`/`OnLogEvent`).
- [ ] Commit.

---

## Task 18: End-to-end verification

**Files:** none (verification only).

- [ ] Run the full test suite: `dotnet test week2_capable/dotnet/Boukensha.slnx` — expect all tests pass.
- [ ] Run `dotnet build week2_capable/dotnet/Boukensha.slnx -warnaserror` (or without `-warnaserror` if the template introduces unavoidable nullable warnings from generated code) and resolve any remaining warnings in the code written in Tasks 2–17.
- [ ] Dry-run check (no live network/MCP calls, no API key required): run `dotnet run --project week2_capable/dotnet/src/Boukensha.Console -- --no-tui` with `BOUKENSHA_DIR` pointed at a directory with **no** `settings.yaml` — expect a clear `ArgumentException` (`settings.yaml has no tasks.player entry`), proving the config-resolution chain fails loudly rather than silently.
- [x] **Blocker check — asked the user before proceeding past this point**: confirmed all prerequisites were actually already present in this checkout — `ANTHROPIC_API_KEY` in `.boukensha/.env`, `.boukensha/settings.yaml` with `tasks.player` + `mcp_servers.mud` pointing at the real `week0_explore/mud_manager/bin/mud-manager` binary, and a MUD server reachable at `localhost:4000` (confirmed via `Test-NetConnection`). User approved proceeding with a live, billed API call.
- [x] Live verification: ran `BOUKENSHA_DIR=".../.boukensha" dotnet run --project week2_capable/dotnet/src/Boukensha.Console -- --no-tui` piping in one turn ("look around and tell me what you see"). Confirmed: banner showed `provider/model: anthropic/claude-haiku-4-5` and `mcp servers: mud`; the turn completed with a coherent final answer describing the actual MUD room (Sewer, First Level) and its exits; the session JSONL log (`.boukensha/sessions/20260809T140133Z-4a8bbb85.jsonl`) contains the full expected phase sequence — `session_start` → `turn` → repeated `iteration`/`prompt`/`plan`/`tool_call`/`tool_result` cycles (multiple MCP tool calls against the live `mud-manager` server) → final `prompt`/`response`/`turn_end`; the `response` event has correctly normalized `stop_reason:"end_turn"`, real `usage` token counts, and a computed `cost_usd:0.004275`. Full functional parity confirmed against the real stack — no gaps found.
- [x] `dotnet test Boukensha.slnx` — all 19 tests pass. `dotnet clean && dotnet build Boukensha.slnx` — 0 warnings, 0 errors.
- [x] Updated `docs/plans/week_2/dotnet_port.md`'s status line to reflect completion.
- [ ] Commit (final) — commit this task's doc updates.
