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

  # Regression test: a later server in the hash failing to start must not
  # leak the subprocess of an earlier server that started successfully.
  # start_mcp_servers raises without returning anything, so run/repl's own
  # `clients = start_mcp_servers(...)` never completes and their `ensure
  # clients&.each(&:stop)` becomes a no-op for this batch — start_mcp_servers
  # itself must stop everything it already started before re-raising.
  def test_a_failed_start_stops_every_previously_started_client
    assert_raises(Boukensha::Mcp::Client::Error) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "good" => { command: "ruby", args: [FIXTURE] },
        "bad"  => { command: "this-command-does-not-exist-12345" }
      })
    end

    # "good" started and registered its tools before "bad" failed. Its
    # registered tool's block is a closure over the actual Mcp::Client
    # instance start_mcp_servers created — dig it out via the block's
    # binding (no changes to Client's public API needed) to prove
    # start_mcp_servers called #stop on it before re-raising: #stop clears
    # @wait_thr, so a non-nil value here would mean the subprocess leaked.
    good_client = @context.tools["echo"].block.binding.local_variable_get(:client)

    assert_nil good_client.instance_variable_get(:@wait_thr),
      "expected the 'good' server's client to have been stopped (subprocess handle cleared) " \
      "before start_mcp_servers re-raised on 'bad' failing to start"
  end
end
