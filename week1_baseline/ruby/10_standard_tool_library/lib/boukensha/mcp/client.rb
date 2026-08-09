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
