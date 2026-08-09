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
