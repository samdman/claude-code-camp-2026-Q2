from __future__ import annotations


class Mcp:
    @staticmethod
    def register(registry, *, client, prefix: str | None = None) -> None:
        for tool in client.tools_list():
            raw_name = tool["name"]
            tool_name = f"{prefix}_{raw_name}" if prefix else raw_name

            if registry.registered(tool_name):
                raise ValueError(
                    f"tool name collision: '{tool_name}' is already registered "
                    f"(from MCP server '{client.name}') — pick a different prefix:"
                )

            schema = tool.get("inputSchema") or {}
            properties = schema.get("properties") or {}
            parameters = {
                param_name: {"type": param_schema.get("type"), "description": param_schema.get("description")}
                for param_name, param_schema in properties.items()
            }

            def block(_raw_name=raw_name, **args):
                return client.tools_call(_raw_name, {str(k): v for k, v in args.items()})

            registry.tool(
                tool_name,
                description=str(tool.get("description") or ""),
                parameters=parameters,
                block=block,
            )
