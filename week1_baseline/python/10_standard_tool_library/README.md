# 10 · A Standard Tool Library — MCP Host (Python port)

Python port of `ruby/10_standard_tool_library`. Boukensha now ships **no
built-in tools of its own** — every tool an agent can call comes from an MCP
(Model Context Protocol) server declared in `settings.yaml`'s `mcp_servers:`
block. An agent with an empty `mcp_servers:` block can only talk.

## Why

Porting Boukensha to another language hits a wall the moment a tool needs
`MudManager::Session` — a long-lived, threaded, telnet-protocol-aware
connection that's expensive to re-derive correctly per language. MCP already
standardizes "long-running server exposes discoverable typed tools over
stdio" with client libraries in every major language, so instead of one more
re-implementation of `Session`, there is one: `mud-manager --mcp` (in the
`mud_manager` gem), reachable from Python through a small, generic MCP
client. See `ruby/10_standard_tool_library/README.md` for the full option
analysis (`docs/plans/mud_manager/generic_interfacing.md`).

Note: unlike the Ruby side, the Python port never shipped built-in
`FileSystem`/`Shell`/`Mud` tools in an earlier step to begin with — this step
is a pure addition of the MCP layer for Python, not a removal of anything
that previously existed here.

## What's new

- **`boukensha.mcp.Client`** (`mcp/client.py`) — a minimal MCP-over-stdio
  client: spawn a server, handshake, `tools/list`, `tools/call`.
  Server-agnostic; `command`/`args`/`env` is the standard stdio transport
  config. Built on `subprocess.Popen` plus a background thread that
  continuously drains the child's stderr (this is what prevents a
  stderr-flood deadlock — draining must be continuous, not on-demand).
- **`boukensha.tools.Mcp`** (`tools/mcp.py`) — registers a server's
  discovered tools into the registry, optionally scoping their names with a
  `prefix=` (client-side only — a collision between two servers' effective
  tool names raises rather than silently clobbering one).
- **`mcp_servers:` in `settings.yaml`** — adding a capability is a config
  edit, not a code change. Each entry takes `command`, `args`, `env`,
  `prefix`, and `required: false` (downgrade a failed start to a warning
  instead of an error).
- MUD gameplay comes from the `mud-manager --mcp` daemon (the `mud_manager`
  gem), run as a separate process, not imported directly.
- `working_dir` survives on `boukensha.run()`/`.repl()` but is now `Context`
  metadata only (`Context.working_dir`) — it registers nothing. Plug in a
  filesystem-capable MCP server via `mcp_servers=` if an agent needs file
  access.

## `settings.yaml`

```yaml
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
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

## New/changed files

| File | Change |
|---|---|
| `mcp/__init__.py`, `mcp/client.py` | **new** — `Client` |
| `tools/__init__.py`, `tools/mcp.py` | **new** — `Mcp.register(...)` |
| `config.py` | `mcp_servers` property added; `mud_host`/`mud_port`/`mud_username`/`mud_password` removed; `_resolve_dir()` reverted to two tiers (env var, then `~/.boukensha` — the `./.boukensha`-in-cwd tier from step 08 is gone) |
| `context.py` | `working_dir` keyword + attribute added |
| `registry.py` | `registered(name)` added |
| `client.py` | the friendlier `401` message added in step 08 is removed — back to one generic failure message |
| `repl.py` | `mcp_server_names` keyword added; banner gains an `mcp servers:` line |
| `version.py` | `0.8.0` → `0.10.0` |
| `__init__.py` | `run()`/`repl()` gain `working_dir=`/`mcp_servers=` keywords and a private `_start_mcp_servers()` helper |

**Why the step 08 features got reverted:** Ruby's own source reverted them
first (in an intermediate `09_global_executable` step that was never ported
to Python on its own — its only other additions were Ruby-gem packaging with
no Python equivalent) and kept them reverted through step 10. This Python
port mirrors that, matching the project's standing rule to track Ruby's
actual source rather than "fix back" something Ruby itself moved away from.

## Verification scope for this port

This plan's verification was deliberately scoped to **fixture tests only** —
no live MUD connection, no live LLM call, zero cost. That's a smaller scope
than the Ruby side's own "Technical observations," which describe a real,
live end-to-end run against a local CircleMUD Docker container. This Python
port has **not** been run against that live infrastructure or a real
Anthropic call — `example.py` is ported and ready to run (given a properly
configured `.boukensha/settings.yaml` with a working `mcp_servers.mud` entry
and a real `ANTHROPIC_API_KEY`), but doing so was out of scope here by
explicit choice, not an oversight.

## Running the protocol-level tests (what this port actually verified)

No live MUD or LLM API key required — these exercise `boukensha.mcp.Client`
and `boukensha.tools.Mcp` against a small standalone fixture server
(`examples/fixtures/echo_mcp_server.py`):

```bash
cd week1_baseline/python
.venv/Scripts/python.exe 10_standard_tool_library/examples/mcp_client_test.py -v
.venv/Scripts/python.exe 10_standard_tool_library/examples/mcp_wiring_test.py -v
```

Both are ports of Ruby's own `minitest`-based `examples/mcp_client_test.rb`
and `examples/mcp_wiring_test.rb`, using Python's stdlib `unittest` — the
first step where Ruby's own source ships committed tests, so the project's
usual "no test framework" rule doesn't apply here.

## Running the demo (not exercised by this plan)

```bash
bash bin/python/10_standard_tool_library
```

Requires a `.boukensha/settings.yaml` with a working `mcp_servers.mud` entry
pointing at a real `mud-manager --mcp` process, and a real
`ANTHROPIC_API_KEY` — this makes a real, billed API call and a real
subprocess connection to a MUD server.
