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
