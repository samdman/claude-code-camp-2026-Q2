# 11 · A Terminal UI (Python port)

Python port of `ruby/11_tui`. **This step's headline feature — a full
terminal UI built on the `charm` gem (native Go bindings for
bubbletea/lipgloss/bubbles) — is not ported.** There is no Python
equivalent of `charm` in this project's dependency scope (stdlib +
`PyYAML` + `python-dotenv` only), and hand-rolling one would be a
from-scratch reimplementation with a different rendering engine, not a
behavioral port. `boukensha.repl(tui=True)` and `boukensha.repl(tui=False)`
are therefore identical in this port — both always run the plain REPL.

What *is* real and ported: `Repl` was refactored in Ruby to be
front-end-agnostic (the same seam its own `Tui` class hooks into), and that
refactor is fully portable on its own.

## What this step adds

### `Repl.on_output(callback)`

Registers a callback that receives every string `Repl` would otherwise
print to stdout. When set, the `boukensha> ` prompt is also suppressed (a
`Tui`-style front end draws its own input box instead).

```python
captured = []
repl.on_output(lambda s: captured.append(s))
```

### `Repl.handle_command(input_line)`

Extracted from `start()`'s inline dispatch. Returns `"quit"`, `"command"`,
or `None` (not a recognized command — the caller should fall through to
`run_turn`).

### `Repl.banner()` / `Repl.run_turn(input_line)` — now public

Both were private (`_banner`/`_run_turn`) through step 10. A composable
front end needs to call `banner()` once at startup and `run_turn(...)` per
submitted line — same reasoning as `handle_command` above.

### `Repl.logger` / `.context` / `.model` / `.version` — new read-only properties

Expose state a front end needs (e.g. to render its own status line) without
opening the underlying `_logger`/`_context`/`_model`/`_version` attributes
to mutation.

### `boukensha.repl()` — new `tui` keyword (inert in this port)

```python
boukensha.repl(tui=True)   # default — same as tui=False in this port
boukensha.repl(tui=False)  # plain REPL
```

### Built-in commands — `/quiet`/`/loud` removed

| Command | Effect |
|---|---|
| `/clear` | Wipe conversation history (tools stay registered) |
| `/help` | Print the command list |
| `/exit` / `/quit` | Leave the REPL |
| Ctrl-D (EOF) | Leave the REPL silently — no `Goodbye.` |
| Ctrl-C | Interrupt — leave the REPL gracefully |

**`/quiet` and `/loud` are gone as of this step — not just from the
command table, but from `Repl` entirely.** Typing either at the prompt now
sends it to the agent as a literal task (a real, billed API call), the
same as any other unrecognized non-slash input always has. This matches
`ruby/11_tui`'s actual behavior exactly (confirmed by direct experiment,
see the plan's "Behavior to Preserve Exactly").

## Running it

```bash
bash bin/python/11_tui
```

or directly:

```bash
cd week1_baseline/python
.venv/Scripts/python 11_tui/examples/example.py
```

This runs the same MUD demo as `10_standard_tool_library/examples/example.py`
(unchanged this step — `ruby/11_tui/examples/example.rb` only got a
stale-comment fix, not a behavior change). To exercise the actual
`Repl`/`on_output`/`handle_command` changes this step introduces, call
`boukensha.repl()` directly:

```bash
printf '/help\n/clear\n/exit\n' | .venv/Scripts/python -c "import boukensha; boukensha.repl()"
```

## Notes on the Ruby README

`ruby/11_tui/README.md` documents both the (carried-over) MCP host and the
(new) `Tui` class in detail, including a keyboard-shortcut table, a
four-zone layout diagram, and a `Logger.subscribe()`-driven live progress
line. None of that applies here — see the scope note at the top of this
file and the Python port plan's Goal section for why.
