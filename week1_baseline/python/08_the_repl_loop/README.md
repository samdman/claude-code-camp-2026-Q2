# 08 · The REPL Loop (Python port)

Python port of `ruby/08_the_repl_loop`. Adds `boukensha.repl()` and `Repl` —
an interactive session loop that registers tools once, then reads tasks
from stdin in a loop, running the agent and printing replies until the
user types `/exit`/`/quit` or sends EOF. Conversation history accumulates
across turns. See `ruby/08_the_repl_loop/README.md` for the original design
rationale. This file documents the Python-specific parts and corrects the
drift in the Ruby README (see "Notes on the Ruby README" at the bottom).

## What this step adds

| | Step 7 | Step 8 |
|---|---|---|
| Entry point | `boukensha.run(task="...")` | `boukensha.repl()` |
| Turns | one | many |
| History | discarded | accumulates across turns |
| User interaction | none | stdin prompt |

## New primitives

### `boukensha.Repl`

The interactive session loop. Built-in commands:

| Command | Effect |
|---|---|
| `/quiet` | Suppress logging output |
| `/loud` | Re-enable logging output |
| `/clear` | Wipe conversation history (tools stay registered) |
| `/help` | Print the command list |
| `/exit` / `/quit` | Leave the REPL |
| Ctrl-D (EOF) | Leave the REPL silently — no `Goodbye.` |
| Ctrl-C | Interrupt — leave the REPL gracefully |

### `boukensha.repl()`

Same keyword arguments as `boukensha.run()`, minus `task`. Register tools
via `block=`; then the REPL loop takes over.

```python
import boukensha


def register_tools(dsl):
    dsl.tool(
        "read_file",
        description="Read a file from disk",
        parameters={"path": {"type": "string", "description": "File path"}},
        block=lambda path: open(path).read(),
    )


boukensha.repl(model="claude-haiku-4-5", block=register_tools)
```

Note the same shape difference from Ruby noted in step 07's README: Python's
`block=` callback takes the `RunDSL` instance as an explicit argument
(`register_tools(dsl)` calling `dsl.tool(...)`) rather than relying on
Ruby's `instance_eval`-based implicit receiver.

## Changes from step 07

### `Context.clear_messages()`
Wipes `self.messages` while keeping tools registered. Used by the REPL
`/clear` command.

### `Agent.run()` — persists the final reply
Before step 08, the agent returned the final text without adding it to the
context. That was fine for one-shot runs (context is thrown away anyway),
but a REPL needs the full transcript so subsequent turns see the prior
exchange.

```python
# step 07 — final text returned but NOT added to context
return text

# step 08 — final text added to context, then returned
self.context.add_message("assistant", text)
return text
```

All three places `Agent` returns a final answer (normal completion, and
both branches of the iteration-limit wind-down) do this now.

### `Client` — friendlier 401 errors
A `401` response now raises `ApiError("authentication failed (401) — check
your API key")` instead of the generic failure message.

### `Config` — a third directory-resolution tier
`Config.dir` now resolves in this order: the `BOUKENSHA_DIR` environment
variable, then `./.boukensha` relative to the current working directory (if
it exists), then `~/.boukensha`. `example.py` always sets `BOUKENSHA_DIR`
explicitly, so the middle tier isn't exercised by the example — it's real,
working `Config` API surface all the same.

### `Logger.turn(n)`
Writes a `"turn"` phase event to the JSONL session log at the start of each
REPL turn — a log-only event, **not** a stdout print (see "Notes on the
Ruby README" below).

## Running it

```bash
bash bin/python/08_the_repl_loop
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 08_the_repl_loop/examples/example.py
```

```
Config: #<Boukensha::Config dir=C:/.../.boukensha tasks=player>

╔══════════════════════════════════════╗
║  BOUKENSHA MUD Assistant (v0.8.0)    ║
╚══════════════════════════════════════╝
  config:    C:/.../.boukensha
  provider:  anthropic (claude-haiku-4-5)  ✓ API key set

  /quiet or /loud   toggle logging
  /clear           reset conversation history
  /exit or /quit    leave the REPL

boukensha> list the files in this directory
...
boukensha> /quiet
(logging suppressed — type /loud to re-enable)
boukensha> /exit
Goodbye.
```

The built-in commands (`/help`, `/quiet`, `/loud`, `/clear`, `/exit`/`/quit`,
EOF) are entirely free — none of that path reaches the model. **Typing an
actual task at the prompt makes a real, billed API call**, same caveat as
every prior step's README from 04 onward, just phrased for an interactive
session instead of a single call.

## Notes on the Ruby README

`ruby/08_the_repl_loop/README.md` has four issues, all left uncorrected in
the Ruby source but not repeated here:

1. **Title says "Step 7"** — the folder is `08_the_repl_loop`. Same
   off-by-one pattern flagged for step 07's own README (which said "Step 6"
   for the `07_the_run_dsl` folder) — apparently systematic across at least
   the last two steps.
2. **"Running it" references a nonexistent path and filename**: `cd
   07_the_repl_loop` and `ruby examples/step7.rb`. Neither exists — the
   real directory is `08_the_repl_loop` and the real file is
   `examples/example.rb`. Looks like a copy-paste artifact from an even
   earlier draft numbering scheme.
3. **The illustrative banner doesn't match the real banner.** The README
   shows `║  BOUKENSHA REPL  —  MUD assistant   ║` / `║  type a command and
   press Enter     ║`; the real `repl.rb#banner` prints `║  BOUKENSHA MUD
   Assistant (v0.8.0)    ║` plus `config:`/`provider:` lines and the
   command hints — confirmed against a live run (see the transcript above).
4. **"`Logger#turn` — prints a `╔══ turn N ══╗` header" is false.**
   `logger.rb` is byte-identical to step 07's — `turn(n:)` only writes a
   `"turn"` phase event to the JSONL session log. It prints nothing to
   stdout. Confirmed via direct diff against step 07's `logger.rb` and by
   the live transcript above showing no such header.
