# .NET Port of Boukensha — Design

Status: implemented and live-verified (2026-08-10) — see `docs/plans/week_2/dotnet_port_plan.md` for the task-by-task build log
Owner: Sam Alhambra

## Purpose

Port the `boukensha` agentic-loop framework to .NET 10/C# 14 as a **permanent, maintained baseline** for the week 2 work (observability layer, basic memory, token-usage optimization) described in `docs/journal/week2.md`. This is not a tutorial-parity exercise like the Ruby→Python port — the .NET version is meant to become the platform week 2's actual features get built on.

## Scope decision: which source to port

Two prior snapshots exist:
- `week1_baseline/ruby/12_context` — the tutorial's real step 12. Reverted to direct tool registration (`tools/mud.rb`, `file_system.rb`, `shell.rb`); **no MCP, no Tasks**. Confirmed to be an authoring one-off (branched from an old snapshot, MCP rewrite never re-merged) per `docs/plans/python_port/IMPLEMENTATION.md`'s per-step decision log.
- `week1_baseline/python/12_context` — a deliberate superset. Kept the MCP-host + `Tasks` architecture from steps 00–11 and layered step 12's context/token-management on top.

**Decision:** target Python's superset (MCP tool-hosting + Tasks + context/token management), since week 2's plans (room_inspector delegation, MCP calls to `mud_manager`, tool permissions) depend on the MCP-host model that Ruby's step 12 dropped.

## Key decisions (confirmed with user)

| Question | Decision |
|---|---|
| Purpose | New permanent baseline, not a throwaway spike |
| Source scope | Python's superset (MCP + Tasks + context mgmt) |
| TUI | Include one, but toggleable off (`--no-tui` / `BOUKENSHA_TUI=0`) |
| MUD connectivity | Reuse the existing Ruby `mud_manager` process via MCP — do not reimplement telnet/MUD protocol handling in .NET |
| MCP client | Hand-rolled (subprocess + stdio JSON-RPC), matching Ruby/Python's minimal-dependency precedent — no MCP SDK NuGet dependency |
| Backend scope | Anthropic only initially; other backends (OpenAI/Gemini/Ollama/OllamaCloud) deferred |
| Solution shape | Class library (`Boukensha.Core`) + console host (`Boukensha.Console`) |
| .NET version | .NET 10 / C# 14 (confirmed installed: SDK 10.0.302) |
| Verification bar | Functional parity (agent runs the loop, connects via MCP, talks to Anthropic, tracks/compacts context correctly) — not byte-for-byte transcript diffing against Ruby/Python |
| Repo location | `week2_capable/dotnet/`, sibling to `ruby/` and `python/` |

## Solution layout

```
week2_capable/dotnet/
  Boukensha.slnx
  src/
    Boukensha.Core/
      Boukensha.Core.csproj
      Config.cs
      Context.cs
      Message.cs               # Message record + ContentBlock discriminated union
      Tool.cs
      Registry.cs
      PromptBuilder.cs
      Client.cs                  # HTTP transport (HttpClient + hand-rolled retry)
      Agent.cs
      Logger.cs
      Errors.cs
      RunDsl.cs
      BoukenshaHost.cs           # composition root (wiring, matches run()/repl() defaulting chain)
      Backends/
        ILlmBackend.cs
        AnthropicBackend.cs
      Mcp/
        McpClient.cs
        McpToolRegistrar.cs
      Tasks/
        ITask.cs
        TaskSettings.cs
        PlayerTask.cs
    Boukensha.Console/
      Boukensha.Console.csproj
      Program.cs
      Repl.cs
      Tui/
        TuiOutputSink.cs         # Spectre.Console live view
  tests/
    Boukensha.Core.Tests/
      Boukensha.Core.Tests.csproj
```

## Core type mapping

| Python | C# | Notes |
|---|---|---|
| `Message` (`@dataclass`) | `record Message(string Role, MessageContent Content, string? ToolUseId = null)` | See content modeling below |
| `Tool` (`@dataclass`) | `record ToolDefinition(string Name, string Description, IReadOnlyDictionary<string, ToolParameter> Parameters, Func<IReadOnlyDictionary<string, object?>, Task<string>> Handler)` | **Renamed from `Tool`** — `Registry.Tool(...)`/`RunDsl.Tool(...)` (matching Ruby/Python's `registry.tool(...)` method name, PascalCased) would otherwise collide with a type also named `Tool` in the same class, since C# resolves an unqualified `Tool` inside `Registry` to the method group first. Same collision category as Python's `model_info`/`model_info_for` rename (see `docs/plans/python_port/IMPLEMENTATION.md`) — documented here rather than silently worked around |
| `Context` | `class Context` | `UsageFraction`, `UsagePct`, `ToolCount` get-only properties; `RegisterTool`, `AddMessage`, `UpdateTokens`, `AddTurnTokens`, `ResetTurnTokens`, `NeedsCompaction`, `CompactMessages`, `ClearMessages` methods. **Two-counter token model kept distinct**: `CurrentTokens` (overwritten each response — window pressure) vs `TurnTokens` (accumulated — spend), not merged into one field |
| `Registry` | `class Registry` | `Tool(...)`, `Registered(name)`, `Task<string> DispatchAsync(name, args)` |
| `errors.py` (4 classes) | `UnknownToolException`, `ApiException`, `LoopException`, `UnsupportedModelException` (all `: Exception`) | `LoopException` ported as an unused type, matching Ruby/Python (reserved for future loop-guard logic) |
| `Logger` | `class Logger : IDisposable` | JSONL-per-line, append-mode, flush-per-write. `Subscribe(Action<LogEvent>)` for a live event tap (drives the TUI) |
| `Config` | `class Config` | Loads `settings.yaml` (YamlDotNet) + `.env` (hand-rolled `KEY=VALUE` parser) from `BOUKENSHA_DIR` / `~/.boukensha`. **Reuses the same `.boukensha/settings.yaml` format** Ruby/Python read — one config directory works across all three languages |
| `Tasks::Base`/`Player` | `interface ITask` + `class PlayerTask : ITask` | Python's classmethods-over-a-dict become instance methods over an injected `TaskSettings` DTO |
| `PromptBuilder` | `class PromptBuilder` | Thin delegator to `ILlmBackend` |
| `Client` | `class Client` | `HttpClient`-based; hand-rolled retry loop (exponential backoff, retryable-status-code set), not Polly — stays consistent with the "no unnecessary dependency" precedent set for MCP |
| `Agent` | `class Agent` | Same loop shape: iteration/token limit checks each pass → wrap-up short-circuit; `HandleToolCallsAsync` catches all exceptions from dispatch and turns them into an error-string tool result |
| `RunDSL` | `class RunDsl` | `Tool(...)` passthrough to `Registry` |
| `models.py` | *(removed, not ported as a separate table)* | Each `ILlmBackend` owns its own model catalog exclusively — fixes the two-sources-of-truth smell where Python's `models.py` (stale 200K) disagreed with `backends/anthropic.py`'s real catalog (1M for sonnet/opus) |

### Content-block modeling

Python stores `Message.content` as a loose `str | list[dict]`. The C# port introduces a proper discriminated union instead:

```csharp
abstract record ContentBlock;
record TextBlock(string Text) : ContentBlock;
record ToolUseBlock(string Id, string Name, IReadOnlyDictionary<string, object?> Input) : ContentBlock;
record ToolResultBlock(string ToolUseId, string Content) : ContentBlock;
record ReasoningBlock(string Text, bool Redacted = false, string? Signature = null) : ContentBlock;
```

`MessageContent` wraps either a plain `string` (most user/system messages) or `IReadOnlyList<ContentBlock>` (assistant tool-use turns), with factory methods for each — no `object`/`dynamic`.

## Wiring (`Program.cs` / `BoukenshaHost`)

Mirrors `boukensha.run()`/`repl()`'s defaulting chain: task settings → system prompt → model → context window → provider → API key from env → working dir. Implemented as an explicit composition method, `BoukenshaHost.BuildAsync(BoukenshaOptions)`, returning a fully-wired `(Context, Registry, Func<Agent>, Logger, IReadOnlyList<McpClient>)`, called by both the one-shot run path and `Repl.StartAsync`. No DI container — a single composition root is enough at this scale.

## Backend

Only `AnthropicBackend : ILlmBackend` ships initially:

```csharp
interface ILlmBackend
{
    int ContextWindow { get; }
    IReadOnlyDictionary<string,string> Headers { get; }
    string Url { get; }
    IReadOnlyList<object> ToMessages(IReadOnlyList<Message> messages);
    IReadOnlyList<object> ToTools(IReadOnlyDictionary<string, ToolDefinition> tools);
    JsonObject ToPayload(Context context, int maxOutputTokens, IReadOnlyList<object>? toolsOverride = null);
    ParsedResponse ParseResponse(JsonNode response);
    double? EstimateCost(int inputTokens, int outputTokens);
}
```

`AnthropicBackend` carries its own model catalog (3 entries — `claude-haiku-4-5` 200K, `claude-sonnet-4-6` and `claude-opus-4-8` at 1,000,000 context window, matching Python's real `backends/anthropic.py`, not the stale `models.py` table). Implements the thinking-block ↔ reasoning-block normalize/denormalize round-trip; signatures must be preserved unaltered (the API rejects a continued conversation if a thinking-block signature changes).

Deferred, not ported this pass: OpenAI, Gemini, Ollama, OllamaCloud backends; their usage-normalization fallback chain (irrelevant with one backend).

## MCP client

`McpClient` — `System.Diagnostics.Process` spawning `command`+`args`+merged `env`; newline-delimited JSON-RPC 2.0 over stdin/stdout (matching Python's framing, not LSP `Content-Length` framing); a background task draining stderr into a buffer; `StartAsync()` performing the `initialize`/`notifications/initialized` handshake; `ToolsListAsync()`; `ToolsCallAsync(name, args)`; `StopAsync()` closing pipes and waiting on the process with a timeout, same shutdown sequence as Python's.

`McpToolRegistrar.Register(registry, client, prefix)` replicates `tools/mcp.py`'s JSON-Schema→boukensha-parameters flattening and prefix-collision guard (raises on a name collision, message names the offending server).

This talks to the **existing Ruby `mud-manager --mcp` binary** (`week0_explore/mud_manager`) exactly as configured in `.boukensha/settings.yaml`'s `mcp_servers.mud` block. No MUD telnet/protocol code is written in .NET.

## Config format & new dependencies

Two new NuGet packages, narrowly scoped:
- **YamlDotNet** — parses `settings.yaml` in the same format Ruby/Python read, so `.boukensha/` (settings, prompts, sessions) is shared across all three ports rather than forked into `appsettings.json`.
- **Spectre.Console** — optional TUI (live conversation panel + context-usage gauge + status bar), toggled off via `--no-tui` / `BOUKENSHA_TUI=0`, falling back to the same plain-line output the REPL uses via the same `IOutputSink`/`Logger.Subscribe` seam Python's `Repl.on_output`/`Logger.subscribe` establish. Chosen over Terminal.Gui because the Ruby TUI's real feature set (live scrolling transcript + a status/gauge line) maps onto Spectre's `Live`/`Layout` primitives without a full windowing model.

Everything else (JSON, HTTP, process spawning, file I/O) stays BCL-only.

## Testing

Ruby and Python ship zero automated tests (transcript-diff parity instead). Since this port is meant to be maintained rather than a tutorial snapshot, a minimal `Boukensha.Core.Tests` xUnit project covers the trickiest logic: `Context` compaction math (40%-drop, min-2-kept, `CurrentTokens` reset), `Registry` dispatch/collision errors, MCP JSON-RPC framing (mocked process I/O), and `AnthropicBackend` payload/parse round-tripping (including the thinking-block signature round-trip). Not a broad suite — matches the "functional parity, not byte parity" bar.

## Out of scope for this pass

- OpenAI/Gemini/Ollama/OllamaCloud backends
- Non-Anthropic usage normalization
- Reimplementing `mud_manager`/MUD telnet protocol in .NET
