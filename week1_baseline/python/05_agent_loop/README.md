# 05 · The Agent Loop (Python port)

Python port of `ruby/05_agent_loop`. Adds `Agent` — the core agentic loop —
to the shared `boukensha` package. See `ruby/05_agent_loop/README.md` for
the full design rationale. This file only documents the Python-specific
parts, and corrects two stale spots in the Ruby README (see "Notes on the
Ruby README" at the bottom).

Everything built before this — the structs, the registry, the prompt
builder, the client — was setup. The loop is where the agent actually does
work: call the API, check `stop_reason`, dispatch tool calls back into the
registry, append results to the context, and repeat until `end_turn` or
`max_iterations` is hit.

## New/Changed in This Step

| File | Change |
|---|---|
| `boukensha/agent.py` | **New** — the agent loop |
| `boukensha/errors.py` | Added `LoopError` (declared for a future step — not raised anywhere yet) |
| `boukensha/tasks/base.py` | Added `max_iterations(settings)` / `max_output_tokens(settings)`, backed by a new `_integer_setting` helper |
| `boukensha/client.py` | `call()` gained a `tools=` override, passed through to the payload |
| `boukensha/prompt_builder.py` | Added `parse_response(response)`, delegating to the backend; `to_api_payload()` gained a `tools=` override |
| `boukensha/backends/anthropic.py` | Added `parse_response` |
| `boukensha/backends/gemini.py` | Added `parse_response` and private `_assistant_parts` |
| `boukensha/backends/ollama.py`, `ollama_cloud.py` | Added `parse_response` and private `_assistant_message` |
| `boukensha/backends/openai.py` | Added `parse_response` and private `_assistant_message` |

## How It Works

```
send messages to API
        ↓
stop_reason == "tool_use"?
    yes → extract tool calls
        → dispatch each tool via Registry
        → inject results as tool_result messages
        → go back to top
    no  → return final text response
```

## `boukensha.Agent`

| Method | Description |
|---|---|
| `run()` | Starts the loop and returns the final text response when the agent is done |

## Every Backend Speaks the Same Normalized Shape

Five providers means five different response formats — Anthropic nests tool
calls inside `content`, Ollama puts them in `message["tool_calls"]`, OpenAI
nests them under `choices[0]["message"]["tool_calls"]`, and Gemini calls
them `functionCall` parts. Rather than teach `Agent` about each of these,
every backend implements `parse_response`, converting its raw response into
one common shape:

```python
{
    "stop_reason": "tool_use",  # or "end_turn"
    "content": [
        {"type": "text", "text": "..."},
        {"type": "tool_use", "id": "...", "name": "...", "input": {...}},
    ],
}
```

`Agent` only ever sees this shape — it calls `self.builder.parse_response(response)`,
which delegates to the backend, and never inspects a raw provider response.

The conversion also runs in reverse. When the conversation history is
replayed on the next request, Gemini (`_assistant_parts`), Ollama, Ollama
Cloud, and OpenAI (`_assistant_message` on each) rebuild a provider-specific
assistant message from the normalized `content` blocks — the inverse of
`parse_response`. Anthropic's `content` array doubles as both the
normalized shape and the wire format, so it needs no extra conversion.

**Tool call IDs aren't universal.** Anthropic and OpenAI assign every tool
call a unique `id`, echoed back in the `tool_result`. Ollama, Ollama Cloud,
and Gemini don't assign call ids at all — those backends reuse the tool's
`name` as its `id` and match the `tool_result` back to the call by name.

## Task Configuration

```yaml
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
    prompt_override:
      system: true
    max_iterations: 25
    max_output_tokens: 1024
```

`max_iterations` controls model round-trips per turn before wind-down, and
`max_output_tokens` is passed to each model reply. Both are optional —
`Tasks::Base.max_iterations`/`max_output_tokens` fall back to
`DEFAULT_MAX_ITERATIONS = 25` / `DEFAULT_MAX_OUTPUT_TOKENS = 1024` when
`settings.yaml` doesn't set them.

## Considerations

**The assistant message must be stored before the tool result.** The
Anthropic API requires the assistant's `tool_use` block to appear in the
message history before its corresponding `tool_result`. `Agent._handle_tool_calls`
handles this — it appends the assistant message first, then dispatches and
appends each tool result. Get the order wrong and the API rejects the
request.

**The model can call multiple tools in one turn.** The loop handles this by
iterating over all `tool_use` blocks in a single response before making the
next API call.

**`MAX_ITERATIONS` is a turn ceiling, not a hard cap.** A poorly prompted
agent can loop forever if the model keeps calling tools. `Agent` stops
starting new work after the configured ceiling (25 by default) and makes
one short wrap-up call with tools disabled (`client.call(tools=[], ...)`).
This keeps the turn bounded while still returning a useful final response.

**The agent has no way to stop itself.** The model signals it is done via
`stop_reason: "end_turn"`. `Agent` watches for that signal and exits the
loop. The agent never decides unilaterally to stop.

## Windows Console Encoding

`Agent`'s tool-call trace prints a `→` (U+2192) character. On Windows,
`sys.stdout` defaults to the console's ANSI codepage rather than UTF-8
whenever it isn't attached to an interactive terminal (e.g. piped or
redirected — exactly what parity checks do), which raises
`UnicodeEncodeError` the first time a tool call is printed.
`examples/example.py` calls `sys.stdout.reconfigure(encoding="utf-8")`
before printing anything, so this runs the same way piped or interactive,
on any platform. This has no Ruby equivalent to port — Ruby's `puts`
handled this transparently in this environment.

## Run Example

```bash
./week1_baseline/bin/python/05_agent_loop
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 05_agent_loop/examples/example.py
```

**This makes multiple real, billed API calls** — at least one, typically
2-4 (one per loop iteration), since the example's task nudges the model
toward calling `read_file`/`list_directory` before answering.

## Notes on the Ruby README

`ruby/05_agent_loop/README.md` has two inaccuracies worth flagging so they
don't get assumed as ground truth:

1. Its "New Files" / "Updated Files" tables list several files (`backends/base.rb`,
   `tasks/base.rb`, `tasks/player.rb`, `backends/openai.rb`, `backends/gemini.rb`,
   `backends/ollama_cloud.rb`, `context.rb`) that were actually introduced or
   last changed in earlier steps (03/04), not this one — likely reused
   language from an earlier README. The table above was built from a direct
   `diff` between `ruby/04_api_client` and `ruby/05_agent_loop`.
2. Its "What the Loop Looks Like" sample shows `[iteration 1]` with no
   denominator, but the actual code (`agent.rb:35`) prints
   `"[iteration #{@iteration}/#{@max_iterations}]"` — real output looks
   like `[iteration 1/25]`. This port matches the code, not the README.
