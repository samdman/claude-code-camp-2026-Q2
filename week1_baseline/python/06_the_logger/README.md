# 06 · The Logger (Python port)

Python port of `ruby/06_the_logger`. Adds `boukensha.Logger` — a file-backed
structured JSONL event logger — to the shared `boukensha` package, and wires
`Agent` to call it at every phase of the loop. See
`ruby/06_the_logger/README.md` for the original design rationale. This file
documents the Python-specific parts and corrects one stale spot in the Ruby
README (see "Notes on the Ruby README" at the bottom).

`Logger` is a file logger, not user-facing display output. As of this step,
`Agent` no longer prints iteration/tool-call/tool-result traces to stdout at
all — that tracing moved entirely into the JSONL log (see "What Changed From
Step 05" below).

## New/Changed in This Step

| File | Change |
|---|---|
| `boukensha/logger.py` | **New** — `Logger`, structured JSONL event logging |
| `boukensha/agent.py` | `Agent` gains a `logger=` constructor param (defaults to a fresh `Logger()` per instance); calls the logger at every phase; catches and logs tool-dispatch exceptions instead of letting them propagate; **all `print()` tracing removed** |
| `boukensha/config.py` | Removed unused `mud_host`/`mud_port`/`mud_username`/`mud_password` properties (dead code — Ruby dropped them too) |
| `boukensha/errors.py` | Removed unused `LoopError` (was dead code added in step 05, Ruby has since dropped it) |
| `boukensha/__init__.py` | Added module-level state: `config()` (memoized `Config` singleton), `quiet()`/`loud()`/`is_quiet()`, `debug()`/`is_debug()`; exports `Logger` |

## Session Logs

Each `Logger` instance creates a session id and writes one log file for that session:

```text
.boukensha/sessions/<session-id>.jsonl
```

Every line is a complete JSON object with `session_id`, `at`, and `phase` fields, plus phase-specific data — grep/tail friendly, machine readable.

```json
{"phase":"session_start","session_id":"20260808T132741Z-66d36d13","at":"2026-08-09T01:27:41+12:00"}
{"phase":"iteration","n":1,"max":25,"session_id":"20260808T132741Z-66d36d13","at":"2026-08-09T01:27:41+12:00"}
```

`response` lines include the active task, provider, model, normalized token counts, and estimated USD cost when the backend has token pricing data:

```json
{"phase":"response","text":"...","usage":{...},"stop_reason":"end_turn","task":"player","provider":"anthropic","model":"claude-haiku-4-5","usage_unit":"tokens","input_tokens":1584,"output_tokens":398,"cost_usd":0.003574,"session_id":"...","at":"..."}
```

## Logger API

A plain object with one method per phase (real signatures — see "Notes on the Ruby README" below for where the Ruby README's table drifted):

| Method | Phase | Logs |
|---|---|---|
| `iteration(n, max)` | `iteration` | loop counter and ceiling |
| `limit_reached(kind, n, max)` | `limit_reached` | iteration ceiling hit, before wind-down |
| `prompt(messages, tools)` | `prompt` | message count/roles, tool count/names |
| `tool_call(name, args)` | `tool_call` | tool name and arguments |
| `tool_result(name, result, ok, error)` | `tool_result` | full (untruncated) tool result, success flag, error message if any |
| `response(text, usage, stop_reason, task, backend)` | `response` | response text, token usage, task/provider/model, estimated cost |
| `raw(data)` | `raw` | raw provider response, only when `boukensha.debug()` was called |
| `turn_end(reason, iterations, tokens)` | `turn_end` | why/when the turn ended |

## What Changed From Step 05

Step 05's `Agent` printed `[iteration N/max]`, `  tool call → name(args)`, and
`  tool result → ...` directly to stdout. As of this step, **none of that
prints anymore** — every one of those events now goes to the JSONL session
log instead, in full (no 61-character truncation like the old stdout print
had). Running `python/06_the_logger/examples/example.py` now only prints the
deterministic preamble and the final response; everything about the loop's
progress lives in `.boukensha/sessions/<session-id>.jsonl`.

## Task Configuration

Unchanged in meaning from step 05:

```yaml
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
    prompt_override:
      system: true
```

## Debug Events

Call `boukensha.debug()` before `agent.run()` to include raw provider responses in the log:

```python
import boukensha
boukensha.debug()
```

## Run Example

```bash
./week1_baseline/bin/python/06_the_logger
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 06_the_logger/examples/example.py
```

**This makes real, billed API calls** (typically 1-2, same as step 05's example, since this step doesn't change what the agent does — only how it's observed).

## Notes on the Ruby README

`ruby/06_the_logger/README.md`'s "Logger API" method table lists
`iteration(n:)`, `prompt(messages:, tools:, budget:)`, `tool_result(name:,
result:)`, and `response(text:, usage:, task:, backend:)` — but the real
code (`logger.rb`) defines `iteration(n:, max:)` (no denominator-less form),
`prompt(messages:, tools:)` (**no** `budget:` param anywhere in `logger.rb`),
`tool_result(name:, result:, ok: true, error: nil)` (two extra params), and
`response(text:, usage: nil, stop_reason: nil, task: nil, backend: nil)`
(has `stop_reason:` too). This table above matches the actual code, not the
Ruby README's.
