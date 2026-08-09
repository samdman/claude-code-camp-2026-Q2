import io
import sys
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest.mock import patch

from boukensha import _start_mcp_servers
from boukensha.context import Context
from boukensha.mcp import Client
from boukensha.registry import Registry
from boukensha.tasks.player import Player

FIXTURE = str(Path(__file__).resolve().parent / "fixtures" / "echo_mcp_server.py")


def _closure_var(func, name):
    # CPython-specific equivalent of Ruby's Proc#binding.local_variable_get —
    # digs a free variable out of a closure via its cell contents.
    names = func.__code__.co_freevars
    return func.__closure__[names.index(name)].cell_contents


class McpWiringTest(unittest.TestCase):
    def setUp(self):
        self.context = Context(task=Player, working_dir=None)
        self.registry = Registry(self.context)

    def test_starts_and_registers_every_configured_server(self):
        clients = _start_mcp_servers(self.registry, {"echo": {"command": sys.executable, "args": [FIXTURE]}})
        try:
            self.assertEqual(len(clients), 1)
            self.assertEqual(sorted(self.context.tools.keys()), ["boom", "echo"])
        finally:
            for c in clients:
                c.stop()

    def test_applies_prefix_from_config(self):
        clients = _start_mcp_servers(
            self.registry, {"echo": {"command": sys.executable, "args": [FIXTURE], "prefix": "mud"}}
        )
        try:
            self.assertEqual(sorted(self.context.tools.keys()), ["mud_boom", "mud_echo"])
        finally:
            for c in clients:
                c.stop()

    def test_required_false_downgrades_a_failed_start_to_a_warning(self):
        err = io.StringIO()
        with redirect_stderr(err):
            clients = _start_mcp_servers(
                self.registry, {"missing": {"command": "this-command-does-not-exist-12345", "required": False}}
            )
        self.assertEqual(clients, [])
        self.assertRegex(err.getvalue(), r"missing.*failed to start")

    def test_required_true_raises_on_a_failed_start(self):
        with self.assertRaises(Client.Error):
            _start_mcp_servers(self.registry, {"missing": {"command": "this-command-does-not-exist-12345"}})

    def test_empty_servers_dict_returns_no_clients_and_registers_nothing(self):
        clients = _start_mcp_servers(self.registry, {})
        self.assertEqual(clients, [])
        self.assertEqual(self.context.tools, {})

    # Regression test: a later server in the dict failing to start must not
    # leak the subprocess of an earlier server that started successfully.
    # _start_mcp_servers raises without returning anything, so the caller's
    # own `clients = _start_mcp_servers(...)` never completes and its own
    # `finally: for c in clients: c.stop()` becomes unreachable for this
    # batch — _start_mcp_servers itself must stop everything it already
    # started before re-raising.
    def test_a_failed_start_stops_every_previously_started_client(self):
        with self.assertRaises(Client.Error):
            _start_mcp_servers(
                self.registry,
                {
                    "good": {"command": sys.executable, "args": [FIXTURE]},
                    "bad": {"command": "this-command-does-not-exist-12345"},
                },
            )

        good_client = _closure_var(self.context.tools["echo"].block, "client")
        self.assertIsNone(
            good_client._process,
            "expected the 'good' server's client to have been stopped before "
            "_start_mcp_servers re-raised on 'bad' failing to start",
        )

    # Regression test: unlike the "earlier servers" case above, the CURRENT
    # server's own client can also leak — its subprocess spawns fine, but
    # Mcp.register then raises ValueError on a tool-name collision before
    # that client is ever appended to _start_mcp_servers' internal `started`
    # list. Two entries both pointing at the fixture with no prefix
    # guarantee a collision on "echo" (the fixture's tools/list returns echo
    # before boom, so the collision is hit deterministically on the second
    # entry's first tool).
    #
    # Ruby's own equivalent test digs both clients out via
    # ObjectSpace.each_object after the fact, relying on Ruby's GC not
    # having collected them yet. That doesn't port directly: CPython's
    # refcounting GC reclaims an object the instant its refcount hits zero,
    # and unittest.assertRaises explicitly clears the caught exception's
    # __traceback__ on exit (to avoid a reference cycle) — so by the time
    # gc.get_objects() would run here, "second"'s Client (never stored
    # anywhere else, since it never got a tool registered) is already gone.
    # A patched, instance-tracking __init__ is the reliable Python
    # substitute — it captures both instances at construction time instead
    # of trying to find them still alive afterward.
    def test_current_client_stopped_on_a_tool_name_collision(self):
        created = []
        original_init = Client.__init__

        def tracking_init(self, *args, **kwargs):
            original_init(self, *args, **kwargs)
            created.append(self)

        with patch.object(Client, "__init__", tracking_init):
            with self.assertRaises(ValueError):
                _start_mcp_servers(
                    self.registry,
                    {
                        "first": {"command": sys.executable, "args": [FIXTURE]},
                        "second": {"command": sys.executable, "args": [FIXTURE]},
                    },
                )

        clients = [c for c in created if c.name in ("first", "second")]
        self.assertEqual(len(clients), 2, "expected to find both the 'first' and 'second' clients")
        for client in clients:
            self.assertIsNone(
                client._process,
                f"expected '{client.name}' client's subprocess to have been stopped after the 'echo' name collision",
            )

    # Regression test: a malformed mcp_servers entry (missing "command")
    # raises KeyError from raw_opts["command"] before McpClient() is ever
    # constructed for that entry — and must not leak the subprocess of an
    # earlier entry that started successfully.
    def test_a_malformed_entry_stops_previously_started_clients_and_raises(self):
        with self.assertRaises(KeyError):
            _start_mcp_servers(
                self.registry,
                {"good": {"command": sys.executable, "args": [FIXTURE]}, "bad": {}},
            )

        good_client = _closure_var(self.context.tools["echo"].block, "client")
        self.assertIsNone(
            good_client._process,
            "expected the 'good' server's client to have been stopped before "
            "_start_mcp_servers re-raised on the malformed 'bad' entry",
        )


if __name__ == "__main__":
    unittest.main()
