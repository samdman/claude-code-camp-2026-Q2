# frozen_string_literal: true

require "minitest/autorun"

require_relative "../lib/mud_manager"

# Verifies MudManager::McpTools wires Primitives + Session correctly, using a
# fake in-memory Session double instead of a live TCP connection.
#
# Run:
#   ruby mud_manager/examples/mcp_tools_test.rb
class FakeSession
  attr_reader :host, :port, :sent

  def initialize
    @host       = "localhost"
    @port       = 4000
    @open       = false
    @sent       = []
    @next_reply = "ok"
  end

  def open?  = @open
  def open   = @open = true
  def close  = @open = false
  def drain  = ""

  def login(_username, _password)
    @open = true
    "Welcome back."
  end

  def send_command(command)
    line = command.respond_to?(:raw) ? command.raw : command.to_s
    @sent << line
    line
  end

  def read_until_prompt(*) = @next_reply
  def read_until_quiet(*)  = @next_reply

  def next_reply=(text)
    @next_reply = text
  end
end

class McpToolsTest < Minitest::Test
  def setup
    @session = FakeSession.new
    @tools   = MudManager::McpTools.build(@session, name: "Gandalf", password: "secret")
  end

  def test_builds_every_tool_from_the_gameplay_surface
    expected = %w[
      mud_connect mud_disconnect mud_status look examine check move flee
      set_position track attack skill_strike consider say tell channel_say
      get_item drop_item put_item equip_item consume_item cast_spell
      use_magic_item shop practice save_character send_raw
    ]

    assert_equal expected.sort, @tools.keys.sort
  end

  def test_mud_connect_opens_and_logs_in
    result = @tools["mud_connect"][:handler].call({})

    assert_match(/connected to localhost:4000/, result)
    assert @session.open?
  end

  def test_gameplay_tool_guards_when_not_connected
    result = @tools["look"][:handler].call({})

    assert_match(/not connected/, result)
  end

  def test_look_sends_the_primitive_command_once_connected
    @session.open
    @session.next_reply = "You are in a room.\n> "

    result = @tools["look"][:handler].call({ "target" => "sword", "preposition" => "at" })

    assert_equal ["look at sword"], @session.sent
    assert_equal "You are in a room.\n> ", result
  end

  def test_move_rejects_invalid_direction
    @session.open

    result = @tools["move"][:handler].call({ "direction" => "sideways" })

    assert_match(/invalid direction/, result)
  end

  def test_attack_defaults_style_to_kill
    @session.open
    @session.next_reply = "You attack!"

    @tools["attack"][:handler].call({ "target" => "goblin" })

    assert_equal ["kill goblin"], @session.sent
  end
end
