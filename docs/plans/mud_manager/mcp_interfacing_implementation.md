# MudManager MCP Interfacing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `mud_manager` an `--mcp` server mode that exposes MUD gameplay as MCP tools over stdio, and turn `boukensha` (Ruby step `10_standard_tool_library`) into a generic MCP host with no built-in tools, so any language's Boukensha port can reach the MUD by speaking MCP instead of re-implementing `MudManager::Session`.

**Architecture:** `MudManager::Session` (telnet/IAC/login) stays exactly as-is and gets one new consumer: `MudManager::McpServer`, a JSON-RPC-over-stdio loop dispatching to a `MudManager::McpTools`-built tool table. `bin/mud-manager --mcp` opens/logs in the session once, then serves `tools/list`/`tools/call` for the lifetime of the process — mirroring the existing "open once, share across many tool calls" design. On the Boukensha side, `Boukensha::Tools::FileSystem`/`Shell`/`Mud` (all hard-coded) are deleted and replaced by `Boukensha::Mcp::Client` (spawn/handshake/list/call) + `Boukensha::Tools::Mcp` (registers a spawned server's tools into the registry, with optional `prefix:`). `mcp_servers:` in `settings.yaml` replaces the old `mud:` block — adding a capability becomes a config edit.

**Tech Stack:** Ruby (`>= 3.0`, matching both gemspecs), stdlib `json` + `open3` (no new gem dependencies), Minitest for the new protocol-level tests (already used by `mud_manager`'s existing `examples/live_session_test.rb`, ships as a Ruby default gem).

## Global Constraints

- Scope is exactly two projects: `week0_explore/mud_manager` (the gem) and `week1_baseline/ruby/10_standard_tool_library` (the Boukensha step used for testing/integration, per explicit instruction). `ruby/11_tui` and `ruby/12_context` (untracked, already present) are **not** touched by this plan.
- Transport is stdio only (no socket/HTTP), one client process per one spawned server process — matches `docs/plans/mud_manager/generic_interfacing.md`'s recommendation and the existing one-session-per-process model.
- Follow the existing wire precedent already documented in `week1_baseline/ITERATIONS.md` §"10 Standard Tool Library — MCP Host": `Boukensha::Mcp::Client`, `Boukensha::Tools::Mcp`, `mcp_servers:` in `settings.yaml` with `command`/`args`/`env`/`prefix`/`required`. Do not invent a different shape.
- No new runtime gem dependencies. Minitest is the only test tool, matching `mud_manager`'s existing convention — do not introduce rspec.
- Every JSON-RPC message is a single newline-delimited line (no embedded newlines), protocol version string `"2024-11-05"`.
- Don't add backwards-compatibility shims for the old `mud:`/`allowed_commands:`/`shell_timeout:` keyword arguments — this is a from-scratch rewrite of step 10's tool model, documented as such in `ITERATIONS.md`, not an incremental addition.

---

## Task 1: `MudManager::McpServer` — the JSON-RPC/MCP protocol core

**Files:**
- Create: `week0_explore/mud_manager/lib/mud_manager/mcp_server.rb`
- Modify: `week0_explore/mud_manager/lib/mud_manager.rb`
- Test: `week0_explore/mud_manager/examples/mcp_server_test.rb`

**Interfaces:**
- Produces: `MudManager::McpServer.new(tools:, name: "mud-manager", version: MudManager::VERSION, input: $stdin, output: $stdout)` with instance method `#serve`. `tools:` is `Hash[String, {description: String, input_schema: Hash, handler: ->(args_hash) { ... returns a String ... }}]`.

- [ ] **Step 1: Write the failing test**

Create `week0_explore/mud_manager/examples/mcp_server_test.rb`:

```ruby
# frozen_string_literal: true

require "minitest/autorun"
require "stringio"
require "json"

require_relative "../lib/mud_manager"

# Protocol-level tests for MudManager::McpServer, driven entirely over
# in-memory StringIO — no live MUD connection required.
#
# Run:
#   ruby mud_manager/examples/mcp_server_test.rb
class McpServerTest < Minitest::Test
  def build_server(input_lines, tools: {})
    input  = StringIO.new(input_lines.map { |h| JSON.generate(h) }.join("\n") + "\n")
    output = StringIO.new
    server = MudManager::McpServer.new(tools: tools, input: input, output: output)
    server.serve
    output.string.each_line.map { |l| JSON.parse(l) }
  end

  def test_initialize_returns_protocol_version_and_server_info
    responses = build_server([
      { jsonrpc: "2.0", id: 1, method: "initialize",
        params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "test", version: "0" } } }
    ])

    result = responses.first["result"]
    assert_equal "2024-11-05", result["protocolVersion"]
    assert_equal "mud-manager", result["serverInfo"]["name"]
  end

  def test_tools_list_returns_declared_tools
    tools = {
      "ping" => { description: "pong", input_schema: { type: "object", properties: {}, required: [] },
                  handler: ->(_args) { "pong" } }
    }

    responses = build_server([{ jsonrpc: "2.0", id: 1, method: "tools/list", params: {} }], tools: tools)

    listed = responses.first["result"]["tools"]
    assert_equal 1, listed.size
    assert_equal "ping", listed.first["name"]
    assert_equal "pong", listed.first["description"]
  end

  def test_tools_call_dispatches_to_handler
    tools = {
      "echo" => {
        description: "echoes the message argument",
        input_schema: { type: "object", properties: { "message" => { type: "string" } }, required: [] },
        handler: ->(args) { "you said: #{args['message']}" }
      }
    }

    responses = build_server([
      { jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "echo", arguments: { message: "hi" } } }
    ], tools: tools)

    result = responses.first["result"]
    refute result["isError"]
    assert_equal "you said: hi", result["content"].first["text"]
  end

  def test_tools_call_unknown_tool_is_an_error
    responses = build_server([{ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "nope", arguments: {} } }])

    result = responses.first["result"]
    assert result["isError"]
    assert_match(/unknown tool/, result["content"].first["text"])
  end

  def test_tools_call_handler_exception_is_reported_as_error_not_raised
    tools = {
      "boom" => { description: "raises", input_schema: { type: "object", properties: {}, required: [] },
                  handler: ->(_args) { raise "kaboom" } }
    }

    responses = build_server([{ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "boom", arguments: {} } }], tools: tools)

    result = responses.first["result"]
    assert result["isError"]
    assert_match(/kaboom/, result["content"].first["text"])
  end

  def test_unknown_method_returns_json_rpc_error
    responses = build_server([{ jsonrpc: "2.0", id: 1, method: "not/a/method", params: {} }])

    assert responses.first["error"]
    assert_equal(-32601, responses.first["error"]["code"])
  end
end
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ruby week0_explore/mud_manager/examples/mcp_server_test.rb`
Expected: `NameError: uninitialized constant MudManager::McpServer` (or `LoadError`) — the class doesn't exist yet.

- [ ] **Step 3: Write minimal implementation**

Create `week0_explore/mud_manager/lib/mud_manager/mcp_server.rb`:

```ruby
require "json"

module MudManager
  # A minimal MCP (Model Context Protocol) server over stdio: reads
  # newline-delimited JSON-RPC 2.0 requests from `input`, dispatches
  # `initialize` / `tools/list` / `tools/call`, and writes newline-delimited
  # JSON-RPC responses to `output`.
  #
  # `tools` is a plain Hash — this class knows nothing about MudManager's
  # gameplay surface. See MudManager::McpTools for the table this project
  # feeds it.
  class McpServer
    PROTOCOL_VERSION = "2024-11-05"

    def initialize(tools:, name: "mud-manager", version: MudManager::VERSION, input: $stdin, output: $stdout)
      @tools   = tools
      @name    = name
      @version = version
      @input   = input
      @output  = output
    end

    # Blocks until `input` reaches EOF (the client closed stdin, or — for a
    # real subprocess — the parent process exited).
    def serve
      @input.each_line do |line|
        line = line.strip
        next if line.empty?

        handle_line(line)
      end
    end

    private

    def handle_line(line)
      request = JSON.parse(line)
      dispatch(request)
    rescue JSON::ParserError => e
      respond_error(nil, -32700, "Parse error: #{e.message}")
    end

    def dispatch(request)
      id     = request["id"]
      method = request["method"]

      case method
      when "initialize"
        respond(id, initialize_result)
      when "notifications/initialized"
        nil # notification — no response
      when "tools/list"
        respond(id, tools_list_result)
      when "tools/call"
        respond(id, tools_call_result(request["params"] || {}))
      else
        respond_error(id, -32601, "Method not found: #{method}") if id
      end
    end

    def initialize_result
      {
        protocolVersion: PROTOCOL_VERSION,
        capabilities:    { tools: {} },
        serverInfo:      { name: @name, version: @version }
      }
    end

    def tools_list_result
      {
        tools: @tools.map do |tool_name, tool|
          { name: tool_name, description: tool[:description], inputSchema: tool[:input_schema] }
        end
      }
    end

    def tools_call_result(params)
      name      = params["name"]
      arguments = params["arguments"] || {}
      tool      = @tools[name]

      return error_content("unknown tool '#{name}'") unless tool

      begin
        text = tool[:handler].call(arguments)
        { content: [{ type: "text", text: text.to_s }], isError: false }
      rescue StandardError => e
        error_content("#{e.class}: #{e.message}")
      end
    end

    def error_content(message)
      { content: [{ type: "text", text: "error: #{message}" }], isError: true }
    end

    def respond(id, result)
      return if id.nil?

      write({ jsonrpc: "2.0", id: id, result: result })
    end

    def respond_error(id, code, message)
      write({ jsonrpc: "2.0", id: id, error: { code: code, message: message } })
    end

    def write(payload)
      @output.puts(JSON.generate(payload))
      @output.flush
    end
  end
end
```

Modify `week0_explore/mud_manager/lib/mud_manager.rb` to require it:

```ruby
module MudManager
end

require_relative "mud_manager/version"
require_relative "mud_manager/primitives"
require_relative "mud_manager/session"
require_relative "mud_manager/mcp_server"
```

(The `mud_manager/version` require is new — added in Task 2, which creates that file. Adding the require line now is harmless since Task 2 lands in the same plan before anything ships.)

- [ ] **Step 4: Run test to verify it passes**

Run: `ruby week0_explore/mud_manager/examples/mcp_server_test.rb`
Expected: `6 runs, ... 0 failures, 0 errors`

- [ ] **Step 5: Commit**

```bash
git add week0_explore/mud_manager/lib/mud_manager/mcp_server.rb week0_explore/mud_manager/lib/mud_manager.rb week0_explore/mud_manager/examples/mcp_server_test.rb
git commit -m "mud_manager: add MudManager::McpServer (JSON-RPC/MCP core over stdio)"
```

---

## Task 2: `MudManager::McpTools` + `bin/mud-manager --mcp` + version/gemspec

**Files:**
- Create: `week0_explore/mud_manager/lib/mud_manager/version.rb`
- Create: `week0_explore/mud_manager/lib/mud_manager/mcp_tools.rb`
- Create: `week0_explore/mud_manager/bin/mud-manager`
- Modify: `week0_explore/mud_manager/lib/mud_manager.rb`
- Modify: `week0_explore/mud_manager/mud_manager.gemspec`
- Modify: `week0_explore/mud_manager/README.md`
- Test: `week0_explore/mud_manager/examples/mcp_tools_test.rb`

**Interfaces:**
- Consumes: `MudManager::Session` (`week0_explore/mud_manager/lib/mud_manager/session.rb`) — `#open?`, `#open`, `#login(user, pass)`, `#close`, `#host`, `#port`, `#drain`, `#send_command(cmd)`, `#read_until_prompt`, `#read_until_quiet`. `MudManager::Primitives` (`week0_explore/mud_manager/lib/mud_manager/primitives.rb`) — all `module_function`s, unchanged. `MudManager::McpServer` from Task 1.
- Produces: `MudManager::McpTools.build(session, name:, password:)` → `Hash[String, {description:, input_schema:, handler:}]` in the exact shape `McpServer` expects. `bin/mud-manager --mcp`, an executable that reads `MUD_HOST`/`MUD_PORT`/`MUD_USERNAME`/`MUD_PASSWORD` from `ENV`, opens+logs in a `Session`, and serves it over MCP on stdio.

- [ ] **Step 1: Write the failing test**

Create `week0_explore/mud_manager/examples/mcp_tools_test.rb`:

```ruby
# frozen_string_literal: true

require "minitest/autorun"

require_relative "../lib/mud_manager"

# Verifies MudManager::McpTools wires Primitives + Session correctly, using a
# fake in-memory Session double instead of a live TCP connection.
#
# Run:
#   ruby mud_manager/examples/mcp_tools_test.rb
class FakeSession
  attr_reader :host, :port, :sent

  def initialize
    @host       = "localhost"
    @port       = 4000
    @open       = false
    @sent       = []
    @next_reply = "ok"
  end

  def open?  = @open
  def open   = @open = true
  def close  = @open = false
  def drain  = ""

  def login(_username, _password)
    @open = true
    "Welcome back."
  end

  def send_command(command)
    line = command.respond_to?(:raw) ? command.raw : command.to_s
    @sent << line
    line
  end

  def read_until_prompt(*) = @next_reply
  def read_until_quiet(*)  = @next_reply

  def next_reply=(text)
    @next_reply = text
  end
end

class McpToolsTest < Minitest::Test
  def setup
    @session = FakeSession.new
    @tools   = MudManager::McpTools.build(@session, name: "Gandalf", password: "secret")
  end

  def test_builds_every_tool_from_the_gameplay_surface
    expected = %w[
      mud_connect mud_disconnect mud_status look examine check move flee
      set_position track attack skill_strike consider say tell channel_say
      get_item drop_item put_item equip_item consume_item cast_spell
      use_magic_item shop practice save_character send_raw
    ]

    assert_equal expected.sort, @tools.keys.sort
  end

  def test_mud_connect_opens_and_logs_in
    result = @tools["mud_connect"][:handler].call({})

    assert_match(/connected to localhost:4000/, result)
    assert @session.open?
  end

  def test_gameplay_tool_guards_when_not_connected
    result = @tools["look"][:handler].call({})

    assert_match(/not connected/, result)
  end

  def test_look_sends_the_primitive_command_once_connected
    @session.open
    @session.next_reply = "You are in a room.\n> "

    result = @tools["look"][:handler].call({ "target" => "sword", "preposition" => "at" })

    assert_equal ["at sword"], @session.sent
    assert_equal "You are in a room.\n> ", result
  end

  def test_move_rejects_invalid_direction
    @session.open

    result = @tools["move"][:handler].call({ "direction" => "sideways" })

    assert_match(/invalid direction/, result)
  end

  def test_attack_defaults_style_to_kill
    @session.open
    @session.next_reply = "You attack!"

    @tools["attack"][:handler].call({ "target" => "goblin" })

    assert_equal ["kill goblin"], @session.sent
  end
end
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ruby week0_explore/mud_manager/examples/mcp_tools_test.rb`
Expected: `NameError: uninitialized constant MudManager::McpTools`

- [ ] **Step 3: Write minimal implementation**

Create `week0_explore/mud_manager/lib/mud_manager/version.rb`:

```ruby
module MudManager
  VERSION = "0.2.0"
end
```

Create `week0_explore/mud_manager/lib/mud_manager/mcp_tools.rb` (a mechanical port of the tool surface `Boukensha::Tools::Mud` used to register directly — same descriptions, same primitive calls, same defaults — adapted from Ruby keyword blocks to string-keyed Hash args because MCP arguments arrive as parsed JSON):

```ruby
require_relative "primitives"

module MudManager
  # Builds the MCP tool table for MUD gameplay: a Hash of tool name to
  # { description:, input_schema:, handler: } exposing the same gameplay
  # surface Boukensha::Tools::Mud used to register directly, now served over
  # MCP by MudManager::McpServer instead.
  #
  # `session` is opened and logged in once by the caller (bin/mud-manager)
  # before the server starts serving requests, and is shared by every tool
  # handler via closure — mirroring how Boukensha::Tools::Mud.register used
  # to share a single Session across ~20 tools.
  module McpTools
    module_function

    def build(session, name:, password:)
      p = Primitives

      send_cmd = lambda do |command|
        session.drain
        session.send_command(command)
        session.read_until_prompt
      end

      guard = lambda do
        "error: not connected — call mud_connect first" unless session.open?
      end

      str_param = ->(desc) { { type: "string", description: desc } }
      int_param = ->(desc) { { type: "integer", description: desc } }

      {
        "mud_connect" => {
          description: "Open the connection to the MUD server and log in with the configured " \
                       "character name and password. Safe to call when already connected " \
                       "(returns current status instead of reconnecting).",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            if session.open?
              "already connected to #{session.host}:#{session.port}"
            else
              begin
                session.open
                welcome = session.login(name, password)
                "connected to #{session.host}:#{session.port}\n#{welcome}"
              rescue MudManager::Session::Error => e
                "error: #{e.message}"
              end
            end
          end
        },

        "mud_disconnect" => {
          description: "Close the connection to the MUD server gracefully.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            if session.open?
              session.close
              "disconnected"
            else
              "already disconnected"
            end
          end
        },

        "mud_status" => {
          description: "Return whether the MUD session is currently connected.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            session.open? ? "connected to #{session.host}:#{session.port}" : "disconnected"
          end
        },

        "look" => {
          description: "Look at the current room or at a specific target. " \
                       "Call with NO arguments to describe the current room (do NOT pass target: 'room'). " \
                       "Pass a target to inspect a specific item, mob, or player (e.g. target: 'sword'). " \
                       "Use preposition 'in' to look inside a container, 'at' to inspect something, " \
                       "or a direction (north/east/south/west/up/down) to peek into an adjacent room.",
          input_schema: {
            type: "object",
            properties: {
              "target"      => str_param.call("Item, mob, or player name to inspect. Omit entirely to describe the current room."),
              "preposition" => str_param.call("Preposition: in, at, north, east, south, west, up, down (optional)")
            },
            required: []
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.look(target: args["target"], preposition: args["preposition"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "examine" => {
          description: "Examine a target in detail (more verbose than look).",
          input_schema: {
            type: "object",
            properties: { "target" => str_param.call("The item, mob, or player to examine") },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.examine(args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "check" => {
          description: "Query information about your character or surroundings. " \
                       "Kinds: score, inventory, equipment, gold, exits, time, weather, " \
                       "levels, wimpy, toggle, where.",
          input_schema: {
            type: "object",
            properties: { "kind" => str_param.call("What to check: score | inventory | equipment | gold | exits | time | weather | levels | wimpy | toggle | where") },
            required: ["kind"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.info_self(args["kind"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "move" => {
          description: "Move in a compass direction or up/down.",
          input_schema: {
            type: "object",
            properties: { "direction" => str_param.call("Direction: north | east | south | west | up | down") },
            required: ["direction"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.move(args["direction"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "flee" => {
          description: "Attempt to flee from combat in a random available direction.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            next guard.call if guard.call
            send_cmd.call(p.flee)
          end
        },

        "set_position" => {
          description: "Change body position. Use 'rest' or 'sleep' between fights to recover " \
                       "HP and mana. Must be standing to move or fight.",
          input_schema: {
            type: "object",
            properties: { "position" => str_param.call("Position: stand | sit | rest | sleep | wake") },
            required: ["position"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.set_position(args["position"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "track" => {
          description: "Attempt to track a mob or player by name, revealing which direction " \
                       "they are in. Requires the Track skill.",
          input_schema: {
            type: "object",
            properties: { "target" => str_param.call("Name of the mob or player to track") },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.track(args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "attack" => {
          description: "Attack a target. Style 'kill' is the standard approach; " \
                       "'murder' bypasses the mercy check; 'hit' is a one-off strike.",
          input_schema: {
            type: "object",
            properties: {
              "target" => str_param.call("Name of the mob or player to attack"),
              "style"  => str_param.call("Attack style: kill | hit | murder (default: kill)")
            },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.attack(args["style"] || "kill", args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "skill_strike" => {
          description: "Use a combat skill against a target.",
          input_schema: {
            type: "object",
            properties: {
              "skill"  => str_param.call("Skill: bash | kick | backstab | rescue | assist"),
              "target" => str_param.call("Name of the mob or player")
            },
            required: %w[skill target]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.skill_strike(args["skill"], args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "consider" => {
          description: "Assess a mob's relative strength before engaging in combat. " \
                       "Returns a phrase such as 'You could kill it easily' or " \
                       "'Death awaits you'. Always consider before attacking an unknown mob.",
          input_schema: {
            type: "object",
            properties: { "target" => str_param.call("Name of the mob to consider") },
            required: ["target"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.consider(args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "say" => {
          description: "Speak or emote in the current room.",
          input_schema: {
            type: "object",
            properties: {
              "text" => str_param.call("What to say or emote"),
              "mode" => str_param.call("Mode: say | emote | reply (default: say)")
            },
            required: ["text"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.say_local(args["mode"] || "say", args["text"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "tell" => {
          description: "Send a private message to a specific player.",
          input_schema: {
            type: "object",
            properties: {
              "target" => str_param.call("Player name to message"),
              "text"   => str_param.call("The message"),
              "mode"   => str_param.call("Mode: tell | whisper | ask (default: tell)")
            },
            required: %w[target text]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.say_targeted(args["mode"] || "tell", args["target"], args["text"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "channel_say" => {
          description: "Broadcast a message over a global channel.",
          input_schema: {
            type: "object",
            properties: {
              "channel" => str_param.call("Channel: shout | gossip | auction | grats | holler"),
              "text"    => str_param.call("The message to broadcast")
            },
            required: %w[channel text]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.say_channel(args["channel"], args["text"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "get_item" => {
          description: "Pick up an item from the room or from a container.",
          input_schema: {
            type: "object",
            properties: {
              "item"      => str_param.call("Name of the item to get"),
              "container" => str_param.call("Container to get it from (optional)"),
              "count"     => int_param.call("Number of items to get (optional)")
            },
            required: ["item"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.get(args["item"], container: args["container"], count: args["count"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "drop_item" => {
          description: "Drop, donate, or junk an item.",
          input_schema: {
            type: "object",
            properties: {
              "item"  => str_param.call("Name of the item"),
              "mode"  => str_param.call("Mode: drop | donate | junk (default: drop)"),
              "count" => int_param.call("Number of items (optional)")
            },
            required: ["item"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.drop(args["mode"] || "drop", args["item"], count: args["count"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "put_item" => {
          description: "Put an item into a container.",
          input_schema: {
            type: "object",
            properties: {
              "item"      => str_param.call("Name of the item to put"),
              "container" => str_param.call("Name of the container"),
              "count"     => int_param.call("Number of items (optional)")
            },
            required: %w[item container]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.put(args["item"], args["container"], count: args["count"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "equip_item" => {
          description: "Wear, wield, hold, grab, or remove an item.",
          input_schema: {
            type: "object",
            properties: {
              "item"     => str_param.call("Name of the item"),
              "action"   => str_param.call("Action: wear | wield | hold | grab | remove"),
              "body_loc" => str_param.call("Body location to wear on (optional, e.g. 'head', 'finger')")
            },
            required: %w[item action]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.equip(args["action"], args["item"], body_loc: args["body_loc"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "consume_item" => {
          description: "Eat, drink, taste, or sip a consumable item.",
          input_schema: {
            type: "object",
            properties: {
              "item" => str_param.call("Name of the item to consume"),
              "mode" => str_param.call("Mode: eat | drink | taste | sip (default: eat)")
            },
            required: ["item"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.consume(args["mode"] || "eat", args["item"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "cast_spell" => {
          description: "Cast a spell, optionally at a target.",
          input_schema: {
            type: "object",
            properties: {
              "spell"  => str_param.call("Full spell name (e.g. 'cure light wounds', 'magic missile')"),
              "target" => str_param.call("Target mob, player, or object (optional)")
            },
            required: ["spell"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.cast(args["spell"], target: args["target"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "use_magic_item" => {
          description: "Activate a magic item: quaff a potion, recite a scroll, or use a wand/staff.",
          input_schema: {
            type: "object",
            properties: {
              "item"        => str_param.call("Name of the item to activate"),
              "mode"        => str_param.call("Mode: quaff | recite | use"),
              "target_args" => str_param.call("Optional target arguments (e.g. mob name for a wand)")
            },
            required: %w[item mode]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.use_magic_item(args["mode"], args["item"], target_args: args["target_args"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "shop" => {
          description: "Interact with a shop NPC: list stock, buy, sell, or get the value of an item.",
          input_schema: {
            type: "object",
            properties: {
              "action" => str_param.call("Action: list | buy | sell | value | offer"),
              "args"   => str_param.call("Item name or number (optional)")
            },
            required: ["action"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            begin
              send_cmd.call(p.shop(args["action"], args: args["args"]))
            rescue ArgumentError => e
              "error: #{e.message}"
            end
          end
        },

        "practice" => {
          description: "List your known skills at a guildmaster, or practice a specific skill.",
          input_schema: {
            type: "object",
            properties: { "skill" => str_param.call("Skill name to practice (omit to list all)") },
            required: []
          },
          handler: lambda do |args|
            next guard.call if guard.call
            send_cmd.call(p.practice(args["skill"]))
          end
        },

        "save_character" => {
          description: "Save your character to disk so progress is not lost on disconnect.",
          input_schema: { type: "object", properties: {}, required: [] },
          handler: lambda do |_args|
            next guard.call if guard.call
            send_cmd.call(p.save_char)
          end
        },

        "send_raw" => {
          description: "Send an arbitrary command string to the MUD and return the response. " \
                       "Use this as an escape hatch when no structured tool fits.",
          input_schema: {
            type: "object",
            properties: { "command" => str_param.call("The raw command to send (e.g. 'who', 'help backstab')") },
            required: ["command"]
          },
          handler: lambda do |args|
            next guard.call if guard.call
            session.send_command(args["command"])
            session.read_until_quiet
          end
        }
      }
    end
  end
end
```

Modify `week0_explore/mud_manager/lib/mud_manager.rb` (final form after this task):

```ruby
module MudManager
end

require_relative "mud_manager/version"
require_relative "mud_manager/primitives"
require_relative "mud_manager/session"
require_relative "mud_manager/mcp_server"
require_relative "mud_manager/mcp_tools"
```

Create `week0_explore/mud_manager/bin/mud-manager`:

```ruby
#!/usr/bin/env ruby
# frozen_string_literal: true
#
# mud-manager --mcp
#
# Runs the MudManager MCP server over stdio: opens one Session, logs in once,
# then serves tools/list and tools/call for the lifetime of the process. This
# is the process Boukensha::Mcp::Client spawns to reach the MUD.
#
# Connection credentials come from the environment (not argv), since this is
# meant to be launched as a subprocess by an MCP client's `command`/`args`/`env`
# config rather than run interactively:
#
#   MUD_HOST      MUD server host (default: localhost)
#   MUD_PORT      MUD server port (default: 4000)
#   MUD_USERNAME  character name to log in as (required)
#   MUD_PASSWORD  character password (required)

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "mud_manager"

def usage
  <<~USAGE
    Usage: mud-manager --mcp

    Runs the MudManager MCP server over stdio. See the file header of
    bin/mud-manager for the environment variables it reads.
  USAGE
end

unless ARGV.include?("--mcp")
  warn usage
  exit 1
end

host     = ENV.fetch("MUD_HOST", "localhost")
port     = Integer(ENV.fetch("MUD_PORT", "4000"))
username = ENV["MUD_USERNAME"]
password = ENV["MUD_PASSWORD"]

if username.to_s.strip.empty? || password.to_s.strip.empty?
  warn "mud-manager --mcp: MUD_USERNAME and MUD_PASSWORD must be set"
  exit 1
end

session = MudManager::Session.new(host: host, port: port)

begin
  session.open
  session.login(username, password)
rescue MudManager::Session::Error => e
  warn "mud-manager --mcp: failed to connect/login: #{e.message}"
  exit 1
end

tools = MudManager::McpTools.build(session, name: username, password: password)
MudManager::McpServer.new(tools: tools).serve
```

Make it executable:

```bash
chmod +x week0_explore/mud_manager/bin/mud-manager
```

Modify `week0_explore/mud_manager/mud_manager.gemspec`:

```ruby
require_relative "lib/mud_manager/version"

Gem::Specification.new do |spec|
  spec.name        = "mud_manager"
  spec.version     = MudManager::VERSION
  spec.summary     = "MudManager — CircleMUD session management, command primitives, and an MCP server"
  spec.description = "Provides MudManager::Session (a long-lived telnet connection with " \
                     "background buffering and IAC stripping), MudManager::Primitives " \
                     "(a stateless library of typed CircleMUD command builders), and an " \
                     "MCP server (`mud-manager --mcp`) that exposes MUD gameplay as MCP tools " \
                     "over stdio so any MCP-capable client can reach the MUD."
  spec.authors     = ["Andrew Brown"]
  spec.email       = ["andrew@exampro.co"]
  spec.license     = "MIT"

  spec.required_ruby_version = ">= 3.0"

  spec.files = Dir["lib/**/*.rb"] + ["bin/mud-manager"]

  spec.bindir      = "bin"
  spec.executables = ["mud-manager"]

  # socket, thread, and json are stdlib — no external dependencies.
end
```

Add a section to `week0_explore/mud_manager/README.md` (append after the existing "## Examples" section):

```markdown
## MCP Server

`mud-manager --mcp` runs the same `MudManager::Session` + `MudManager::Primitives`
gameplay surface as an MCP server over stdio, so any MCP-capable client (in any
language) can drive the MUD without linking against this gem's Ruby code.

```sh
MUD_USERNAME=YourCharacterName MUD_PASSWORD=yourpassword ruby bin/mud-manager --mcp
```

It opens the connection and logs in once at startup, then serves `tools/list` /
`tools/call` requests for the lifetime of the process — the same "log in once,
reuse across many commands" model `Session` was built for.

Protocol-level tests (no live MUD required):

```sh
ruby examples/mcp_server_test.rb
ruby examples/mcp_tools_test.rb
```
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ruby week0_explore/mud_manager/examples/mcp_tools_test.rb`
Expected: `6 runs, ... 0 failures, 0 errors`

Also re-run Task 1's test to confirm the `mud_manager.rb` require-list edit didn't break it:

Run: `ruby week0_explore/mud_manager/examples/mcp_server_test.rb`
Expected: `6 runs, ... 0 failures, 0 errors`

- [ ] **Step 5: Commit**

```bash
git add week0_explore/mud_manager/lib/mud_manager/version.rb week0_explore/mud_manager/lib/mud_manager/mcp_tools.rb week0_explore/mud_manager/lib/mud_manager.rb week0_explore/mud_manager/bin/mud-manager week0_explore/mud_manager/mud_manager.gemspec week0_explore/mud_manager/README.md week0_explore/mud_manager/examples/mcp_tools_test.rb
git commit -m "mud_manager: add McpTools + bin/mud-manager --mcp, bump to 0.2.0"
```

---

## Task 3: `Boukensha::Mcp::Client` — spawn/handshake/list/call

**Files:**
- Create: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/mcp/client.rb`
- Create: `week1_baseline/ruby/10_standard_tool_library/examples/fixtures/echo_mcp_server.rb`
- Test: `week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`

**Interfaces:**
- Consumes: `Boukensha::VERSION` (`lib/boukensha/version.rb`, already `"0.10.0"`).
- Produces: `Boukensha::Mcp::Client.new(name:, command:, args: [], env: {})`, `#start` (returns self), `#tools_list` (returns `Array[Hash]` with string keys `"name"`/`"description"`/`"inputSchema"`), `#tools_call(tool_name, arguments_hash)` (returns a `String`, raises `Boukensha::Mcp::Client::Error` on `isError: true` or transport failure), `#stop`. `#name` reader.

- [ ] **Step 1: Write the failing test**

Create `week1_baseline/ruby/10_standard_tool_library/examples/fixtures/echo_mcp_server.rb` — a standalone MCP server fixture with **no** dependency on the `mud_manager` gem (Boukensha no longer depends on it after Task 5), just enough protocol to exercise the client:

```ruby
#!/usr/bin/env ruby
# frozen_string_literal: true
#
# Minimal standalone MCP server used only as a test fixture for
# Boukensha::Mcp::Client — implements just enough of the protocol
# (initialize, tools/list, tools/call) to exercise the client without
# depending on the mud_manager gem.
require "json"

TOOLS = {
  "echo" => {
    "description" => "Returns 'you said: <message>'",
    "inputSchema" => { "type" => "object", "properties" => { "message" => { "type" => "string" } }, "required" => [] }
  },
  "boom" => {
    "description" => "Always returns an error",
    "inputSchema" => { "type" => "object", "properties" => {}, "required" => [] }
  }
}.freeze

def respond(id, result)
  $stdout.puts(JSON.generate({ jsonrpc: "2.0", id: id, result: result }))
  $stdout.flush
end

$stdin.each_line do |line|
  line = line.strip
  next if line.empty?

  request = JSON.parse(line)
  id      = request["id"]
  method  = request["method"]

  case method
  when "initialize"
    respond(id, { protocolVersion: "2024-11-05", capabilities: { tools: {} },
                   serverInfo: { name: "echo-fixture", version: "0.0.1" } })
  when "notifications/initialized"
    next
  when "tools/list"
    respond(id, { tools: TOOLS.map { |name, t| { name: name, description: t["description"], inputSchema: t["inputSchema"] } } })
  when "tools/call"
    name = request.dig("params", "name")
    args = request.dig("params", "arguments") || {}
    case name
    when "echo"
      respond(id, { content: [{ type: "text", text: "you said: #{args['message']}" }], isError: false })
    when "boom"
      respond(id, { content: [{ type: "text", text: "boom: intentional failure" }], isError: true })
    else
      respond(id, { content: [{ type: "text", text: "error: unknown tool '#{name}'" }], isError: true })
    end
  else
    if id
      $stdout.puts(JSON.generate({ jsonrpc: "2.0", id: id, error: { code: -32601, message: "Method not found: #{method}" } }))
      $stdout.flush
    end
  end
end
```

Create `week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`:

```ruby
# frozen_string_literal: true

require "minitest/autorun"

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"

# Verifies Boukensha::Mcp::Client against a tiny standalone fixture MCP
# server (examples/fixtures/echo_mcp_server.rb) — no live MUD connection or
# LLM API key required.
#
# Run:
#   ruby examples/mcp_client_test.rb
class McpClientTest < Minitest::Test
  FIXTURE = File.expand_path("fixtures/echo_mcp_server.rb", __dir__)

  def setup
    @client = Boukensha::Mcp::Client.new(name: "echo", command: "ruby", args: [FIXTURE])
    @client.start
  end

  def teardown
    @client.stop
  end

  def test_tools_list_returns_the_fixture_tools
    names = @client.tools_list.map { |t| t["name"] }

    assert_equal %w[boom echo].sort, names.sort
  end

  def test_tools_call_returns_text_content
    result = @client.tools_call("echo", { message: "hi" })

    assert_equal "you said: hi", result
  end

  def test_tools_call_error_raises
    error = assert_raises(Boukensha::Mcp::Client::Error) do
      @client.tools_call("boom", {})
    end

    assert_match(/boom: intentional failure/, error.message)
  end
end
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`
Expected: `NameError: uninitialized constant Boukensha::Mcp` (Client doesn't exist yet — `boukensha.rb` won't even require it until Task 5, so for now `require_relative` it directly at the top of the test to isolate this task: add `require_relative "../lib/boukensha/mcp/client"` right after `require "boukensha"` in the test file above. Remove that extra line again in Task 5 once `boukensha.rb` requires it itself — leaving both is harmless but redundant.)

- [ ] **Step 3: Write minimal implementation**

Create `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/mcp/client.rb`:

```ruby
require "open3"
require "json"

module Boukensha
  module Mcp
    # A minimal MCP client over stdio: spawns a server process, performs the
    # initialize handshake, and exposes tools/list + tools/call as plain
    # Ruby methods. One Client instance owns one subprocess for its whole
    # lifetime — matching the "spawn once, call many times" shape
    # MudManager::Session already requires on the server side.
    class Client
      class Error < StandardError; end

      PROTOCOL_VERSION = "2024-11-05"

      attr_reader :name

      def initialize(name:, command:, args: [], env: {})
        @name     = name
        @command  = command
        @args     = args
        @env      = env.transform_keys(&:to_s).transform_values(&:to_s)
        @next_id  = 0
        @stdin    = nil
        @stdout   = nil
        @stderr   = nil
        @wait_thr = nil
      end

      def start
        @stdin, @stdout, @stderr, @wait_thr = Open3.popen3(@env, @command, *@args)
        handshake
        self
      rescue SystemCallError => e
        raise Error, "failed to start MCP server '#{@name}' (#{@command}): #{e.message}"
      end

      def tools_list
        request("tools/list", {})["tools"] || []
      end

      def tools_call(tool_name, arguments)
        result = request("tools/call", { name: tool_name, arguments: arguments })
        text   = (result["content"] || []).select { |b| b["type"] == "text" }.map { |b| b["text"] }.join
        raise Error, "tool '#{tool_name}' on '#{@name}' failed: #{text}" if result["isError"]

        text
      end

      def stop
        return unless @stdin

        begin
          @stdin.close
        rescue IOError
          nil
        end
        begin
          @stdout.close
        rescue IOError
          nil
        end
        begin
          @stderr.close
        rescue IOError
          nil
        end
        @wait_thr&.join(2)
      ensure
        @stdin = @stdout = @stderr = @wait_thr = nil
      end

      private

      def handshake
        request("initialize", {
                  protocolVersion: PROTOCOL_VERSION,
                  capabilities:    {},
                  clientInfo:      { name: "boukensha", version: Boukensha::VERSION }
                })
        notify("notifications/initialized", {})
      end

      def request(method, params)
        id = (@next_id += 1)
        write({ jsonrpc: "2.0", id: id, method: method, params: params })
        response = read_response(id)
        raise Error, "#{@name}: #{response['error']['message']}" if response["error"]

        response["result"] || {}
      end

      def notify(method, params)
        write({ jsonrpc: "2.0", method: method, params: params })
      end

      def write(payload)
        @stdin.puts(JSON.generate(payload))
        @stdin.flush
      rescue Errno::EPIPE
        raise Error, "MCP server '#{@name}' closed its input unexpectedly"
      end

      def read_response(expected_id)
        loop do
          line = @stdout.gets
          raise Error, "MCP server '#{@name}' closed its output unexpectedly (stderr: #{drain_stderr})" if line.nil?

          line = line.strip
          next if line.empty?

          message = JSON.parse(line)
          next if message["id"].nil?
          return message if message["id"] == expected_id
        end
      end

      def drain_stderr
        @stderr.read_nonblock(4096)
      rescue StandardError
        ""
      end
    end
  end
end
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`
Expected: `3 runs, ... 0 failures, 0 errors`

- [ ] **Step 5: Commit**

```bash
git add week1_baseline/ruby/10_standard_tool_library/lib/boukensha/mcp/client.rb week1_baseline/ruby/10_standard_tool_library/examples/fixtures/echo_mcp_server.rb week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb
git commit -m "boukensha: add Boukensha::Mcp::Client (spawn/handshake/tools_list/tools_call)"
```

---

## Task 4: `Boukensha::Tools::Mcp` — register a spawned server's tools

**Files:**
- Create: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/mcp.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/registry.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb` (append registration tests)

**Interfaces:**
- Consumes: `Boukensha::Mcp::Client` from Task 3 (`#tools_list`, `#tools_call`). `Boukensha::Registry` (`lib/boukensha/registry.rb`) — `#tool(name, description:, parameters:, &block)`.
- Produces: `Boukensha::Tools::Mcp.register(registry, client:, prefix: nil)` (raises `ArgumentError` on a tool-name collision). `Registry#registered?(name)` (new public method).

- [ ] **Step 1: Write the failing test**

Append to `week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb` (same file as Task 3 — same fixture server, different concern):

```ruby
class ToolsMcpRegistrationTest < Minitest::Test
  FIXTURE = File.expand_path("fixtures/echo_mcp_server.rb", __dir__)

  def setup
    @client = Boukensha::Mcp::Client.new(name: "echo", command: "ruby", args: [FIXTURE])
    @client.start
    @context  = Boukensha::Context.new(task: Boukensha::Tasks::Player, working_dir: nil)
    @registry = Boukensha::Registry.new(@context)
  end

  def teardown
    @client.stop
  end

  def test_registers_every_fixture_tool
    Boukensha::Tools::Mcp.register(@registry, client: @client)

    assert_equal %w[boom echo].sort, @context.tools.keys.sort
  end

  def test_prefixes_tool_names_when_given
    Boukensha::Tools::Mcp.register(@registry, client: @client, prefix: "mud")

    assert_equal %w[mud_boom mud_echo].sort, @context.tools.keys.sort
  end

  def test_dispatch_calls_through_to_the_mcp_server
    Boukensha::Tools::Mcp.register(@registry, client: @client)

    result = @registry.dispatch("echo", { "message" => "hello" })

    assert_equal "you said: hello", result
  end

  def test_raises_on_tool_name_collision
    Boukensha::Tools::Mcp.register(@registry, client: @client)

    error = assert_raises(ArgumentError) do
      Boukensha::Tools::Mcp.register(@registry, client: @client)
    end

    assert_match(/collision/, error.message)
  end
end
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`
Expected: `NameError: uninitialized constant Boukensha::Tools::Mcp` (also add `require_relative "../lib/boukensha/tools/mcp"` at the top of the test file, next to the Task 3 `require_relative` line, for the same "isolate this task before Task 5 wires it into boukensha.rb" reason.)

- [ ] **Step 3: Write minimal implementation**

Add to `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/registry.rb` (new public method, `dispatch` and `tool` unchanged):

```ruby
module Boukensha
  class Registry
    def initialize(context)
      @context = context
    end

    def tool(name, description:, parameters: {}, &block)
      tool = Tool.new(name.to_s, description, parameters, block)
      @context.register_tool(tool)
      tool
    end

    def registered?(name)
      @context.tools.key?(name.to_s)
    end

    def dispatch(name, args = {})
      tool = @context.tools[name.to_s]
      raise UnknownToolError, "No tool registered as '#{name}'" unless tool
      tool.block.call(**args.transform_keys(&:to_sym))
    end
  end
end
```

Create `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/mcp.rb`:

```ruby
module Boukensha
  module Tools
    # Mcp registers every tool a spawned MCP server declares (via
    # client.tools_list) into the local registry, translating each MCP
    # inputSchema into the {name => {type:, description:}} shape the rest of
    # Boukensha (PromptBuilder, the backends) already expects from
    # Registry#tool.
    #
    # Boukensha ships no tools of its own — every capability an agent has
    # comes from an MCP server registered this way, driven by the
    # mcp_servers: block in settings.yaml (see Boukensha.run / .repl).
    #
    # Usage:
    #
    #   client = Boukensha::Mcp::Client.new(name: "mud", command: "mud-manager", args: ["--mcp"]).start
    #   Boukensha::Tools::Mcp.register(registry, client: client, prefix: "mud")
    #
    # `prefix:` is client-side only (it does not change what name is sent
    # back to the server in tools/call) — it exists so two servers can both
    # expose a tool named e.g. "look" without colliding in the registry. A
    # collision between two servers' *effective* (post-prefix) tool names
    # raises rather than silently overwriting one of them.
    module Mcp
      def self.register(registry, client:, prefix: nil)
        client.tools_list.each do |tool|
          raw_name  = tool["name"]
          tool_name = prefix ? "#{prefix}_#{raw_name}" : raw_name

          if registry.registered?(tool_name)
            raise ArgumentError, "tool name collision: '#{tool_name}' is already registered " \
                                 "(from MCP server '#{client.name}') — pick a different prefix:"
          end

          schema     = tool["inputSchema"] || {}
          properties = schema["properties"] || {}
          parameters = properties.each_with_object({}) do |(param_name, param_schema), acc|
            acc[param_name.to_sym] = {
              type:        param_schema["type"],
              description: param_schema["description"]
            }
          end

          registry.tool tool_name,
            description: tool["description"].to_s,
            parameters:  parameters do |**args|
            client.tools_call(raw_name, args.transform_keys(&:to_s))
          end
        end
      end
    end
  end
end
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`
Expected: `7 runs, ... 0 failures, 0 errors` (3 from `McpClientTest` + 4 from `ToolsMcpRegistrationTest`)

- [ ] **Step 5: Commit**

```bash
git add week1_baseline/ruby/10_standard_tool_library/lib/boukensha/registry.rb week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/mcp.rb week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb
git commit -m "boukensha: add Boukensha::Tools::Mcp + Registry#registered?"
```

---

## Task 5: Wire `mcp_servers:` into `Boukensha.run`/`.repl`; delete the old built-in tools

**Files:**
- Modify: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/config.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/repl.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha_loader.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/Gemfile`
- Modify: `week1_baseline/ruby/10_standard_tool_library/boukensha.gemspec`
- Delete: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/mud.rb`
- Delete: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/file_system.rb`
- Delete: `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/shell.rb`
- Modify: `week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb` (remove the two isolation `require_relative` lines added in Tasks 3–4, now that `boukensha.rb` requires them itself)
- Create: `week1_baseline/ruby/10_standard_tool_library/examples/mcp_wiring_test.rb`

**Interfaces:**
- Consumes: `Boukensha::Mcp::Client` (Task 3), `Boukensha::Tools::Mcp` (Task 4).
- Produces: `Boukensha.run(..., mcp_servers: nil, &block)` / `Boukensha.repl(..., mcp_servers: nil, &block)` — `mcp_servers:` is `Hash[String, {command:, args:, env:, prefix:, required:}]`, defaulting to `Config#mcp_servers` (parsed from `settings.yaml`'s `mcp_servers:` block) when `nil`. `working_dir:` keyword survives but is Context metadata only. `allowed_commands:` and `shell_timeout:` keywords are removed (no shell tool ships built-in any more). `Boukensha.start_mcp_servers(registry, servers)` (private class method) — `Array[Boukensha::Mcp::Client]` of started, already-registered clients; a `servers` entry with `required: false` that fails to spawn is skipped with a `warn` instead of raising.

- [ ] **Step 1: Write the failing test**

Confirm the pre-edit regression baseline first:

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`
Expected (before this task's edits): `7 runs, ... 0 failures, 0 errors` (same as Task 4's end state — this task's file deletions/renames must not break it).

Then create `week1_baseline/ruby/10_standard_tool_library/examples/mcp_wiring_test.rb`, exercising `Boukensha.start_mcp_servers` directly (the one piece of genuinely new branching logic this task adds — success, `prefix:`, `required: false` downgrade, `required: true` (default) raise, and the empty-hash case — none of which Task 3/4's suite touches):

```ruby
# frozen_string_literal: true

require "minitest/autorun"

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"

# Verifies the Boukensha.run/.repl <-> mcp_servers: wiring itself (the
# private Boukensha.start_mcp_servers), using the same standalone fixture
# server as mcp_client_test.rb — no live MUD or LLM API key required.
#
# Run:
#   ruby examples/mcp_wiring_test.rb
class McpWiringTest < Minitest::Test
  FIXTURE = File.expand_path("fixtures/echo_mcp_server.rb", __dir__)

  def setup
    @context  = Boukensha::Context.new(task: Boukensha::Tasks::Player, working_dir: nil)
    @registry = Boukensha::Registry.new(@context)
  end

  def test_starts_and_registers_every_configured_server
    clients = Boukensha.send(:start_mcp_servers, @registry, {
      "echo" => { command: "ruby", args: [FIXTURE] }
    })

    assert_equal 1, clients.size
    assert_equal %w[boom echo].sort, @context.tools.keys.sort
  ensure
    clients&.each(&:stop)
  end

  def test_applies_prefix_from_config
    clients = Boukensha.send(:start_mcp_servers, @registry, {
      "echo" => { command: "ruby", args: [FIXTURE], prefix: "mud" }
    })

    assert_equal %w[mud_boom mud_echo].sort, @context.tools.keys.sort
  ensure
    clients&.each(&:stop)
  end

  def test_required_false_downgrades_a_failed_start_to_a_warning
    clients = nil
    _out, err = capture_io do
      clients = Boukensha.send(:start_mcp_servers, @registry, {
        "missing" => { command: "this-command-does-not-exist-12345", required: false }
      })
    end

    assert_empty clients
    assert_match(/missing.*failed to start/, err)
  end

  def test_required_true_raises_on_a_failed_start
    assert_raises(Boukensha::Mcp::Client::Error) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "missing" => { command: "this-command-does-not-exist-12345" }
      })
    end
  end

  def test_empty_servers_hash_returns_no_clients_and_registers_nothing
    clients = Boukensha.send(:start_mcp_servers, @registry, {})

    assert_empty clients
    assert_empty @context.tools
  end
end
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_wiring_test.rb`
Expected: `NoMethodError` — `Boukensha.start_mcp_servers` doesn't exist yet (the module only has `run`/`repl`/`config`/etc. so far).

- [ ] **Step 3: Write the implementation**

Delete the three old tool files:

```bash
rm week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/mud.rb
rm week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/file_system.rb
rm week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/shell.rb
```

Modify `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/config.rb` — replace the "MUD connection" section:

```ruby
    # ---------- MCP servers -------------------------------------------------

    # The full mcp_servers: hash from settings.yaml, e.g.
    #   { "mud" => { "command" => "mud-manager", "args" => ["--mcp"], "env" => {...} } }
    # Empty Hash (never nil) when unset, so callers can iterate unconditionally.
    def mcp_servers
      dig(:mcp_servers) || {}
    end
```

(replacing the previous `mud_host` / `mud_port` / `mud_username` / `mud_password` methods, which are now unused — nothing else in the codebase calls them after this task.)

Modify `week1_baseline/ruby/10_standard_tool_library/lib/boukensha.rb` in full:

```ruby
require_relative "boukensha/version"
require_relative "boukensha/config"
require_relative "boukensha/tasks/player"

module Boukensha
  @quiet  = false
  @debug  = false
  @config = nil

  def self.config
    @config ||= Config.new
  end

  def self.quiet!
    @quiet = true
  end

  def self.loud!
    @quiet = false
  end

  def self.quiet?
    @quiet
  end

  def self.debug!
    @debug = true
  end

  def self.debug?
    @debug
  end

  # One-shot run: send a single task, get a response, return.
  #
  # working_dir:  Context metadata only (returned by Context#working_dir).
  #               Boukensha registers no filesystem tools of its own — plug
  #               in a filesystem MCP server via mcp_servers: if an agent
  #               needs file access.
  #
  # mcp_servers:  Hash of server name => { command:, args:, env:, prefix:,
  #               required: }. Each entry is spawned via Boukensha::Mcp::Client
  #               and its tools registered into the registry (Boukensha::Tools::Mcp).
  #               required: false (default true) downgrades a failed spawn to
  #               a warning instead of raising. nil (default) uses
  #               config.mcp_servers (the mcp_servers: block in settings.yaml).
  #               Pass {} to run with no tools at all.
  def self.run(
    task:,
    system:            nil,
    model:             nil,
    backend:           nil,
    api_key:           nil,
    ollama_host:       "http://localhost:11434",
    log:               nil,
    max_output_tokens: nil,
    working_dir:       Dir.pwd,
    mcp_servers:       nil,
    &block
  )
    cfg           = config                           # loads .env; populates ENV
    task_class    = Tasks::Player
    task_settings = cfg.tasks(task_class.task_name)
    system      ||= task_class.system_prompt(task_settings, user_prompts_dir: cfg.user_prompts_dir, default_prompts_dir: Config::PROMPTS_DIR)
    model       ||= task_class.model(task_settings)
    backend     ||= task_class.provider(task_settings).to_sym
    api_key ||= case backend
                when :anthropic    then ENV["ANTHROPIC_API_KEY"]
                when :openai       then ENV["OPENAI_API_KEY"]
                when :gemini       then ENV["GEMINI_API_KEY"]
                when :ollama_cloud then ENV["OLLAMA_API_KEY"]
                end

    ctx      = Context.new(task: task_class, system: system, working_dir: working_dir)
    registry = Registry.new(ctx)
    clients  = start_mcp_servers(registry, mcp_servers || cfg.mcp_servers)

    RunDSL.new(registry).instance_eval(&block) if block

    be = case backend
         when :anthropic    then Backends::Anthropic.new(api_key: api_key, model: model)
         when :openai       then Backends::OpenAI.new(api_key: api_key, model: model)
         when :gemini       then Backends::Gemini.new(api_key: api_key, model: model)
         when :ollama       then Backends::Ollama.new(host: ollama_host, model: model)
         when :ollama_cloud then Backends::OllamaCloud.new(api_key: api_key, model: model)
         else raise ArgumentError, "Unknown backend #{backend.inspect}. Use :anthropic, :openai, :gemini, :ollama, or :ollama_cloud."
         end

    builder = PromptBuilder.new(ctx, be)
    client  = Client.new(builder)
    effective_max_iterations = task_class.max_iterations(task_settings)
    effective_max_output_tokens = max_output_tokens || task_class.max_output_tokens(task_settings)
    logger  = Logger.new(log: log, snapshot: {
      task:              task_class.task_name,
      max_iterations:    effective_max_iterations,
      max_output_tokens: effective_max_output_tokens,
      model:             model,
      provider:          backend
    })
    agent   = Agent.new(context: ctx, registry: registry, builder: builder, client: client, logger: logger,
                        task_settings: task_settings, max_iterations: effective_max_iterations, max_output_tokens: effective_max_output_tokens)

    ctx.add_message(:user, task)
    agent.run
  ensure
    clients&.each(&:stop)
    logger&.close
  end

  # Interactive REPL — see Boukensha.run for full option documentation.
  def self.repl(
    system:            nil,
    model:             nil,
    backend:           nil,
    api_key:           nil,
    ollama_host:       "http://localhost:11434",
    log:               nil,
    max_output_tokens: nil,
    working_dir:       Dir.pwd,
    mcp_servers:       nil,
    &block
  )
    cfg           = config                           # loads .env; populates ENV
    task_class    = Tasks::Player
    task_settings = cfg.tasks(task_class.task_name)
    system      ||= task_class.system_prompt(task_settings, user_prompts_dir: cfg.user_prompts_dir, default_prompts_dir: Config::PROMPTS_DIR)
    model       ||= task_class.model(task_settings)
    backend     ||= task_class.provider(task_settings).to_sym
    api_key ||= case backend
                when :anthropic    then ENV["ANTHROPIC_API_KEY"]
                when :openai       then ENV["OPENAI_API_KEY"]
                when :gemini       then ENV["GEMINI_API_KEY"]
                when :ollama_cloud then ENV["OLLAMA_API_KEY"]
                end

    ctx      = Context.new(task: task_class, system: system, working_dir: working_dir)
    registry = Registry.new(ctx)
    clients  = start_mcp_servers(registry, mcp_servers || cfg.mcp_servers)

    RunDSL.new(registry).instance_eval(&block) if block

    be = case backend
         when :anthropic    then Backends::Anthropic.new(api_key: api_key, model: model)
         when :openai       then Backends::OpenAI.new(api_key: api_key, model: model)
         when :gemini       then Backends::Gemini.new(api_key: api_key, model: model)
         when :ollama       then Backends::Ollama.new(host: ollama_host, model: model)
         when :ollama_cloud then Backends::OllamaCloud.new(api_key: api_key, model: model)
         else raise ArgumentError, "Unknown backend #{backend.inspect}. Use :anthropic, :openai, :gemini, :ollama, or :ollama_cloud."
         end

    builder = PromptBuilder.new(ctx, be)
    client  = Client.new(builder)
    effective_max_iterations = task_class.max_iterations(task_settings)
    effective_max_output_tokens = max_output_tokens || task_class.max_output_tokens(task_settings)
    logger  = Logger.new(log: log, snapshot: {
      task:              task_class.task_name,
      max_iterations:    effective_max_iterations,
      max_output_tokens: effective_max_output_tokens,
      model:             model,
      provider:          backend
    })

    Repl.new(
      context:    ctx,
      registry:   registry,
      builder:    builder,
      client:     client,
      logger:     logger,
      task_settings: task_settings,
      max_iterations:    effective_max_iterations,
      max_output_tokens: effective_max_output_tokens,
      config_dir: cfg.dir,
      provider:   backend,
      model:      model,
      version:    VERSION,
      api_key:    api_key,
      mcp_server_names: clients.map(&:name)
    ).start
  rescue Interrupt
    puts "\nInterrupted."
  ensure
    clients&.each(&:stop)
    logger&.close
  end

  # Spawn every configured MCP server and register its tools. Returns the
  # Array of started Mcp::Client instances (already registered), so the
  # caller can #stop them in its ensure block. A server with required: false
  # that fails to start is skipped with a warning instead of raising.
  def self.start_mcp_servers(registry, servers)
    return [] unless servers

    servers.filter_map do |server_name, raw_opts|
      opts     = raw_opts.transform_keys(&:to_sym)
      required = opts.key?(:required) ? opts[:required] : true

      client = Mcp::Client.new(
        name:    server_name.to_s,
        command: opts.fetch(:command),
        args:    opts[:args] || [],
        env:     opts[:env] || {}
      )

      begin
        client.start
        Tools::Mcp.register(registry, client: client, prefix: opts[:prefix])
        client
      rescue Mcp::Client::Error => e
        raise unless required == false

        warn "[boukensha] MCP server '#{server_name}' failed to start: #{e.message} (continuing without it)"
        nil
      end
    end
  end
  private_class_method :start_mcp_servers
end

require_relative "boukensha/tool"
require_relative "boukensha/message"
require_relative "boukensha/context"
require_relative "boukensha/errors"
require_relative "boukensha/registry"
require_relative "boukensha/prompt_builder"
require_relative "boukensha/logger"
require_relative "boukensha/backends/base"
require_relative "boukensha/backends/anthropic"
require_relative "boukensha/backends/gemini"
require_relative "boukensha/backends/ollama"
require_relative "boukensha/backends/ollama_cloud"
require_relative "boukensha/backends/openai"
require_relative "boukensha/client"
require_relative "boukensha/agent"
require_relative "boukensha/run_dsl"
require_relative "boukensha/repl"
require_relative "boukensha/mcp/client"
require_relative "boukensha/tools/mcp"
```

Modify `week1_baseline/ruby/10_standard_tool_library/lib/boukensha/repl.rb` — replace the `mud:`-specific parts. Full file:

```ruby
module Boukensha
  # Repl is the interactive session loop.
  #
  # It wraps the same primitives as a single Boukensha.run call, but instead of
  # running once it stays alive: it reads a task from the user, runs the agent,
  # prints the reply, and loops back to the prompt.
  #
  # The Context is shared across every turn so conversation history accumulates
  # naturally — the agent sees the full transcript each time it is called.
  #
  # Built-in commands (not sent to the agent):
  #   /help    print the command list
  #   /quiet   suppress detailed logging
  #   /loud    re-enable logging
  #   /clear   wipe conversation history (tools stay registered)
  #   /exit    leave the REPL
  #   /quit    alias for /exit
  class Repl
    PROMPT = "boukensha> "

    HELP = <<~HELP
      Commands:
        /quiet   suppress logging output
        /loud    re-enable logging output
        /clear   wipe conversation history (tools stay)
        /exit    leave the REPL
        /help    show this message
    HELP

    def initialize(context:, registry:, builder:, client:, logger:, config_dir: nil, provider: nil, model: nil, version: nil, api_key: nil, mcp_server_names: [], task_settings: nil, max_iterations: nil, max_output_tokens: nil)
      @context    = context
      @registry   = registry
      @builder    = builder
      @client     = client
      @logger     = logger
      @task_settings     = task_settings
      @max_iterations    = max_iterations
      @max_output_tokens = max_output_tokens
      @config_dir = config_dir
      @provider   = provider
      @model      = model
      @version    = version
      @api_key    = api_key
      @mcp_server_names = mcp_server_names
      @turn       = 0
    end

    def start
      puts banner
      loop do
        print PROMPT
        $stdout.flush

        input = $stdin.gets
        break unless input  # EOF / Ctrl-D

        input = input.chomp.strip
        next if input.empty?

        case input
        when "/exit", "/quit"
          puts "Goodbye."
          break
        when "/help"
          puts HELP
          next
        when "/quiet"
          Boukensha.quiet!
          puts "(logging suppressed — type /loud to re-enable)"
          next
        when "/loud"
          Boukensha.loud!
          puts "(logging enabled)"
          next
        when "/clear"
          @context.clear_messages!
          @turn = 0
          puts "(conversation history cleared)"
          next
        end

        run_turn(input)
      end
    end

    private

    def banner
      key_status    = (@api_key.nil? || @api_key.strip.empty?) ? "✗ API key not set" : "✓ API key set"
      provider_line = "#{@provider || "default"} (#{@model || "default"})  #{key_status}"
      config_exists = @config_dir && Dir.exist?(@config_dir)
      config_line   = config_exists ? @config_dir : "#{@config_dir || "(default)"}  ✗ directory not found"
      ver           = @version || "?.?.?"
      mcp_line      = @mcp_server_names.empty? ? "(none configured)" : @mcp_server_names.join(", ")

      <<~BANNER

        ╔══════════════════════════════════════╗
        ║  BOUKENSHA MUD Assistant (v#{ver})#{" " * (9 - ver.length)}║
        ╚══════════════════════════════════════╝
          config:      #{config_line}
          provider:    #{provider_line}
          mcp servers: #{mcp_line}

          /quiet or /loud   toggle logging
          /clear           reset conversation history
          /exit or /quit    leave the REPL

      BANNER
    end

    def run_turn(input)
      @turn += 1
      @logger.turn(n: @turn)

      @context.add_message(:user, input)

      agent  = Agent.new(
        context:  @context,
        registry: @registry,
        builder:  @builder,
        client:   @client,
        logger:   @logger,
        task_settings: @task_settings,
        max_iterations:    @max_iterations,
        max_output_tokens: @max_output_tokens
      )
      result = agent.run

      # Print the final response outside of the logger so it is always visible,
      # even when Boukensha.quiet! is active.
      puts
      puts result
    rescue LoopError => e
      puts "\n[error] #{e.message}"
    rescue ApiError => e
      puts "\n[error] API call failed: #{e.message}"
    end
  end
end
```

Modify `week1_baseline/ruby/10_standard_tool_library/lib/boukensha_loader.rb` — replace the header comment and the `MUD_NAME` legacy block:

```ruby
# BoukenshaLoader resolves which step folder to load from, then boots the REPL.
#
# Resolution order:
#   1. BOUKENSHA_PATH environment variable (selects which *step* lib to load)
#   2. ~/.boukensharc  (a file containing a single path)
#   3. The lib/ directory bundled inside this gem (step 10 — the latest release)
#
# Config directory (settings.yaml, .env, system.md) is separate:
#   BOUKENSHA_DIR=~/.boukensha  (default; set to override)
#
# MCP servers (tools) come from settings.yaml's mcp_servers: block by default.
# The legacy MUD_NAME / MUD_HOST / MUD_PORT / MUD_PASSWORD env vars are still
# honoured as a shortcut that builds a single "mud" mcp_servers: entry
# pointing at the `mud-manager` executable on PATH, and take precedence over
# config when set.
#
# Examples:
#   boukensha                                                              # uses bundled lib + ~/.boukensha
#   BOUKENSHA_PATH=~/Sites/boukensha/04_api_client boukensha              # loads step 4
#   BOUKENSHA_DIR=~/projects/mybot/.boukensha boukensha                   # custom config dir
#   echo ~/Sites/boukensha/10_standard_tool_library > ~/.boukensharc && boukensha
module BoukenshaLoader
  # Absolute path to this gem's own bundled boukensha lib.
  BUNDLED_LIB = File.expand_path("../boukensha.rb", __FILE__)

  def self.resolve
    # 1. Env var wins.
    if ENV["BOUKENSHA_PATH"]
      dir  = File.expand_path(ENV["BOUKENSHA_PATH"])
      main = File.join(dir, "lib", "boukensha.rb")
      return main if File.exist?(main)

      abort <<~MSG
        boukensha: BOUKENSHA_PATH is set but no lib/boukensha.rb found at:
               #{dir}
               Make sure BOUKENSHA_PATH points to a step folder, e.g.:
               BOUKENSHA_PATH=~/Sites/boukensha/07_the_repl_loop boukensha
      MSG
    end

    # 2. ~/.boukensharc
    rc = File.expand_path("~/.boukensharc")
    if File.exist?(rc)
      dir  = File.read(rc).strip
      unless dir.empty?
        main = File.join(File.expand_path(dir), "lib", "boukensha.rb")
        return main if File.exist?(main)

        abort <<~MSG
          boukensha: ~/.boukensharc points to #{dir}
                 but no lib/boukensha.rb was found there.
                 Update ~/.boukensharc or remove it to use the bundled default.
        MSG
      end
    end

    # 3. Bundled default.
    BUNDLED_LIB
  end

  def self.load_and_start_repl
    main = resolve
    step_dir = File.dirname(File.dirname(main))

    puts "[boukensha] loading from: #{step_dir}" if ENV["BOUKENSHA_DEBUG"]

    require main

    unless Boukensha.respond_to?(:repl)
      abort <<~MSG
        boukensha: the step at #{step_dir}
               does not support the interactive REPL (added in step 7).
               Run its examples directly, e.g.:
                 ruby #{step_dir}/examples/*.rb
               Or point BOUKENSHA_PATH at step 7 or later.
      MSG
    end

    repl_opts = {}

    if ENV["MUD_NAME"]
      # Legacy env-var override still works and takes precedence over config:
      # builds a single "mud" mcp_servers: entry instead of a hard-coded tool.
      repl_opts[:working_dir] = false
      repl_opts[:mcp_servers] = {
        "mud" => {
          command: "mud-manager",
          args:    ["--mcp"],
          env: {
            "MUD_HOST"     => ENV.fetch("MUD_HOST", "localhost"),
            "MUD_PORT"     => ENV.fetch("MUD_PORT", "4000"),
            "MUD_USERNAME" => ENV.fetch("MUD_NAME"),
            "MUD_PASSWORD" => ENV.fetch("MUD_PASSWORD") { abort "boukensha: MUD_NAME is set but MUD_PASSWORD is missing." }
          }
        }
      }
    end
    # If MUD_NAME is not set, Boukensha.repl will fall back to config.mcp_servers
    # (settings.yaml's mcp_servers: block) automatically.

    Boukensha.repl(**repl_opts)
  end
end
```

Modify `week1_baseline/ruby/10_standard_tool_library/Gemfile` — Boukensha no longer `require`s the `mud_manager` gem directly (it only shells out to whatever `mcp_servers.*.command` says), so drop the path dependency:

```ruby
source "https://rubygems.org"

gem "dotenv"

gemspec
```

Modify `week1_baseline/ruby/10_standard_tool_library/boukensha.gemspec` — drop the `mud_manager` dependency line:

```ruby
require_relative "lib/boukensha/version"

Gem::Specification.new do |spec|
  spec.name        = "boukensha"
  spec.version     = Boukensha::VERSION
  spec.summary     = "BOUKENSHA — a tiny teaching framework for coding harnesses"
  spec.description = "Step-by-step coding harness framework. " \
                     "Set BOUKENSHA_PATH to load a specific lesson step, " \
                     "or run with defaults to use the bundled release."
  spec.authors     = ["Andrew Brown"]
  spec.email       = ["andrew@exampro.co"]
  spec.license     = "MIT"

  spec.required_ruby_version = ">= 3.0"

  # All files tracked in git, plus the bin/ executable.
  spec.files = Dir["lib/**/*.rb"] + ["bin/boukensha"]

  spec.bindir      = "bin"
  spec.executables = ["boukensha"]

  # net/http, json, and open3 are stdlib. Users supply their own ANTHROPIC_API_KEY.
  # MUD (and any other) tools come from MCP servers configured in settings.yaml —
  # see mcp_servers: in Boukensha::Config. No tool-specific gem dependency here.
end
```

In `week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`, remove the two isolation lines added in Tasks 3 and 4 (`require_relative "../lib/boukensha/mcp/client"` and `require_relative "../lib/boukensha/tools/mcp"`), since `require "boukensha"` now pulls both in transitively.

Regenerate the lockfile (Gemfile changed):

```bash
cd week1_baseline/ruby/10_standard_tool_library
bundle install
cd -
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_wiring_test.rb`
Expected: `5 runs, ... 0 failures, 0 errors`

Run: `ruby week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb`
Expected: `7 runs, ... 0 failures, 0 errors` (unchanged from Task 4 — confirms the `require "boukensha"` wiring alone is now sufficient, and that deleting the three old tool files didn't break anything this suite exercises).

Also sanity-check the library still loads standalone with no syntax errors from the deletions:

Run: `ruby -e "require_relative 'week1_baseline/ruby/10_standard_tool_library/lib/boukensha'; puts Boukensha::VERSION"`
Expected: `0.10.0`

- [ ] **Step 5: Commit**

```bash
git add week1_baseline/ruby/10_standard_tool_library/lib/boukensha.rb week1_baseline/ruby/10_standard_tool_library/lib/boukensha/config.rb week1_baseline/ruby/10_standard_tool_library/lib/boukensha/repl.rb week1_baseline/ruby/10_standard_tool_library/lib/boukensha_loader.rb week1_baseline/ruby/10_standard_tool_library/Gemfile week1_baseline/ruby/10_standard_tool_library/Gemfile.lock week1_baseline/ruby/10_standard_tool_library/boukensha.gemspec week1_baseline/ruby/10_standard_tool_library/examples/mcp_client_test.rb week1_baseline/ruby/10_standard_tool_library/examples/mcp_wiring_test.rb
git rm week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/mud.rb week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/file_system.rb week1_baseline/ruby/10_standard_tool_library/lib/boukensha/tools/shell.rb
git commit -m "boukensha: rewire Boukensha.run/.repl onto mcp_servers:, delete built-in tools"
```

---

## Task 6: Update `10_standard_tool_library`'s README and rebuild both gems

**Files:**
- Modify: `week1_baseline/ruby/10_standard_tool_library/README.md`

**Interfaces:**
- None (documentation only).

- [ ] **Step 1: Rewrite the README**

Replace the full contents of `week1_baseline/ruby/10_standard_tool_library/README.md`:

```markdown
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
```

- [ ] **Step 2: Rebuild both gems**

```bash
cd week0_explore/mud_manager
gem build mud_manager.gemspec
cd -

cd week1_baseline/ruby/10_standard_tool_library
gem build boukensha.gemspec
cd -
```

Expected: `Successfully built RubyGem` for both, producing `mud_manager-0.2.0.gem` and `boukensha-0.10.0.gem`.

- [ ] **Step 3: Commit**

```bash
git add week1_baseline/ruby/10_standard_tool_library/README.md week0_explore/mud_manager/mud_manager-0.2.0.gem week1_baseline/ruby/10_standard_tool_library/boukensha-0.10.0.gem
git commit -m "docs: document the mcp_servers: MCP-host model in step 10's README"
```

(If the repo's `.gitignore` excludes built `.gem` artifacts — check `week1_baseline/.gitignore`'s `!*.gemspec` line, which implies `*.gem` files elsewhere in the tree are tracked deliberately since the old `boukensha-0.10.0.gem` and `mud_manager-0.1.0.gem` are already committed — only `git add` the rebuilt `.gem` files if `git status` shows them as tracked/modified, not ignored.)

---

## Task 7: End-to-end integration test against a real MUD

**Files:** none (verification only — this is the "testing and integrating with the latest ruby step" the plan exists to satisfy).

**Interfaces:** none.

- [ ] **Step 1: Point local config at the new `mcp_servers:` schema**

In your local `.boukensha/settings.yaml` (resolved from `BOUKENSHA_DIR`, which
`examples/example.rb` sets to `week1_baseline/.boukensha` — create that
directory and file if it doesn't exist yet), replace any existing `mud:`
block with:

```yaml
mcp_servers:
  mud:
    command: ruby
    args:
      - <absolute-path-to-repo>/week0_explore/mud_manager/bin/mud-manager
      - --mcp
    env:
      MUD_HOST: localhost
      MUD_PORT: "4000"
      MUD_USERNAME: <your character name>
      MUD_PASSWORD: <your character password>
```

Ensure `.boukensha/.env` still has `ANTHROPIC_API_KEY` set (unchanged by this
plan) and a live CircleMUD-compatible server is reachable at
`MUD_HOST:MUD_PORT`.

- [ ] **Step 2: Run the demo end-to-end**

```bash
cd week1_baseline/ruby/10_standard_tool_library
ruby examples/example.rb
```

Expected: the same behavior as before this plan (agent connects to the MUD,
looks around, reports score/exits) — but now every MUD action round-trips
through `Boukensha::Mcp::Client → mud-manager --mcp → MudManager::Session`
instead of an in-process `Tools::Mud`. A connection or tool-call failure at
any hop surfaces as a normal tool-result error string the agent sees (not a
crash), matching the old `Tools::Mud` guard behavior.

- [ ] **Step 3: Confirm the MCP round-trip specifically (not just "it worked")**

Run with `BOUKENSHA_DEBUG=1` or inspect the JSONL log (`log:` option / default
log path) for at least one `tool_call` / `tool_result` pair on a MUD tool
(e.g. `look`), and confirm via `ps`/Task Manager that a `mud-manager --mcp`
(or `ruby .../bin/mud-manager --mcp`) child process is running for the
duration of the script — evidence the tool call actually left the Boukensha
process rather than hitting an in-process stub.

- [ ] **Step 4: Record the result**

Append a dated entry to the "## Technical observations" section of
`week1_baseline/ruby/10_standard_tool_library/README.md` describing what was
verified (pass/fail, any CircleMUD quirks hit through the extra hop — e.g.
timing changes from the subprocess round-trip) — following the same
convention as the two existing bullet points in that section.

- [ ] **Step 5: Commit**

```bash
git add week1_baseline/ruby/10_standard_tool_library/README.md
git commit -m "docs: record end-to-end MCP integration test result for step 10"
```

---

## Self-Review Notes

- **Spec coverage:** `docs/plans/mud_manager/generic_interfacing.md`'s four recommendation points are covered: (1) `--mcp` server mode shipped inside the `mud_manager` gem itself — Tasks 1–2; (2) Boukensha generalized to a tool-free MCP host — Tasks 3–5; (3) stdio transport, one client process per one server process — Task 3's `Client`/Task 1's `McpServer`; (4) "spike a client library per language track" is explicitly out of scope here (Ruby-only, per the user's framing) and is left to whichever future plan ports step 10 to another language.
- **Placeholder scan:** every task ships complete, runnable code — no `TODO`/`TBD`, no "similar to Task N" cross-references without the actual code repeated in place.
- **Type consistency:** `{description:, input_schema:, handler:}` (Task 1/2, server-side) and `{"name", "description", "inputSchema"}` (Task 3/4, client-side, string-keyed because it's parsed JSON) are intentionally different shapes on either side of the wire — verified consistent within each side across all tasks that touch them.
