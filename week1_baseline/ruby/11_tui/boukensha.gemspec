require_relative "lib/boukensha/version"

Gem::Specification.new do |spec|
  spec.name        = "boukensha"
  spec.version     = Boukensha::VERSION
  spec.summary     = "BOUKENSHA — a tiny teaching framework for coding harnesses"
  spec.description = "Step-by-step coding harness framework. " \
                     "Set BOUKENSHA_PATH to load a specific lesson step, " \
                     "or run with defaults to use the bundled release."
  spec.authors     = ["Andrew Brown"]
  spec.email       = ["andrew@exampro.co"]
  spec.license     = "MIT"

  spec.required_ruby_version = ">= 3.0"

  # All files tracked in git, plus the bin/ executable.
  spec.files = Dir["lib/**/*.rb"] + ["bin/boukensha"]

  spec.bindir      = "bin"
  spec.executables = ["boukensha"]

  # TUI powered by charm (bubbletea + lipgloss + bubbles bindings).
  spec.add_dependency "charm"

  # net/http, json, and open3 are stdlib. Users supply their own ANTHROPIC_API_KEY.
  # MUD (and any other) tools come from MCP servers configured in settings.yaml —
  # see mcp_servers: in Boukensha::Config. No tool-specific gem dependency here.
end
