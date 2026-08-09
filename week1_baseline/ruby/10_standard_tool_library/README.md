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
- 2026-08-09: end-to-end MCP integration test against a live CircleMUD (local Docker container, `week0_explore/infrastructure`). `Boukensha::Mcp::Client` spawned `ruby bin/mud-manager --mcp` as a real child process, completed the MCP handshake, and `tools/list` returned all 27 gameplay tools. Called `look` and `check {kind: "score"}` directly through the client — both round-tripped through the subprocess to `MudManager::Session` and back with real CircleMUD output (room description, character sheet), confirming the full hop (Boukensha → `Mcp::Client` → `mud-manager --mcp` subprocess → `Session` → live MUD) works as designed. The MUD server's own log corroborated each session (`Dummy has reconnected` / `Closing link to: Dummy` around the same timestamps as each test run — two more runs to double-check subprocess liveness landed at 02:12:46-51 and 02:14:58-15:08 UTC, matching each test's start/stop exactly).
  - Evidentiary note: the two checks this plan's brief specifically asked for — a `tool_call`/`tool_result` JSONL pair, and a `ps`/Task Manager listing showing the child process — weren't captured in their literal form, and that's worth being explicit about rather than letting the bullet above imply otherwise. `tool_call`/`tool_result` logging lives in `Boukensha::Agent` (`lib/boukensha/agent.rb`), which is only reached by `Boukensha.run`'s full LLM loop; since that loop fails before its first tool call (see below), calling `Mcp::Client` directly instead never produces those log entries — confirmed by inspecting the session's own JSONL log, which stops at `phase: "prompt"`. Similarly, a live `tasklist`/`ps` snapshot mid-run kept missing the subprocess (background-process timing made it hard to sample synchronously through the tooling used here) — the substitute evidence is `Open3`'s own `wait_thr.pid` (a real, distinct Windows PID captured at spawn time on every run) plus the MUD server's own connection/disconnection log timestamps lining up exactly with each run's start/stop. That combination proves a live, separate OS process handled each call — arguably stronger than a bare process-list entry, since it also proves the process was doing the right thing (talking to the MUD) — but it is a different evidentiary method than the brief specified, not the same one.
- Two environment gotchas hit along the way, both config/environment issues rather than plan bugs: (1) a leftover `BOUKENSHA_DIR` env var from earlier baseline work pointed at the outer repo's `.boukensha/` and silently overrode `examples/example.rb`'s own default (`week1_baseline/.boukensha`, resolved 3 levels up from `examples/`) — worth remembering that env var wins over the script's `||=` default; (2) `mcp_servers.*.args` paths must be Windows-style (`C:/...`) when the MCP client's `command:` is spawned by native Windows Ruby — a Git-Bash-style `/c/...` path fails with a `LoadError` from the child process, not a clean "file not found" from the parent.
- Running the full agent loop via `Boukensha.run`/`examples/example.rb` (as opposed to calling `Mcp::Client` directly) currently fails before making any tool call — the Anthropic API rejects the request with `400: system: Input should be a valid array`, coming from `lib/boukensha/backends/anthropic.rb#to_payload` sending `system:` as a plain string. This is a pre-existing bug in the Anthropic backend (unrelated to MCP — the file predates this plan and no task touches it), not something this plan introduced or is in scope to fix. The MCP layer itself is proven working independent of this (see above); the backend bug blocks the full LLM-driven demo from completing and should be tracked separately.
