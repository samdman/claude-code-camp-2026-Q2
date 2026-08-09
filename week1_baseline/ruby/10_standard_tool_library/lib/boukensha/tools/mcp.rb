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
