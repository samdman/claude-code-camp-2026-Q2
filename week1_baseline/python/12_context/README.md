# 12 · Context Management (Python port)

**This step deliberately does not mirror `ruby/12_context`'s file
layout.** `ruby/12_context` reverts to the pre-MCP direct-tool-registration
model (`tools/mud.rb`/`tools/file_system.rb`/`tools/shell.rb`, no `mcp/`,
no `tasks/`) — a real regression relative to steps 10–11's MCP-host
architecture, despite `ruby/../ITERATIONS.md`'s step-12 section claiming
otherwise (verified false against the actual Ruby source). This port takes
the opposite path: it keeps Python's existing MCP-host + `Tasks`
architecture from steps 00–11 fully intact and layers only step 12's
genuinely new capabilities on top. Result: this step's Python package has
the union of every capability shipped 00–12 — MCP tool hosting,
`Tasks`-based per-task settings, *and* context management — which
`ruby/12_context` itself does not have. See
`docs/plans/python_port/12_context`'s Global Constraint 1 for the full
reasoning.

Practical consequence: `python/12_context/examples/example.py` still uses
`mcp_servers:` (unchanged from step 11) to reach the MUD, while
`ruby/12_context/examples/example.rb` registers `Tools::Mud` directly. If
you're comparing the two folders side by side, that's why the tool source
looks different — not an oversight.

## What this step adds

### `boukensha.models` — new module

A static model → `context_window` capability table
(`boukensha.models.context_window(model)`), used to size `Context`
correctly before a backend is constructed. Unknown models fall back to a
conservative 32,000-token default.

### Accurate context tracking

`Context` now tracks two distinct token counts, alongside the `task`-based
settings resolution it already had:

| Attribute | What it measures |
|---|---|
| `context_window` | The model's maximum input token capacity (from `boukensha.models`) |
| `current_tokens` | Tokens actually used in the most recent API response (`usage.input_tokens`) |

`turn_tokens` is a separate cumulative per-turn spend counter (distinct
from `current_tokens`, which tracks window *pressure*, not spend).

### Auto-compaction

At the start of each `Agent.run()`, if `current_tokens / context_window`
crosses `Config.agent_compaction_threshold` (default 0.85), the oldest
~40% of messages are dropped (keeping at least 2) before the next API
call, and a `compaction` event is logged. `Context.compact_messages()`
does the same thing on demand — now wired into the REPL's new `/compact`
command.

### A second circuit breaker — `max_turn_tokens`

`Agent` now stops a turn on whichever trips first: `max_iterations` (tool-
call count, from `Tasks`-scoped settings, unchanged) or the new
`max_turn_tokens` (cumulative input+output tokens spent this turn, from a
new top-level `agent:` settings block, default 60,000). Both are *trigger
thresholds*: hitting one stops new work and makes exactly one terminal
wind-down call, not a hard abort.

### Normalized reasoning blocks

Every backend now surfaces provider-specific "thinking" output (Anthropic
`thinking`/`redacted_thinking`, Gemini `thought`/`thoughtSignature`,
Ollama/OllamaCloud `message["thinking"]`) as a common
`{"type": "reasoning", ...}` content block, logged via `Logger.reasoning()`
and, when present, a `Logger.plan()` event for any preamble text
accompanying a tool call.

### `stop_reason` logging fix

`Agent._log_response()` now logs the *normalized* `stop_reason`
(`"tool_use"`/`"end_turn"`) instead of reading it off the raw API
response. That field only ever existed in Anthropic's raw response shape —
every non-Anthropic backend (Gemini, Ollama, OllamaCloud, and now OpenAI's
`/v1/responses`) had silently logged `stop_reason: null` since step 05.
This is a real correctness fix, independent of the MCP/`Tasks` question,
so it's included here even though the broader Ruby 12 logging rewrite
(dropping cost metadata) is not.

### OpenAI backend moved to `/v1/responses`

`gpt-5.x` rejects `reasoning_effort` + tools on `/v1/chat/completions`.
`backends/openai.py` now targets the Responses API: messages become
`input` items, the system prompt becomes a top-level `instructions`
string, tool defs are flat (no `function:` wrapper), and tool results
round-trip via `function_call_output` items matched by `call_id`.

### Model catalog updates

Real data changes carried over regardless of the architecture question:
Anthropic drops the stale `claude-haiku-4-5-20251001` entry; Gemini trims
to its 2 current models; Ollama trims from 9 stale local-model entries
down to 1 (`gemma4:e4b`); OllamaCloud drops `gpt-5.4`, adds
`gpt-5.4-nano`.

### `boukensha.run()` / `.repl()` — new `context_window:` keyword

```python
boukensha.run(task="...", context_window=128_000)  # override for a smaller model
```

Defaults to `boukensha.models.context_window(model)` when not given.

### `/compact` REPL command

```
boukensha> /compact
(compacted context — 12 messages dropped)
```

## What did *not* change

`Config.tasks()`, `Config.mcp_servers`, `Config.user_prompts_dir`,
`Config.PROMPTS_DIR`, `Registry.registered()`, the `mcp/` package, and
`tools/mcp.py` are all untouched — this step adds two new `Config`
properties (`agent_max_turn_tokens`, `agent_compaction_threshold`) and
nothing is removed.

## Running it

```bash
bash bin/python/12_context
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 12_context/examples/example.py
```

Same MUD demo as steps 10–11 — connects via the configured
`mcp_servers:` block, so stdout stays byte-similar to step 11's. The new
context-management behavior (token tracking, compaction, reasoning
events) is visible in the session's JSONL log
(`~/.boukensha/sessions/<id>.jsonl`), not in this script's stdout.

To exercise `/compact` directly:

```bash
printf '/compact\n/exit\n' | .venv/Scripts/python -c "import boukensha; boukensha.repl()"
```
