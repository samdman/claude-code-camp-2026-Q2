# frozen_string_literal: true

require "minitest/autorun"

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"
require_relative "../lib/boukensha/mcp/client"

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
