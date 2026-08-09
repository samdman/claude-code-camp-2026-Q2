require_relative "lib/mud_manager/version"

Gem::Specification.new do |spec|
  spec.name        = "mud_manager"
  spec.version     = MudManager::VERSION
  spec.summary     = "MudManager — CircleMUD session management, command primitives, and an MCP server"
  spec.description = "Provides MudManager::Session (a long-lived telnet connection with " \
                     "background buffering and IAC stripping), MudManager::Primitives " \
                     "(a stateless library of typed CircleMUD command builders), and an " \
                     "MCP server (`mud-manager --mcp`) that exposes MUD gameplay as MCP tools " \
                     "over stdio so any MCP-capable client can reach the MUD."
  spec.authors     = ["Andrew Brown"]
  spec.email       = ["andrew@exampro.co"]
  spec.license     = "MIT"

  spec.required_ruby_version = ">= 3.0"

  spec.files = Dir["lib/**/*.rb"] + ["bin/mud-manager"]

  spec.bindir      = "bin"
  spec.executables = ["mud-manager"]

  # socket, thread, and json are stdlib — no external dependencies.
end
