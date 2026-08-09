Our MudManager is written in Ruby
In our bootcamp, bootcampers want to use their own language e.g. java, python, rust, go

what is the solution?
- we have to create a wrapper per lang
- we make mudmanager a CLI and other lang will execute the shell command in their lang
- we implement a communication protocol
- we implement MCP as a layer

consider that the mudmanager is managing the sessions for the mud

## Technical exploration

### What `MudManager` actually has to expose

Read `week0_explore/mud_manager/lib/mud_manager/{session,primitives}.rb` to ground this in
the real API surface rather than the abstract idea of "a Ruby library":

- **`MudManager::Primitives`** is pure and stateless — a module of functions that take
  enum-checked arguments (e.g. `direction`, `position`) and return a `Command` struct wrapping
  a raw string (`"north"`, `"kill goblin"`). No I/O, no state. Trivial to reimplement in any
  language if it ever needed to be — but that's not where the hard problem is.
- **`MudManager::Session`** is the hard part: a **long-lived TCP telnet connection** with a
  background reader thread that continuously drains the socket, strips telnet IAC negotiation
  bytes, and buffers output behind a mutex/condition-variable. It runs a multi-step CircleMUD
  login handshake (`login`) and exposes blocking reads keyed on quiet-time or a prompt sentinel
  (`read_until_quiet`, `read_until_prompt`). Critically, **one session is opened once and reused
  across many subsequent commands** — `Boukensha::Tools::Mud.register` opens/logs in a single
  `Session` and shares it by closure across ~20 tools (`look`, `move`, `attack`, `say`, ...).

So the real constraint isn't "Ruby code needs to run from another language" in the abstract —
it's "a stateful, threaded, protocol-aware TCP session needs to be reachable from another
language, and the session must persist across many discrete tool calls." That constraint rules
out or reshapes some of the four options below.

### Option 1 — Wrapper per language (reimplement in Java/Python/Rust/Go)

Reimplementing `Primitives` per language is cheap (pure functions, small surface). Reimplementing
`Session` per language is not: telnet IAC stripping, the CircleMUD login state machine, and the
quiet-time/prompt-sentinel buffering logic are all subtle and only validated against the live
Ruby implementation. Four reimplementations means four places to independently get IAC parsing,
prompt detection, and login-menu handling right — and four places for those to silently drift out
of sync as CircleMUD quirks are discovered. Highest total maintenance cost, worst fit for a
bootcamp where the point is teaching agent/tool concepts, not re-deriving a telnet client per
language track.

### Option 2 — MudManager as a CLI, other languages shell out

Doesn't fit the session model. A CLI invocation is naturally one-shot (spawn, run, exit), but
`Session` needs to stay open and logged in across dozens of calls within one agent turn.
Shelling out per command means either:
- reconnecting and re-logging-in on every single tool call (multi-second login handshake paid
  per `look`/`move`/`attack` — both slow and likely to desync the game state), or
- turning the "CLI" into a background daemon the shell commands talk to — at which point it isn't
  really a CLI anymore, it's option 3 (a custom protocol) with extra steps.

Only viable if paired with a persistent daemon process, which just relocates this option into
"communication protocol," below.

### Option 3 — Implement a bespoke communication protocol

This is the correct *shape* (a long-running Ruby process owns the `Session`; other languages
speak to it over a socket/stdio), but building it from scratch means reinventing: message framing,
a request/response or request/notification model, tool/command discovery, typed argument
validation, and error propagation — all per client language. That's a full protocol design and
four client implementations of it, for a problem that already has a standard solution (next
option).

### Option 4 — MCP as a layer

MCP (Model Context Protocol) already standardizes exactly this shape: a long-running server
process that exposes discoverable, typed tools (`tools/list`, `tools/call`) over a stdio or
socket transport, with client libraries already available for Python, Java, Go, Rust, and
JS/TS. Adopting it means:
- The `Session` (and all its threading/IAC/login complexity) stays **implemented exactly once**,
  in Ruby, inside the `mud_manager` gem's server.
- The server process is long-running by nature — the MCP session maps directly onto
  `MudManager::Session`'s lifetime (open once, serve many `tools/call` requests), so there's no
  reconnect-per-command problem like the CLI option has.
- Every other language only needs a generic MCP *client* (spawn subprocess, JSON-RPC over
  stdin/stdout) — a small, mechanical, well-documented piece of code, not a bespoke protocol
  implementation.

### This has already been tried, and it's what actually worked

`week1_baseline/ITERATIONS.md` (§"10 Standard Tool Library — MCP Host") documents that this exact
problem was hit for real during the Python port:

> "We are implementing a mapping of tools for the agent from the Mud Manager. However when we
> went to port the code to Python the python app had no way of accessing the MudManager ruby
> version so we end up implementing MCP... We end up adding the MCP server within Mud Manager so
> its a single gem."

The resolution that shipped (in the reference "omenking" repo, not yet in this baseline checkout)
was Option 4, applied consistently:
- `mud_manager` gained an `--mcp` mode (`mud-manager --mcp`), run as its own process, serving MUD
  gameplay tools over MCP stdio.
- Boukensha itself was generalized into an **MCP host with no built-in tools at all** —
  `Boukensha::Mcp::Client` (spawn/handshake/`tools/list`/`tools/call`) plus
  `Boukensha::Tools::Mcp` (registers a server's tools into the local tool registry, with an
  optional name-collision-safe `prefix:`). Filesystem, shell, and MUD access all became
  `mcp_servers:` entries in `settings.yaml` instead of hard-coded Ruby modules.
- This generalization was a net win beyond just solving the MUD problem: adding *any* new
  capability, in *any* language's Boukensha port, became a config edit instead of a code change.

The note in `ITERATIONS.md` is also explicit about cost: implementing MCP from scratch was a real
side quest ("a 2 hour video and its worth watching but not doing"), and the recommendation on
replaying this course is to copy the already-built `MudManager` + MCP-host `10_standard_tool_library`
from the reference repo rather than re-deriving it.

## Recommendation

**Adopt Option 4 (MCP), matching the precedent in `ITERATIONS.md` — don't re-explore this.**

1. Give `mud_manager` an `--mcp` server mode. One `MudManager::Session` per server process,
   exposing the same tool surface `Boukensha::Tools::Mud` already defines (`mud_connect`, `look`,
   `move`, `attack`, `say`, ... — see `ruby/10_standard_tool_library/lib/boukensha/tools/mud.rb`
   for the full list and parameter shapes) via `tools/list` / `tools/call`. Ship it inside the
   `mud_manager` gem itself (single artifact: `gem install mud_manager` gets both the library and
   the `mud-manager` MCP binary), not as a separate wrapper project.
   - Not yet present in this checkout: `week0_explore/mud_manager` currently has no `--mcp` mode —
     this is the concrete gap to close, ideally by porting it from the reference repo per the
     `ITERATIONS.md` note rather than building it fresh.
2. Turn each language's Boukensha port into a generic **MCP host** with no hard-coded MUD (or
   filesystem/shell) tools: a minimal MCP client (spawn `mud-manager --mcp`, JSON-RPC over stdio,
   `tools/list` at startup, `tools/call` per agent tool invocation) plus a `mcp_servers:` config
   block. This is the same shape already targeted for Ruby steps 10–12 in `ITERATIONS.md` and
   should be treated as one requirement, not a Ruby-specific one and a separate MUD-access
   problem.
3. Use stdio transport, not a socket/HTTP protocol. One MCP host process spawning one
   `mud-manager --mcp` subprocess maps 1:1 onto one `MudManager::Session`, which matches how the
   game already expects a single logged-in connection per character. Avoids the added complexity
   of a network transport (auth, multiple concurrent clients sharing one session, reconnect
   handling) that this bootcamp doesn't need.
4. Before committing per language, do a quick spike confirming an MCP client library exists and
   supports stdio transport for that language's course track (Python has an official `mcp`
   package; Java, Go, and Rust have community/official SDKs) — cheap to verify up front, expensive
   to discover missing mid-port.

**Why not the others:** per-language wrappers (Option 1) duplicate `Session`'s hardest, most
failure-prone logic (IAC stripping, login handshake, buffered reads) four times over with no
shared source of truth. CLI shell-out (Option 2) fundamentally conflicts with the
one-login-many-commands session model and either pays a login round-trip per tool call or
degrades into a daemon (i.e., Option 3 in disguise). A bespoke protocol (Option 3) solves the
right problem but reinvents transport, framing, and discovery that MCP already provides — for no
benefit, since the bootcamp already needs an MCP host generalization for other tools regardless of
MUD access.
