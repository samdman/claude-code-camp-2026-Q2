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

  # Regression test: unlike the "earlier servers" case above, the CURRENT
  # server's own client can also leak — its subprocess spawns fine
  # (Mcp::Client#start succeeds), but Tools::Mcp.register then raises
  # ArgumentError on a tool-name collision before that client is ever
  # pushed onto start_mcp_servers' internal `started` array. Two entries
  # both pointing at the fixture with no prefix: guarantees a collision on
  # "echo" (the fixture's tools/list returns echo before boom, so the
  # collision is hit deterministically on the second entry's first tool).
  def test_current_client_stopped_on_a_tool_name_collision
    assert_raises(ArgumentError) do
      Boukensha.send(:start_mcp_servers, @registry, {
        "first"  => { command: "ruby", args: [FIXTURE] },
        "second" => { command: "ruby", args: [FIXTURE] }
      })
    end

    # "second" never got any tool registered (it failed on the very first
    # one), so there's no registry closure to dig it out of like the "good"
    # client above. Its subprocess still spawned before the collision was
    # detected, though, so it exists as a live Mcp::Client object reachable
    # via ObjectSpace — ask it directly whether it was stopped.
    clients = ObjectSpace.each_object(Boukensha::Mcp::Client).select { |c| %w[first second].include?(c.name) }
    assert_equal 2, clients.size, "expected to find both the 'first' and 'second' clients via ObjectSpace"

    clients.each do |client|
      assert_nil client.instance_variable_get(:@wait_thr),
        "expected '#{client.name}' client's subprocess to have been stopped after the 'echo' name collision, " \
        "but its wait_thr handle is still set"
    end
  end
end
