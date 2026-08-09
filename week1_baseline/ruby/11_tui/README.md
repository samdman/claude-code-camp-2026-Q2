# Step 11 — A Terminal UI + MCP Host

Boukensha ships two things on top of step 9's plain REPL loop:

1. **An MCP-host tool architecture** (carried over from `10_standard_tool_library`): Boukensha ships **no tools of its own**. Every tool the agent can call comes from an MCP server declared in `settings.yaml`'s `mcp_servers:` block. An agent with an empty `mcp_servers:` block can only talk.
2. **A full terminal UI (TUI)**, built on the [`charm`](https://github.com/charm-ruby/charm) gem (bubbletea + lipgloss + bubbles). The plain REPL is still there and can be selected with `tui: false` / `--no-tui`.

## Why MCP

Porting Boukensha to another language hits a wall the moment a tool needs
`MudManager::Session` — a long-lived, threaded, telnet-protocol-aware
connection that's expensive to re-derive correctly per language. MCP
(Model Context Protocol) already standardizes "long-running server exposes
discoverable typed tools over stdio" with client libraries in every major
language, so instead of four re-implementations of `Session`, there is one:
`mud-manager --mcp` (in the `mud_manager` gem), reachable from any language's
Boukensha port through a small, generic MCP client. See
`docs/plans/mud_manager/generic_interfacing.md` for the full option analysis.

## What's new versus step 9

### MCP host

- **`Boukensha::Mcp::Client`** (`lib/boukensha/mcp/client.rb`) — a minimal
  MCP-over-stdio client: spawn a server, handshake, `tools/list`,
  `tools/call`. Server-agnostic; `command` / `args` / `env` is the standard
  stdio transport config.
- **`Boukensha::Tools::Mcp`** (`lib/boukensha/tools/mcp.rb`) — the only file
  under `tools/`. Registers a server's discovered tools into the registry,
  optionally scoping their names with a `prefix:` (client-side only — a
  collision between two servers' effective tool names raises rather than
  silently clobbering one).
- **`mcp_servers:` in `settings.yaml`** — adding a capability is a config
  edit, not a code change. Each entry takes `command`, `args`, `env`,
  `prefix`, and `required: false` (downgrade a failed start to a warning
  instead of an error).
- MUD gameplay comes from the `mud-manager --mcp` daemon (the `mud_manager`
  gem, run as a separate process instead of `require`d directly).
- `working_dir:` survives on `Boukensha.run` / `.repl` but is Context
  metadata only — it registers nothing. Plug in a filesystem- or
  shell-capable MCP server via `mcp_servers:` if an agent needs one.

### `Boukensha::Tui`

Wraps a `Repl` instance and replaces its raw `puts`/`gets` I/O with a structured four-zone display:

```
┌──────────────────────────────────────────────┐
│  conversation viewport (scrollable)           │
├──────────────────────────────────────────────┤
│  ⟳ live progress line (hidden when idle)     │
├──────────────────────────────────────────────┤
│  boukensha> input box                         │
├──────────────────────────────────────────────┤
│  status line (always-on)                      │
└──────────────────────────────────────────────┘
```

The **progress line** shows a spinner, current action, iteration counter (`n/MAX`), elapsed seconds, token counts (↑ in / ↓ out), and tool call count while the agent is running. When idle it shows context usage and turn count.

The **status line** always shows: version · model · context tokens used/max · registered tool count · wall-clock time.

**Keyboard shortcuts:**

| Key | Action |
|-----|--------|
| `Enter` | Submit input or slash command |
| `Esc` | Interrupt the running agent turn |
| `Ctrl+L` | Clear conversation history |
| `PgUp` / `PgDn` | Scroll conversation viewport |
| `Ctrl+C` / `Ctrl+D` | Quit |

The agent runs in a background thread so the UI stays responsive during long turns.

`Tui` requires the `charm` gem (native bubbletea/lipgloss/bubbles bindings), which only ships prebuilt gems for some platforms — see `Gemfile.lock`'s `PLATFORMS` list. `lib/boukensha.rb` requires it defensively (`rescue LoadError`), so on a platform without a compatible `charm` build, `Boukensha.repl` automatically falls back to the plain REPL instead of failing to load at all.

### `Boukensha.repl` — new `tui:` keyword

```ruby
Boukensha.repl(tui: true)   # default — launches charm TUI
Boukensha.repl(tui: false)  # falls back to plain terminal REPL
```

The `--no-tui` CLI flag sets `tui: false` from the command line.

### `Repl` refactored for composability

`Repl` no longer hard-codes `puts`/`gets`. Three methods are public so `Tui` (or any other front-end) can drive it:

| Method | Purpose |
|--------|---------|
| `on_output(&block)` | Route all REPL output through a callback instead of stdout |
| `handle_command(input)` | Process a slash command; returns `:quit`, `:command`, or `nil` |
| `run_turn(input)` | Run one agent turn and route the result through `on_output` |

`banner`, `logger`, `context`, `model`, and `version` are also exposed as readers. The banner's `mcp servers:` line lists every currently-connected server's name (matching `10_standard_tool_library`'s banner), instead of directly probing a MUD connection.

### `Logger#subscribe`

```ruby
logger.subscribe { |event| ... }
```

Every structured log event (`:iteration`, `:tool_call`, `:tool_result`, `:response`, etc.) is broadcast to all registered subscribers as well as being written to the JSONL file. `Tui` uses this to update the live progress line in real time without polling.

## `settings.yaml`

```yaml
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
    prompt_override:
      system: true
mcp_servers:
  mud:
    command: ruby
    args:
      - /absolute/path/to/week0_explore/mud_manager/bin/mud-manager
      - --mcp
    env:
      MUD_HOST: localhost
      MUD_PORT: "4000"
      MUD_USERNAME: dummy
      MUD_PASSWORD: helloworld
```

`command`/`args` above point straight at the checked-out `mud_manager` gem's
`bin/mud-manager` script (no `gem install` required — it self-loads its own
`lib/` via `$LOAD_PATH.unshift`). Once `mud_manager` is published/installed
as a gem, this can shrink to `command: mud-manager`, `args: [--mcp]`, relying
on the `mud-manager` executable being on `PATH`.

## Run

The TUI is interactive, so it's run via the global `boukensha` executable
rather than `examples/example.rb` (that file is the MUD demo carried over
from step 10 — it doesn't exercise the TUI):

```sh
# Build and install this step's gem. If a later step's gem is already
# installed, `boukensha` will keep launching that version's loader instead —
# remove it first:
gem uninstall boukensha

gem build boukensha.gemspec
gem install boukensha-0.11.0.gem

# launches the charm TUI (requires a platform with a compatible charm build):
BOUKENSHA_DIR=~/.boukensha BOUKENSHA_PATH=~/Sites/boukensha/11_tui boukensha

# plain REPL (no charm dependency required):
BOUKENSHA_PATH=~/Sites/boukensha/11_tui boukensha --no-tui
```

Non-interactive MUD demo (same shape as step 10):

```sh
ruby examples/example.rb
```

Protocol-level tests (no live MUD or LLM API key required):

```sh
ruby examples/mcp_client_test.rb
ruby examples/mcp_wiring_test.rb
```

(mud_manager's own protocol tests live alongside it: `ruby
../../../week0_explore/mud_manager/examples/mcp_server_test.rb` and
`mcp_tools_test.rb`.)

## Technical observations

- This step's `Gemfile.lock` was generated on Linux (`PLATFORMS: ruby,
  x86_64-linux`) — `charm`/`bubbletea`/`bubbles`/`ntcharts`/`glamour`/`lipgloss`
  ship native builds for that platform only. On a platform without a matching
  build (e.g. native Windows Ruby), `require "boukensha/tui"` raises
  `LoadError`; `lib/boukensha.rb` catches that and `Boukensha.repl` falls back
  to the plain REPL (`tui: false` behavior) automatically — verified during
  this step's MCP port by running `examples/mcp_client_test.rb` and
  `examples/mcp_wiring_test.rb` end-to-end on Windows with `charm` absent.
- Carried over from step 10 (not reinvestigated as part of this port):
  `Config::PROMPTS_DIR` (`lib/boukensha/config.rb`) resolves via
  `File.expand_path("../../../prompts", __dir__)`, one `..` too many relative
  to this step's actual `prompts/` directory. If `Boukensha.run`/`.repl`'s
  full LLM loop is exercised without a `system:` override, this can surface
  as `system: nil` being sent to a backend that requires a string. This is a
  pre-existing, one-line bug unrelated to the MCP/TUI work in this step —
  worth fixing, but out of scope here.
