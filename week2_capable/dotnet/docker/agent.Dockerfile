# Build context: repo root (needs both week2_capable/dotnet and week0_explore/mud_manager)
#
# Bundles the .NET agent (Boukensha.Console) together with Ruby + mud_manager in one
# image. This is required, not a convenience: McpClient spawns the MCP server as a
# local child process over stdio (see Boukensha.Core/Mcp/McpClient.cs), so the agent
# and the MUD MCP server must share a container -- splitting them would need a
# network-transport rewrite of the MCP client, which is out of scope here.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY week2_capable/dotnet/Boukensha.slnx ./week2_capable/dotnet/
COPY week2_capable/dotnet/src ./week2_capable/dotnet/src
COPY week2_capable/dotnet/tests ./week2_capable/dotnet/tests
WORKDIR /src/week2_capable/dotnet
RUN dotnet publish src/Boukensha.Console/Boukensha.Console.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ruby \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./
COPY week0_explore/mud_manager/lib /app/mud_manager/lib
COPY week0_explore/mud_manager/bin /app/mud_manager/bin
COPY week2_capable/dotnet/docker/settings.template.yaml /app/settings.template.yaml
COPY week2_capable/dotnet/docker/agent-entrypoint.sh /app/agent-entrypoint.sh

# mud_manager is checked out with CRLF line endings on Windows dev machines (no
# .gitattributes pins it to LF); a CRLF shebang breaks `ruby` on Linux, so normalize here.
RUN find /app/mud_manager -type f \( -name '*.rb' -o -name 'mud-manager' \) | xargs sed -i 's/\r$//' \
    && chmod +x /app/agent-entrypoint.sh /app/mud_manager/bin/mud-manager

ENTRYPOINT ["/app/agent-entrypoint.sh"]
