# frozen_string_literal: true

require "minitest/autorun"

$LOAD_PATH.unshift File.expand_path("../lib", __dir__)
require "boukensha"

# Verifies the Boukensha.run/.repl <-> mcp_servers: wiring itself (the
# private Boukensha.start_mcp_servers), using the same standalone fixture
# server as mcp_client_test.rb — no live MUD or LLM API key required.
#
# Run:
#   ruby examples/mcp_wiring_test.rb
class McpWiringTest < Minitest::Test
  FIXTURE = File.expand_path("fixtures/echo_mcp_server.rb", __dir__)

  def setup
    @context  = Boukensha::Context.new(task: Boukensha::Tasks::Player, working_dir: nil)
    @registry = Boukensha::Registry.new(@context)
  end

  def test_starts_and_registers_every_configured_server
    clients = Boukensha.send(:start_mcp_servers, @registry, {
      "echo" => { command: "ruby", args: [FIXTURE] }
    })

    assert_equal 1, clients.size
    assert_equal %w[boom echo].sort, @context.tools.keys.sort
  ensure
    clients&.each(&:stop)
  end

  def test_applies_prefix_from_config
    clients = Boukensha.send(:start_mcp_servers, @registry, {
      "echo" => { command: "ruby", args: [FIXTURE], prefix: "mud" }
    })

    assert_equal %w[mud_boom mud_echo].sort, @context.tools.keys.sort
  ensure
    clients&.each(&:stop)
  end

  def test_required_false_downgrades_a_failed_start_to_a_warning
    clients = nil
    _out, err = capture_io do
      clients = Boukensha.send(:start_mcp_servers, @registry, {
        "missing" => { command: "this-command-does-not-exist-12345", required: false }
      })
    end

    assert_empty clients
    assert_match(/missing.*failed to start/, err)
  end

  def test_required_true_raises_on_a_failed_start
    assert_raises(Boukensha::Mcp::Client::Error) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "missing" => { command: "this-command-does-not-exist-12345" }
      })
    end
  end

  def test_empty_servers_hash_returns_no_clients_and_registers_nothing
    clients = Boukensha.send(:start_mcp_servers, @registry, {})

    assert_empty clients
    assert_empty @context.tools
  end
end
