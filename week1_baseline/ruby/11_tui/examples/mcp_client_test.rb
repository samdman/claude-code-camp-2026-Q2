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
