# 07 · The boukensha.run DSL (Python port)

Python port of `ruby/07_the_run_dsl`. Adds `boukensha.run()` — a single
top-level entry point that wires together `Context`, `Registry`, a backend,
`PromptBuilder`, `Client`, `Logger`, and `Agent` from just a `task=` string
and a block that registers tools. See `ruby/07_the_run_dsl/README.md` for
the original design rationale. This file documents the Python-specific
parts and corrects the drift in the Ruby README (see "Notes on the Ruby
README" at the bottom).

## What this step adds

Every previous step required manually creating and wiring together a
`Context`, `Registry`, a backend, `PromptBuilder`, `Client`, `Logger`, and
`Agent`. This step hides all of that behind one function call and a
registration callback.

## The new primitives

### `boukensha.RunDSL`

A tiny host object. Ruby's `Boukensha.run` does `instance_eval(&block)`
against a `RunDSL`, rebinding `self` inside the block so it can call `tool`
with no explicit receiver. Python has no equivalent for rebinding `self` —
instead, `boukensha.run(..., block=...)` calls `block(dsl)`, passing the
`RunDSL` instance as an explicit argument. `RunDSL` exposes exactly one
method: `.tool(name, *, description, parameters=None, block)`.

### `boukensha.run()`

Accepts keyword arguments that describe *what* to do. All plumbing is
handled internally.

| Option | Default | Description |
|---|---|---|
| `task` | *(required)* | The user message handed to the agent |
| `system` | task's configured system prompt | System prompt |
| `model` | task's configured model | Model name |
| `backend` | task's configured provider | One of `"anthropic"`, `"openai"`, `"gemini"`, `"ollama"`, `"ollama_cloud"` |
| `api_key` | matching `*_API_KEY` env var (loaded from `.boukensha/.env`) | API key for the chosen backend; not needed for `"ollama"` |
| `ollama_host` | `"http://localhost:11434"` | Ollama base URL |
| `log` | `None` | Optional JSONL path override; by default logs go to `.boukensha/sessions/<session-id>.jsonl` |
| `max_output_tokens` | task's configured max output tokens | Per-reply output cap |
| `block` | `None` | Callable invoked with a `RunDSL` instance, for registering tools |

## Before and after

**Step 6 — manual plumbing:**

```python
from boukensha import Context, Registry, PromptBuilder, Client, Logger, Agent
from boukensha.backends import Anthropic
import os

ctx = Context(task=Player, system="You are a MUD player assistant.")
registry = Registry(ctx)
backend = Anthropic(api_key=os.environ["ANTHROPIC_API_KEY"], model="claude-haiku-4-5")
builder = PromptBuilder(ctx, backend)
client = Client(builder)
logger = Logger()
agent = Agent(context=ctx, registry=registry, builder=builder, client=client, logger=logger)

registry.tool(
    "read_file",
    description="Read a file",
    parameters={"path": {"type": "string"}},
    block=lambda path: open(path).read(),
)

ctx.add_message("user", "Read src/boukensha/__init__.py")
agent.run()
```

**Step 7 — just describe what you want:**

```python
import boukensha


def register_tools(dsl):
    dsl.tool(
        "read_file",
        description="Read a file",
        parameters={"path": {"type": "string", "description": "File path"}},
        block=lambda path: open(path).read(),
    )


boukensha.run(task="Read src/boukensha/__init__.py", block=register_tools)
```

Note the one real shape difference from Ruby's version: since Python's
`block=` callback takes the `RunDSL` instance as an explicit argument
(`register_tools(dsl)`) rather than relying on `instance_eval`'s implicit
receiver, tool registration reads as `dsl.tool(...)` instead of Ruby's bare
`tool ...`.

## New/Changed in This Step

| File | Change |
|---|---|
| `boukensha/run_dsl.py` | **New** — `RunDSL`, a one-method host object for tool registration |
| `boukensha/__init__.py` | Adds `run()`, the top-level DSL entry point; exports `RunDSL` and `LoopError` |
| `boukensha/logger.py` | Adds `Logger.turn(n)` and `Logger.subscribe(block)` (both unused by this step's example — real, working methods, not stubs) |
| `boukensha/config.py` | Restores `mud_host`/`mud_port`/`mud_username`/`mud_password` — this is Ruby source drift, not new functionality. These properties existed through step 05, were removed in step 06 (Ruby dropped them as dead code), and are simply back in step 07's Ruby snapshot, still unused by any code path |
| `boukensha/errors.py` | Restores `LoopError` — same Ruby source drift as above (removed in step 06, back in step 07, still unused) |

## Task Configuration

Unchanged in meaning from step 06:

```yaml
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
    prompt_override:
      system: true
```

## Run Example

```bash
./week1_baseline/bin/python/07_the_run_dsl
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 07_the_run_dsl/examples/example.py
```

**This makes real, billed API calls** (typically 1-2, same as prior steps'
examples, since this step doesn't change what the agent does — only how
it's invoked). The example registers two tools (`read_file`,
`list_directory`) and asks the agent to read this `README.md` and
summarize the framework. All iteration/tool-call/tool-result tracing goes
to the JSONL session log under `.boukensha/sessions/`, not stdout (unchanged
since step 06).

## Notes on the Ruby README

`ruby/07_the_run_dsl/README.md` has three issues, all left uncorrected in
the Ruby source but not repeated here:

1. **Title says "Step 6"** — it should say "Step 7". Leftover from
   copy-pasting step 06's README as a starting point (the before/after
   example section repeats the same mistake, calling step 6's manual-wiring
   code "Step 5").
2. **Options table lists `token_budget:` (default `8192`) and `max_tokens:`
   (default `1024`)** — neither parameter exists in the real `boukensha.rb`
   method signature. The real parameter is `max_output_tokens:`, resolved
   from task config when omitted, with no fixed default shown in the
   signature.
3. **Backend table only lists `:anthropic` and `:ollama`** — the real
   `case backend` dispatch in `boukensha.rb` handles all five backends this
   framework has supported since step 03/04: `:anthropic`, `:openai`,
   `:gemini`, `:ollama`, `:ollama_cloud`. The docstring comment directly
   above `self.run` in the same file correctly lists all five — only the
   markdown table below it is stale.
