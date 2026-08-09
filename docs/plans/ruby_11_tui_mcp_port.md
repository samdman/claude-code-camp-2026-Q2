# Port the MCP Delta from 10_standard_tool_library into 11_tui — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring `week1_baseline/ruby/11_tui` up to the same MCP-host tool architecture that `week1_baseline/ruby/10_standard_tool_library` now has (`Boukensha::Mcp::Client`, `Boukensha::Tools::Mcp`, `mcp_servers:` config, and the hardened `start_mcp_servers` spawn/cleanup logic), while preserving everything `11_tui` added on top of step 10 (the `Boukensha::Tui` charm-ruby front end, `Repl#on_output`/`#handle_command`, `Logger#subscribe`, the `--no-tui` flag).

**Architecture:** `11_tui` currently forks from an *earlier* version of step 10 — one that still shipped built-in `Tools::FileSystem` / `Tools::Shell` / `Tools::Mud`. Since that fork point, step 10 deleted all three built-ins and replaced them with an MCP-only model: every tool an agent can call comes from a server declared in `mcp_servers:` (settings.yaml), spawned via `Boukensha::Mcp::Client` and registered via `Boukensha::Tools::Mcp`. This plan re-applies that same swap to `11_tui`, on top of its existing TUI layer, which is orthogonal to where tools come from and needs no architectural change — only its `Repl` banner (which currently shows raw MUD connection status) needs to swap "mud status" for "list of connected MCP servers," matching what `Repl` already shows in step 10.

**Tech Stack:** Ruby (stdlib `open3`, `json`), `charm` gem (bubbletea/lipgloss/bubbles) for the TUI, `minitest` for the example-driven test scripts under `examples/`.

## Global Constraints

- End state must match `10_standard_tool_library`'s tool architecture exactly: **no** built-in `Tools::FileSystem`, `Tools::Shell`, or `Tools::Mud` — every capability comes from `mcp_servers:`. (Confirmed via `AskUserQuestion`: user chose "MCP-only (match 10 exactly)" over the additive alternative.)
- Preserve every `11_tui`-specific improvement already in place: `Repl#on_output`, `Repl#handle_command`, `Repl` attr_readers (`logger`, `context`, `model`, `version`), `Logger#subscribe`, `Boukensha::Tui`, the `--no-tui` CLI flag and `tui:` keyword on `Boukensha.repl`. Do **not** reintroduce `/quiet` / `/loud` — `11_tui` already dropped them; that removal predates this plan and is out of scope to reverse.
- Copy `Boukensha::Mcp::Client`, `Boukensha::Tools::Mcp`, and `Boukensha.start_mcp_servers` (including its subprocess-cleanup-on-failure logic — the "4 important findings" fixes from commit `90b8c58`) **verbatim** from `10_standard_tool_library`. That logic is already reviewed and covered by tests; do not re-derive it.
- Leave `VERSION` at `0.11.0` — this is not a version bump task.
- Match existing 2-space Ruby indentation and each file's existing `require_relative` ordering convention (do not alphabetize wholesale).
- All file paths below are relative to `week1_baseline/ruby/11_tui/` unless stated otherwise.

---

### Task 1: Additive config/registry support (`Config#mcp_servers`, `Registry#registered?`)

**Files:**
- Modify: `lib/boukensha/config.rb`
- Modify: `lib/boukensha/registry.rb`

**Interfaces:**
- Produces: `Boukensha::Config#mcp_servers` → `Hash` (never `nil`), read from `settings.yaml`'s `mcp_servers:` block.
- Produces: `Boukensha::Registry#registered?(name)` → `Boolean`, used by `Tools::Mcp.register` (Task 2) to detect tool-name collisions.

This task is purely additive — `Config#mud_host` / `#mud_port` / `#mud_username` / `#mud_password` stay for now (Task 3 removes them once nothing references them).

- [ ] **Step 1: Add `mcp_servers` to `Config`**

In `lib/boukensha/config.rb`, add this method right after `user_prompts_dir` (before the `# ---------- MUD connection` section, which Task 3 will remove):

```ruby
    # ---------- MCP servers -------------------------------------------------

    # The full mcp_servers: hash from settings.yaml, e.g.
    #   { "mud" => { "command" => "mud-manager", "args" => ["--mcp"], "env" => {...} } }
    # Empty Hash (never nil) when unset, so callers can iterate unconditionally.
    def mcp_servers
      dig(:mcp_servers) || {}
    end
```

- [ ] **Step 2: Add `registered?` to `Registry`**

In `lib/boukensha/registry.rb`, add this method between `tool` and `dispatch`:

```ruby
    def registered?(name)
      @context.tools.key?(name.to_s)
    end
```

The full file should read:

```ruby
require_relative "errors"

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

- [ ] **Step 3: Verify both files still load cleanly**

Run:
```sh
cd week1_baseline/ruby/11_tui
ruby -Ilib -e 'require "boukensha"; puts Boukensha.config.mcp_servers.inspect; puts Boukensha::Registry.instance_method(:registered?)'
```
Expected: prints `{}` (or the current `~/.boukensha/settings.yaml` `mcp_servers:` value) followed by `#<UnboundMethod: Boukensha::Registry#registered?>`. No `NoMethodError` / `LoadError`.

- [ ] **Step 4: Commit**

```sh
git add lib/boukensha/config.rb lib/boukensha/registry.rb
git commit -m "boukensha: add Config#mcp_servers and Registry#registered? (additive)"
```

---

### Task 2: Add `Boukensha::Mcp::Client` and `Boukensha::Tools::Mcp`

**Files:**
- Create: `lib/boukensha/mcp/client.rb`
- Create: `lib/boukensha/tools/mcp.rb`
- Modify: `lib/boukensha.rb` (add two `require_relative` lines only — no other change yet)
- Create: `examples/fixtures/echo_mcp_server.rb`
- Create: `examples/mcp_client_test.rb`

**Interfaces:**
- Consumes: `Registry#registered?` from Task 1.
- Produces: `Boukensha::Mcp::Client.new(name:, command:, args: [], env: {})` with `#start`, `#tools_list`, `#tools_call(name, args)`, `#stop`, `#name`, and `Boukensha::Mcp::Client::Error`.
- Produces: `Boukensha::Tools::Mcp.register(registry, client:, prefix: nil)`.

- [ ] **Step 1: Create `lib/boukensha/mcp/client.rb`**

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
        @name          = name
        @command       = command
        @args          = args
        @env           = env.transform_keys(&:to_s).transform_values(&:to_s)
        @next_id       = 0
        @stdin         = nil
        @stdout        = nil
        @stderr        = nil
        @wait_thr      = nil
        @stderr_thread = nil
        @stderr_buf    = +""
        @stderr_mutex  = Mutex.new
      end

      def start
        @stdin, @stdout, @stderr, @wait_thr = Open3.popen3(@env, @command, *@args)
        @stderr_thread = Thread.new do
          begin
            @stderr.each_line { |line| @stderr_mutex.synchronize { @stderr_buf << line } }
          rescue IOError, Errno::EBADF
            nil
          end
        end
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
        @stderr_thread&.join(2)
        begin
          @stderr.close
        rescue IOError
          nil
        end
        @wait_thr&.join(2)
      ensure
        @stdin = @stdout = @stderr = @wait_thr = @stderr_thread = nil
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

          begin
            message = JSON.parse(line)
          rescue JSON::ParserError
            next
          end

          next if message["id"].nil?
          return message if message["id"] == expected_id
        end
      end

      def drain_stderr
        @stderr_mutex.synchronize { @stderr_buf.dup }
      end
    end
  end
end
```

- [ ] **Step 2: Create `lib/boukensha/tools/mcp.rb`**

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

- [ ] **Step 3: Require both new files from `lib/boukensha.rb`**

Add these two lines to the `require_relative` block at the bottom of `lib/boukensha.rb`, directly after `require_relative "boukensha/repl"` and before `require_relative "boukensha/tools/file_system"`:

```ruby
require_relative "boukensha/mcp/client"
require_relative "boukensha/tools/mcp"
```

(`tools/file_system`, `tools/shell`, `tools/mud`, and `tui` requires stay untouched for now — Task 3 removes the first three.)

- [ ] **Step 4: Create the standalone MCP fixture server, `examples/fixtures/echo_mcp_server.rb`**

```ruby
#!/usr/bin/env ruby
# frozen_string_literal: true
#
# Minimal standalone MCP server used only as a test fixture for
# Boukensha::Mcp::Client — implements just enough of the protocol
# (initialize, tools/list, tools/call) to exercise the client without
# depending on the mud_manager gem.
#
# Two opt-in behaviors for regression tests, both gated behind env vars so
# the fixture's default behavior (used by the rest of this file's tests) is
# unaffected:
#   NOISY_PREFIX=1      - print one non-JSON line to stdout before any real
#                          protocol traffic, simulating a stray warning or
#                          banner a real MCP server might emit on startup.
#   STDERR_FLOOD_KB=<n> - write n KB of stderr output immediately before
#                          responding to any tools/call, simulating a server
#                          that logs heavily during normal operation (used to
#                          exercise continuous stderr draining).
require "json"

if ENV["NOISY_PREFIX"]
  $stdout.puts "not json — a stray banner line printed before any real protocol traffic"
  $stdout.flush
end

STDERR_FLOOD_KB = ENV["STDERR_FLOOD_KB"] && ENV["STDERR_FLOOD_KB"].to_i

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

    if STDERR_FLOOD_KB
      $stderr.write("x" * (STDERR_FLOOD_KB * 1024))
      $stderr.flush
    end

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

- [ ] **Step 5: Create `examples/mcp_client_test.rb`**

```ruby
# frozen_string_literal: true

require "minitest/autorun"
require "timeout"

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

# Regression test for finding 2: a server that writes one non-JSON line to
# stdout before any real protocol traffic (a stray warning, a shell wrapper
# banner, a bundler notice) must not crash the client with an unrescued
# JSON::ParserError — read_response should skip unparseable lines and keep
# reading until the real response arrives.
class McpClientNoisyPrefixTest < Minitest::Test
  FIXTURE = File.expand_path("fixtures/echo_mcp_server.rb", __dir__)

  def setup
    @client = Boukensha::Mcp::Client.new(name: "noisy", command: "ruby", args: [FIXTURE],
                                          env: { "NOISY_PREFIX" => "1" })
  end

  def teardown
    @client.stop
  end

  def test_start_survives_a_leading_non_json_line_on_stdout
    # The fixture prints its garbage banner line before it ever reads a
    # request, so it's already sitting ahead of the initialize response by
    # the time #start reads for it — this exercises the fix directly in the
    # handshake itself, not just a later call.
    @client.start

    names = @client.tools_list.map { |t| t["name"] }
    assert_equal %w[boom echo].sort, names.sort
  end
end

# Regression test for finding 3: the child's stderr must be drained
# continuously in the background, not just on-demand when stdout hits EOF —
# otherwise a server that writes enough stderr output to fill the OS pipe
# buffer during normal operation blocks on that write, and the client (blocked
# in a timeout-less @stdout.gets) hangs forever waiting for a response that
# will never come. 200KB is comfortably larger than a typical OS pipe buffer
# (commonly ~64KB on Linux, and this repo runs on Windows Ruby via Git Bash)
# so this reliably reproduces the deadlock without the fix.
class McpClientStderrFloodTest < Minitest::Test
  FIXTURE = File.expand_path("fixtures/echo_mcp_server.rb", __dir__)

  def setup
    @client = Boukensha::Mcp::Client.new(name: "flood", command: "ruby", args: [FIXTURE],
                                          env: { "STDERR_FLOOD_KB" => "200" })
    @client.start
  end

  def teardown
    @client.stop
  end

  def test_tools_call_does_not_hang_when_the_server_floods_stderr
    result = nil

    Timeout.timeout(10) do
      result = @client.tools_call("echo", { message: "hi" })
    end

    assert_equal "you said: hi", result
  rescue Timeout::Error
    flunk "tools_call hung — the child likely blocked writing to a full stderr pipe " \
      "while the client was blocked reading stdout (the deadlock finding 3 fixes)"
  end
end

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

- [ ] **Step 6: Run it and verify all assertions pass**

```sh
cd week1_baseline/ruby/11_tui
ruby examples/mcp_client_test.rb
```
Expected: all tests green (`0 failures, 0 errors`). At this point `Boukensha::Tools::FileSystem`/`Shell`/`Mud` are still loaded too (Task 3 removes them) — that's expected and harmless here.

- [ ] **Step 7: Commit**

```sh
git add lib/boukensha/mcp/client.rb lib/boukensha/tools/mcp.rb lib/boukensha.rb \
        examples/fixtures/echo_mcp_server.rb examples/mcp_client_test.rb
git commit -m "boukensha: add Mcp::Client + Tools::Mcp (ported from 10_standard_tool_library)"
```

---

### Task 3: Swap the tool architecture — wire `mcp_servers:`, delete the built-in tool modules

**Files:**
- Modify: `lib/boukensha.rb` (rewrite `Boukensha.run`, `Boukensha.repl`, add `Boukensha.start_mcp_servers`, drop `mud_opts_from_config`, update the bottom `require_relative` block)
- Modify: `lib/boukensha/repl.rb` (swap `mud:`/banner mud status for `mcp_server_names:`/banner mcp line)
- Modify: `lib/boukensha/config.rb` (remove `mud_host`/`mud_port`/`mud_username`/`mud_password`)
- Delete: `lib/boukensha/tools/file_system.rb`
- Delete: `lib/boukensha/tools/shell.rb`
- Delete: `lib/boukensha/tools/mud.rb`
- Create: `examples/mcp_wiring_test.rb`

**Interfaces:**
- Consumes: `Boukensha::Mcp::Client`, `Boukensha::Tools::Mcp` (Task 2); `Config#mcp_servers`, `Registry#registered?` (Task 1).
- Produces: `Boukensha.run(task:, ..., working_dir: Dir.pwd, mcp_servers: nil)` — `working_dir:` is now Context metadata only. `Boukensha.repl(..., mcp_servers: nil, tui: true)`. Both use the private `Boukensha.start_mcp_servers(registry, servers)` → `Array<Mcp::Client>`.
- Produces: `Repl.new(..., mcp_server_names: [], ...)` (replaces the old `mud:` keyword).

This is one task (not split further) because `lib/boukensha.rb`'s `Repl.new` call site and `Repl#initialize`'s signature must change together — an intermediate state where one expects `mud:` and the other passes `mcp_server_names:` would raise `ArgumentError` on every `Boukensha.repl` call.

- [ ] **Step 1: Remove MUD accessors from `Config`**

In `lib/boukensha/config.rb`, delete the entire `# ---------- MUD connection --------------------------------------------` section (the `mud_host`, `mud_port`, `mud_username`, `mud_password` methods added before this plan). The file should now go straight from `user_prompts_dir` to the `mcp_servers` method added in Task 1, then to `# ---------- low-level helpers -----------------------------------------`.

- [ ] **Step 2: Delete the three built-in tool files**

```sh
git rm lib/boukensha/tools/file_system.rb lib/boukensha/tools/shell.rb lib/boukensha/tools/mud.rb
```

- [ ] **Step 3: Rewrite `lib/boukensha.rb`**

Replace the entire file with:

```ruby
require_relative "boukensha/version"
require_relative "boukensha/config"
require_relative "boukensha/tasks/player"

module Boukensha
  @debug  = false
  @config = nil

  def self.config
    @config ||= Config.new
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
  #
  # tui: true (default) wraps the REPL in a charm-ruby TUI.  Pass tui: false or
  # use the --no-tui CLI flag to fall back to the plain terminal REPL.
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
    tui:               true,
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

    repl = Repl.new(
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
    )

    if tui && defined?(Tui)
      Tui.new(repl).start
    else
      repl.start
    end
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
  #
  # Any client that DID start successfully before a later entry fails is
  # stopped here (not left for the caller's ensure block to clean up) —
  # the caller's `clients = start_mcp_servers(...)` assignment never
  # completes when this method raises, so its own `ensure clients&.each(&:stop)`
  # would otherwise never run against the partially-started batch.
  def self.start_mcp_servers(registry, servers)
    return [] unless servers

    started = []
    servers.each do |server_name, raw_opts|
      client   = nil
      required = true

      begin
        # opts/required/client all live inside this begin now: a malformed
        # entry (missing `command:`, or a non-Hash value) can raise
        # KeyError/NoMethodError before `client` is ever assigned, and that
        # must still hit the cleanup below — otherwise every already-started
        # client in this batch leaks its subprocess, and a malformed
        # required: false entry would abort the whole host instead of being
        # skipped. `client`/`required` are predeclared above so both rescue
        # clauses can reference them safely even when the crash happens
        # before this begin ever assigns them.
        opts     = raw_opts.transform_keys(&:to_sym)
        required = opts.key?(:required) ? opts[:required] : true

        client = Mcp::Client.new(
          name:    server_name.to_s,
          command: opts.fetch(:command),
          args:    opts[:args] || [],
          env:     opts[:env] || {}
        )

        client.start
        Tools::Mcp.register(registry, client: client, prefix: opts[:prefix])
        started << client
      rescue Mcp::Client::Error => e
        # This client's own subprocess may already have spawned (e.g. the
        # handshake failed after Open3.popen3 succeeded) even though it
        # never made it into `started` — stop it regardless. #stop is a
        # safe no-op if it never actually started (it guards on @stdin).
        # client may still be nil here if the crash happened before
        # Mcp::Client.new ran, hence `&.`.
        client&.stop
        if required == false
          warn "[boukensha] MCP server '#{server_name}' failed to start: #{e.message} (continuing without it)"
        else
          # A sibling `rescue StandardError` below would NOT catch a bare
          # `raise` from this clause (rescue clauses in the same begin
          # don't fall through to one another), so the cleanup has to live
          # here too — otherwise a required: true failure leaks every
          # client started earlier in this loop.
          started.each(&:stop)
          raise
        end
      rescue StandardError
        # e.g. a Tools::Mcp.register tool-name collision (ArgumentError), or
        # a malformed entry raising KeyError/NoMethodError before `client`
        # was ever assigned: the subprocess may have spawned fine, so stop
        # the current client too (if there is one), not just the ones from
        # earlier iterations.
        client&.stop
        started.each(&:stop)
        raise
      end
    end

    started
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
require_relative "boukensha/tui"
```

Note what changed versus the pre-Task-3 file: `working_dir`-driven `Tools::FileSystem`/`Tools::Shell` registration is gone; `allowed_commands:`/`shell_timeout:`/`mud:` keywords and `mud_opts_from_config` are gone; `mcp_servers:` keyword and `start_mcp_servers` are new; both `ensure` blocks now stop `clients`; `Repl.new` now passes `mcp_server_names:` instead of `mud:`; the tail `require_relative` block drops `tools/file_system` and `tools/shell` and swaps `tools/mud` for `mcp/client` + `tools/mcp` (keeping `tui` last, unchanged).

- [ ] **Step 4: Rewrite `lib/boukensha/repl.rb`**

Replace the entire file with:

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
  #   /clear   wipe conversation history (tools stay registered)
  #   /exit    leave the REPL
  #   /quit    alias for /exit
  class Repl
    PROMPT = "boukensha> "

    HELP = <<~HELP
      Commands:
        /clear   wipe conversation history (tools stay)
        /exit    leave the REPL
        /help    show this message
    HELP

    attr_reader :logger, :context, :model, :version

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
      @output_cb  = nil
    end

    # Register a callback that receives every string the REPL would otherwise
    # print to stdout.  When set, puts/print are suppressed entirely and all
    # output is routed through the callback instead.  Used by Tui.
    def on_output(&block)
      @output_cb = block
    end

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

          /clear           reset conversation history
          /exit or /quit    leave the REPL

      BANNER
    end

    # Handle a slash command.  Returns :quit, :command, or nil (not a command).
    # Output is routed through the registered on_output callback if present.
    def handle_command(input)
      case input
      when "/exit", "/quit"
        output("Goodbye.")
        :quit
      when "/help"
        output(HELP)
        :command
      when "/clear"
        @context.clear_messages!
        @turn = 0
        output("(conversation history cleared)")
        :command
      end
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

      output("")
      output(result)
    rescue LoopError => e
      output("\n[error] #{e.message}")
    rescue ApiError => e
      output("\n[error] API call failed: #{e.message}")
    end

    def start
      output(banner)
      loop do
        unless @output_cb
          print PROMPT
          $stdout.flush
        end

        input = $stdin.gets
        break unless input  # EOF / Ctrl-D

        input = input.chomp.strip
        next if input.empty?

        result = handle_command(input)
        break if result == :quit
        next  if result

        run_turn(input)
      end
    end

    private

    def output(str)
      if @output_cb
        @output_cb.call(str.to_s)
      else
        puts str
      end
    end
  end
end
```

This removes `mud_status_string`/`probe_mud` entirely (MUD, if used, now lives behind an MCP subprocess `Repl` has no direct visibility into beyond its server name) and mirrors `10_standard_tool_library`'s `mcp servers:` banner line. Everything TUI-related — `on_output`, `handle_command`, `attr_reader`s, the `start` loop shape — is untouched from what `11_tui` already had.

- [ ] **Step 5: Create `examples/mcp_wiring_test.rb`**

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

  # Regression test: a later server in the hash failing to start must not
  # leak the subprocess of an earlier server that started successfully.
  # start_mcp_servers raises without returning anything, so run/repl's own
  # `clients = start_mcp_servers(...)` never completes and their `ensure
  # clients&.each(&:stop)` becomes a no-op for this batch — start_mcp_servers
  # itself must stop everything it already started before re-raising.
  def test_a_failed_start_stops_every_previously_started_client
    assert_raises(Boukensha::Mcp::Client::Error) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "good" => { command: "ruby", args: [FIXTURE] },
        "bad"  => { command: "this-command-does-not-exist-12345" }
      })
    end

    # "good" started and registered its tools before "bad" failed. Its
    # registered tool's block is a closure over the actual Mcp::Client
    # instance start_mcp_servers created — dig it out via the block's
    # binding (no changes to Client's public API needed) to prove
    # start_mcp_servers called #stop on it before re-raising: #stop clears
    # @wait_thr, so a non-nil value here would mean the subprocess leaked.
    good_client = @context.tools["echo"].block.binding.local_variable_get(:client)

    assert_nil good_client.instance_variable_get(:@wait_thr),
      "expected the 'good' server's client to have been stopped (subprocess handle cleared) " \
      "before start_mcp_servers re-raised on 'bad' failing to start"
  end

  # Regression test: unlike the "earlier servers" case above, the CURRENT
  # server's own client can also leak — its subprocess spawns fine
  # (Mcp::Client#start succeeds), but Tools::Mcp.register then raises
  # ArgumentError on a tool-name collision before that client is ever
  # pushed onto start_mcp_servers' internal `started` array. Two entries
  # both pointing at the fixture with no prefix: guarantees a collision on
  # "echo" (the fixture's tools/list returns echo before boom, so the
  # collision is hit deterministically on the second entry's first tool).
  def test_current_client_stopped_on_a_tool_name_collision
    assert_raises(ArgumentError) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "first"  => { command: "ruby", args: [FIXTURE] },
        "second" => { command: "ruby", args: [FIXTURE] }
      })
    end

    # "second" never got any tool registered (it failed on the very first
    # one), so there's no registry closure to dig it out of like the "good"
    # client above. Its subprocess still spawned before the collision was
    # detected, though, so it exists as a live Mcp::Client object reachable
    # via ObjectSpace — ask it directly whether it was stopped.
    clients = ObjectSpace.each_object(Boukensha::Mcp::Client).select { |c| %w[first second].include?(c.name) }
    assert_equal 2, clients.size, "expected to find both the 'first' and 'second' clients via ObjectSpace"

    clients.each do |client|
      assert_nil client.instance_variable_get(:@wait_thr),
        "expected '#{client.name}' client's subprocess to have been stopped after the 'echo' name collision, " \
        "but its wait_thr handle is still set"
    end
  end

  # Regression test: a malformed mcp_servers: entry (missing `command:`)
  # raises KeyError from `opts.fetch(:command)` before Mcp::Client.new is
  # ever called for that entry — and must not leak the subprocess of an
  # earlier entry that started successfully. opts/required/client are
  # computed inside start_mcp_servers' own begin block specifically so a
  # crash this early still triggers the same cleanup path as a
  # Mcp::Client::Error raised later.
  def test_a_malformed_entry_stops_previously_started_clients_and_raises
    assert_raises(KeyError) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "good" => { command: "ruby", args: [FIXTURE] },
        "bad"  => {}
      })
    end

    # "good" started and registered its tools before "bad" failed to even
    # build a Mcp::Client. Dig its client out via the registered tool
    # block's binding (same technique as the "earlier servers" leak test
    # above) to prove start_mcp_servers stopped it before re-raising.
    good_client = @context.tools["echo"].block.binding.local_variable_get(:client)

    assert_nil good_client.instance_variable_get(:@wait_thr),
      "expected the 'good' server's client to have been stopped (subprocess handle cleared) " \
      "before start_mcp_servers re-raised on the malformed 'bad' entry"
  end
end
```

- [ ] **Step 6: Run both MCP test scripts and confirm everything is green**

```sh
cd week1_baseline/ruby/11_tui
ruby examples/mcp_client_test.rb
ruby examples/mcp_wiring_test.rb
```
Expected: all tests pass in both files.

- [ ] **Step 7: Confirm the deleted tool classes are actually gone and the app still loads**

```sh
ruby -Ilib -e 'require "boukensha"; puts defined?(Boukensha::Tools::FileSystem).inspect; puts defined?(Boukensha::Tools::Shell).inspect; puts defined?(Boukensha::Tools::Mud).inspect; puts defined?(Boukensha::Tools::Mcp).inspect'
```
Expected: `nil`, `nil`, `nil`, `"constant"`.

- [ ] **Step 8: Commit**

```sh
git add lib/boukensha.rb lib/boukensha/repl.rb lib/boukensha/config.rb examples/mcp_wiring_test.rb
git commit -m "boukensha: rewire run/repl onto mcp_servers:, delete built-in FileSystem/Shell/Mud tools"
```

---

### Task 4: Update `boukensha_loader.rb`'s legacy `MUD_NAME` shortcut

**Files:**
- Modify: `lib/boukensha_loader.rb`

**Interfaces:**
- Consumes: `Boukensha.repl(mcp_servers:, working_dir:, tui:)` (Task 3).

The `--no-tui` flag and `tui:` wiring `11_tui` already has must be preserved; only the `MUD_NAME` legacy-override block changes shape (from a `mud:` hash to a single `mcp_servers:` entry that spawns `mud-manager --mcp`).

- [ ] **Step 1: Rewrite `lib/boukensha_loader.rb`**

Replace the entire file with:

```ruby
# BoukenshaLoader resolves which step folder to load from, then boots the REPL.
#
# Resolution order:
#   1. BOUKENSHA_PATH environment variable (selects which *step* lib to load)
#   2. ~/.boukensharc  (a file containing a single path)
#   3. The lib/ directory bundled inside this gem (step 11 — the latest release)
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
#   echo ~/Sites/boukensha/11_tui > ~/.boukensharc && boukensha
#   boukensha --no-tui                                                     # plain REPL, no charm-ruby TUI
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

    # --no-tui falls back to the plain terminal REPL (no charm-ruby).
    no_tui = ARGV.delete("--no-tui")

    repl_opts = { tui: !no_tui }

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

- [ ] **Step 2: Smoke-test the option-building logic in isolation**

```sh
cd week1_baseline/ruby/11_tui
ruby -e '
ARGV.replace(["--no-tui"])
ENV["MUD_NAME"] = "dummy"
ENV["MUD_PASSWORD"] = "hunter2"
no_tui = ARGV.delete("--no-tui")
repl_opts = { tui: !no_tui }
if ENV["MUD_NAME"]
  repl_opts[:working_dir] = false
  repl_opts[:mcp_servers] = {
    "mud" => { command: "mud-manager", args: ["--mcp"],
               env: { "MUD_HOST" => ENV.fetch("MUD_HOST", "localhost"),
                      "MUD_PORT" => ENV.fetch("MUD_PORT", "4000"),
                      "MUD_USERNAME" => ENV.fetch("MUD_NAME"),
                      "MUD_PASSWORD" => ENV.fetch("MUD_PASSWORD") } }
  }
end
p repl_opts
'
```
Expected: `{:tui=>false, :working_dir=>false, :mcp_servers=>{"mud"=>{:command=>"mud-manager", :args=>["--mcp"], :env=>{"MUD_HOST"=>"localhost", "MUD_PORT"=>"4000", "MUD_USERNAME"=>"dummy", "MUD_PASSWORD"=>"hunter2"}}}}`

- [ ] **Step 3: Commit**

```sh
git add lib/boukensha_loader.rb
git commit -m "boukensha: legacy MUD_NAME override now builds an mcp_servers: entry"
```

---

### Task 5: Update `boukensha.gemspec` dependencies

**Files:**
- Modify: `boukensha.gemspec`

**Interfaces:** None (packaging metadata only).

`mud_manager` is no longer `require`d directly by `11_tui` (Task 3 deleted the only file that did) — MUD gameplay now runs as a separate `mud-manager --mcp` subprocess spawned by `Mcp::Client`, the same as in `10_standard_tool_library`. `charm` stays; it's the TUI dependency and is unrelated to this delta.

- [ ] **Step 1: Edit `boukensha.gemspec`**

Replace:

```ruby
  # MUD session management and CircleMUD command primitives.
  spec.add_dependency "mud_manager", "~> 0.1"

  # TUI powered by charm (bubbletea + lipgloss + bubbles bindings).
  spec.add_dependency "charm"

  # net/http and json are stdlib. Users supply their own ANTHROPIC_API_KEY.
```

with:

```ruby
  # TUI powered by charm (bubbletea + lipgloss + bubbles bindings).
  spec.add_dependency "charm"

  # net/http, json, and open3 are stdlib. Users supply their own ANTHROPIC_API_KEY.
  # MUD (and any other) tools come from MCP servers configured in settings.yaml —
  # see mcp_servers: in Boukensha::Config. No tool-specific gem dependency here.
```

- [ ] **Step 2: Verify the gemspec still parses and builds**

```sh
cd week1_baseline/ruby/11_tui
ruby -e 'spec = Gem::Specification.load("boukensha.gemspec"); puts spec.dependencies.map(&:name).inspect'
```
Expected: `["dotenv", "charm"]` (from `Gemfile`'s own `gem "dotenv"` plus the gemspec's `charm`) — note `dotenv` is a `Gemfile`-level dependency, not `add_dependency` in the gemspec; if this only prints `["charm"]`, that's also correct — the key check is that `mud_manager` is absent and no error is raised.

- [ ] **Step 3: Commit**

```sh
git add boukensha.gemspec
git commit -m "boukensha: drop mud_manager gemspec dependency (MUD is now an MCP subprocess)"
```

---

### Task 6: Update `examples/example.rb` and `README.md`

**Files:**
- Modify: `examples/example.rb`
- Modify: `README.md`

**Interfaces:** None (docs + demo script only).

- [ ] **Step 1: Fix the stale `working_dir:`/`mud:` comment in `examples/example.rb`**

Replace:

```ruby
#!/usr/bin/env ruby
# frozen_string_literal: true
#
# Step 10 — A Standard Tool Library (MUD demo)
#
# Demonstrates Boukensha::Tools::Mud, which registers gameplay tools against
# a live CircleMUD connection. Connection credentials come from
# ~/.boukensha/settings.yaml (mud: host/port/username/password) by default.
# Set BOUKENSHA_DIR to point at a different config directory.
#
# You can still override individual values as keyword arguments:
#
#   ruby examples/demo.rb
#   BOUKENSHA_DIR=iterations/.boukensha ruby examples/demo.rb

ENV["BOUKENSHA_DIR"] ||= File.expand_path("../../../.boukensha", __dir__)

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"

cfg = Boukensha.config
puts "Config: #{cfg}"
puts "API key set? #{!ENV['ANTHROPIC_API_KEY'].nil?}"
puts

Boukensha.run(
  task: "Connect to the MUD, look at your surroundings, check your score, " \
        "then look at the available exits and tell me what you see.",
  # system/model/api_key all come from config automatically
  working_dir: false   # no filesystem tools needed for MUD play
  # mud: comes from config (settings.yaml mud: block) automatically
)
```

with:

```ruby
#!/usr/bin/env ruby
# frozen_string_literal: true
#
# Step 11 — A Terminal UI (MUD demo, carried over unchanged from step 10)
#
# Demonstrates the mud-manager MCP server, spawned via mcp_servers: and
# exposing gameplay tools against a live CircleMUD connection. Connection
# details (mud-manager's command/args/env) come from
# ~/.boukensha/settings.yaml (mcp_servers: block) by default.
# Set BOUKENSHA_DIR to point at a different config directory.
#
# You can still override individual values as keyword arguments:
#
#   ruby examples/example.rb
#   BOUKENSHA_DIR=iterations/.boukensha ruby examples/example.rb

ENV["BOUKENSHA_DIR"] ||= File.expand_path("../../../.boukensha", __dir__)

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"

cfg = Boukensha.config
puts "Config: #{cfg}"
puts "API key set? #{!ENV['ANTHROPIC_API_KEY'].nil?}"
puts

Boukensha.run(
  task: "Connect to the MUD, look at your surroundings, check your score, " \
        "then look at the available exits and tell me what you see.",
  # system/model/api_key all come from config automatically
  working_dir: false   # Context metadata only; no MCP filesystem server needed for MUD play
  # mcp_servers: comes from config (settings.yaml mcp_servers: block) automatically
)
```

- [ ] **Step 2: Rewrite `README.md`**

Replace the entire file with:

```markdown
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

# launches the charm TUI:
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
```

(This intentionally drops step 10's "Technical observations" section — those notes documented step 10's own investigation and a pre-existing off-by-one bug in `Config::PROMPTS_DIR`. `11_tui`'s `Config::PROMPTS_DIR` uses the same `../../../prompts` relative expansion, so if that bug is still present it's already pre-existing here too, not something this port introduces — worth a quick check in Task 7 but not a rewrite of step 10's investigation log.)

- [ ] **Step 3: Commit**

```sh
git add examples/example.rb README.md
git commit -m "docs: describe the MCP-host + TUI architecture together in step 11's README"
```

---

### Task 7: Full verification pass

**Files:** none (verification only).

- [ ] **Step 1: Run every example/test script**

```sh
cd week1_baseline/ruby/11_tui
ruby examples/mcp_client_test.rb
ruby examples/mcp_wiring_test.rb
```
Expected: all green.

- [ ] **Step 2: Confirm `Config::PROMPTS_DIR` resolves correctly (carried-over risk noted in Task 6)**

```sh
ruby -Ilib -e 'require "boukensha"; puts Boukensha::Config::PROMPTS_DIR; puts Dir.exist?(Boukensha::Config::PROMPTS_DIR)'
```
Expected: a path ending in `.../11_tui/prompts` and `true`. If it prints `false` or a path one level too high (e.g. resolving to `ruby/prompts` instead of `ruby/11_tui/prompts`), that is the same pre-existing bug step 10's README flagged in `Config::PROMPTS_DIR` (`lib/boukensha/config.rb`) — note it but treat it as out of scope for this plan (it predates this delta and isn't introduced by it); file it separately rather than fixing it inline here.

- [ ] **Step 3: Build and install the gem**

```sh
gem uninstall boukensha --all --executables 2>/dev/null || true
gem build boukensha.gemspec
gem install boukensha-0.11.0.gem
```
Expected: builds and installs without error, with no `mud_manager` dependency resolution step.

- [ ] **Step 4: Boot the plain REPL against an echo MCP fixture and confirm the banner shows it**

```sh
mkdir -p /tmp/boukensha_verify_dir
cat > /tmp/boukensha_verify_settings.yaml <<'YAML'
tasks:
  player:
    provider: anthropic
    model: claude-haiku-4-5
mcp_servers:
  echo:
    command: ruby
    args:
      - REPLACE_WITH_ABSOLUTE_PATH/examples/fixtures/echo_mcp_server.rb
YAML
cp /tmp/boukensha_verify_settings.yaml /tmp/boukensha_verify_dir/settings.yaml
BOUKENSHA_DIR=/tmp/boukensha_verify_dir BOUKENSHA_PATH="$(pwd)" boukensha --no-tui <<< $'/exit\n'
```
(Replace `REPLACE_WITH_ABSOLUTE_PATH` with the absolute path to this step's `examples/` directory before running — the YAML heredoc can't self-reference `$(pwd)`.)

Expected: the printed banner's `mcp servers:` line reads `echo` (not `(none configured)`), confirming `Boukensha.repl` → `start_mcp_servers` → `Repl#banner` end-to-end wiring works without a live MUD or LLM API key.

- [ ] **Step 5: Confirm the TUI still launches (manual, interactive check)**

```sh
BOUKENSHA_DIR=~/.boukensha BOUKENSHA_PATH="$(pwd)" boukensha
```
Expected: the charm TUI's four-zone layout renders; type `/exit` or press `Ctrl+C` to quit. This step is manual/interactive — record the observed result in the commit message or a follow-up note rather than trying to automate it.

- [ ] **Step 6: Clean up temp files and stray installed gem versions used for verification**

```sh
rm -rf /tmp/boukensha_verify_dir /tmp/boukensha_verify_settings.yaml
```
