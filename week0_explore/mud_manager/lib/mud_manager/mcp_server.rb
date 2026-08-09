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
