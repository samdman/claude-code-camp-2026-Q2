# Step 10 — A Standard Tool Library — MCP Host

This step originally shipped three built-in tool modules (`Tools::FileSystem`,
`Tools::Shell`, `Tools::Mud`). That code has been deleted and replaced by an
MCP-host rewrite: Boukensha now ships **no tools of its own**. Every tool the
agent can call comes from an MCP server declared in `settings.yaml`. An agent
with an empty `mcp_servers:` block can only talk.

## Why

Porting Boukensha to another language hits a wall the moment a tool needs
`MudManager::Session` — a long-lived, threaded, telnet-protocol-aware
connection that's expensive to re-derive correctly per language. MCP
(Model Context Protocol) already standardizes "long-running server exposes
discoverable typed tools over stdio" with client libraries in every major
language, so instead of four re-implementations of `Session`, there is one:
`mud-manager --mcp` (in the `mud_manager` gem), reachable from any language's
Boukensha port through a small, generic MCP client. See
`docs/plans/mud_manager/generic_interfacing.md` for the full option analysis.

## What's new

- **`Boukensha::Mcp::Client`** (`lib/boukensha/mcp/client.rb`) — a minimal
  MCP-over-stdio client: spawn a server, handshake, `tools/list`,
  `tools/call`. Server-agnostic; `command` / `args` / `env` is the standard
  stdio transport config.
- **`Boukensha::Tools::Mcp`** (`lib/boukensha/tools/mcp.rb`) — the only file
  left under `tools/`. Registers a server's discovered tools into the
  registry, optionally scoping their names with a `prefix:` (client-side
  only — a collision between two servers' effective tool names raises
  rather than silently clobbering one).
- **`mcp_servers:` in `settings.yaml`** — adding a capability is a config
  edit, not a code change. Each entry takes `command`, `args`, `env`,
  `prefix`, and `required: false` (downgrade a failed start to a warning
  instead of an error).
- MUD gameplay comes from the `mud-manager --mcp` daemon (the `mud_manager`
  gem, now run as a separate process instead of `require`d directly).
- `working_dir:` survives on `Boukensha.run` / `.repl` but is now Context
  metadata only — it registers nothing. `allowed_commands:` and
  `shell_timeout:` are gone along with the built-in shell tool; plug in a
  shell-capable MCP server via `mcp_servers:` if an agent needs one.

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

## Run the demo

```sh
ruby examples/example.rb

# or via the global executable pointed at this step:
BOUKENSHA_PATH=~/Sites/boukensha/10_standard_tool_library boukensha
```

Protocol-level tests (no live MUD or LLM API key required):

```sh
ruby examples/mcp_client_test.rb
```

(mud_manager's own protocol tests live alongside it: `ruby
../../../week0_explore/mud_manager/examples/mcp_server_test.rb` and
`mcp_tools_test.rb`.)

## Technical observations

- at this point seems i still haven't installed mud manager, so i had to do that
- gem build on 09 is different version (0.9) than the one we have in 10, i had to rebuild and install what gemspec we have in 10
