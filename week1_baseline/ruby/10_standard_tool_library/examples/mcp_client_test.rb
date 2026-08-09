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
