#!/usr/bin/env ruby
# frozen_string_literal: true
#
# Step 11 — A Terminal UI (MUD demo, carried over unchanged from step 10)
#
# Demonstrates the mud-manager MCP server, spawned via mcp_servers: and
# exposing gameplay tools against a live CircleMUD connection. Connection
# details (mud-manager's command/args/env) come from
# ~/.boukensha/settings.yaml (mcp_servers: block) by default.
# Set BOUKENSHA_DIR to point at a different config directory.
#
# You can still override individual values as keyword arguments:
#
#   ruby examples/example.rb
#   BOUKENSHA_DIR=iterations/.boukensha ruby examples/example.rb

ENV["BOUKENSHA_DIR"] ||= File.expand_path("../../../.boukensha", __dir__)

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"

cfg = Boukensha.config
puts "Config: #{cfg}"
puts "API key set? #{!ENV['ANTHROPIC_API_KEY'].nil?}"
puts

Boukensha.run(
  task: "Connect to the MUD, look at your surroundings, check your score, " \
        "then look at the available exits and tell me what you see.",
  # system/model/api_key all come from config automatically
  working_dir: false   # Context metadata only; no MCP filesystem server needed for MUD play
  # mcp_servers: comes from config (settings.yaml mcp_servers: block) automatically
)
