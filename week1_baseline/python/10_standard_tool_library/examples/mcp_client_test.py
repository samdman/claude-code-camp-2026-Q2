import sys
import threading
import unittest
from pathlib import Path

from boukensha.mcp import Client

FIXTURE = str(Path(__file__).resolve().parent / "fixtures" / "echo_mcp_server.py")


class McpClientTest(unittest.TestCase):
    def setUp(self):
        self.client = Client(name="echo", command=sys.executable, args=[FIXTURE])
        self.client.start()

    def tearDown(self):
        self.client.stop()

    def test_tools_list_returns_the_fixture_tools(self):
        names = sorted(t["name"] for t in self.client.tools_list())
        self.assertEqual(names, ["boom", "echo"])

    def test_tools_call_returns_text_content(self):
        result = self.client.tools_call("echo", {"message": "hi"})
        self.assertEqual(result, "you said: hi")

    def test_tools_call_error_raises(self):
        with self.assertRaisesRegex(Client.Error, "boom: intentional failure"):
            self.client.tools_call("boom", {})


# Regression test for finding 2: a server that writes one non-JSON line to
# stdout before any real protocol traffic (a stray warning, a shell wrapper
# banner) must not crash the client with an unhandled JSONDecodeError —
# _read_response should skip unparseable lines and keep reading until the
# real response arrives.
class McpClientNoisyPrefixTest(unittest.TestCase):
    def setUp(self):
        self.client = Client(name="noisy", command=sys.executable, args=[FIXTURE], env={"NOISY_PREFIX": "1"})

    def tearDown(self):
        self.client.stop()

    def test_start_survives_a_leading_non_json_line_on_stdout(self):
        # The fixture prints its garbage banner line before it ever reads a
        # request, so it's already sitting ahead of the initialize response
        # by the time start() reads for it — this exercises the fix directly
        # in the handshake itself, not just a later call.
        self.client.start()
        names = sorted(t["name"] for t in self.client.tools_list())
        self.assertEqual(names, ["boom", "echo"])


# Regression test for finding 3: the child's stderr must be drained
# continuously in the background, not just on-demand — otherwise a server
# that writes enough stderr output to fill the OS pipe buffer during normal
# operation blocks on that write, and the client (blocked in a timeout-less
# readline()) hangs forever waiting for a response that will never come.
# 200KB is comfortably larger than a typical OS pipe buffer (commonly ~64KB
# on Linux, and this repo runs on Windows too), so this reliably reproduces
# the deadlock without the fix.
class McpClientStderrFloodTest(unittest.TestCase):
    def setUp(self):
        self.client = Client(name="flood", command=sys.executable, args=[FIXTURE], env={"STDERR_FLOOD_KB": "200"})
        self.client.start()

    def tearDown(self):
        self.client.stop()

    def test_tools_call_does_not_hang_when_the_server_floods_stderr(self):
        outcome = {}

        def call():
            outcome["result"] = self.client.tools_call("echo", {"message": "hi"})

        thread = threading.Thread(target=call, daemon=True)
        thread.start()
        thread.join(timeout=10)
        self.assertFalse(
            thread.is_alive(),
            "tools_call hung — the child likely blocked writing to a full stderr pipe "
            "while the client was blocked reading stdout (the deadlock this fixture reproduces)",
        )
        self.assertEqual(outcome.get("result"), "you said: hi")


class ToolsMcpRegistrationTest(unittest.TestCase):
    def setUp(self):
        from boukensha.context import Context
        from boukensha.registry import Registry
        from boukensha.tasks.player import Player
        from boukensha.tools import Mcp

        self.Mcp = Mcp
        self.client = Client(name="echo", command=sys.executable, args=[FIXTURE])
        self.client.start()
        self.context = Context(task=Player, working_dir=None)
        self.registry = Registry(self.context)

    def tearDown(self):
        self.client.stop()

    def test_registers_every_fixture_tool(self):
        self.Mcp.register(self.registry, client=self.client)
        self.assertEqual(sorted(self.context.tools.keys()), ["boom", "echo"])

    def test_prefixes_tool_names_when_given(self):
        self.Mcp.register(self.registry, client=self.client, prefix="mud")
        self.assertEqual(sorted(self.context.tools.keys()), ["mud_boom", "mud_echo"])

    def test_dispatch_calls_through_to_the_mcp_server(self):
        self.Mcp.register(self.registry, client=self.client)
        result = self.registry.dispatch("echo", {"message": "hello"})
        self.assertEqual(result, "you said: hello")

    def test_raises_on_tool_name_collision(self):
        self.Mcp.register(self.registry, client=self.client)
        with self.assertRaisesRegex(ValueError, "collision"):
            self.Mcp.register(self.registry, client=self.client)


if __name__ == "__main__":
    unittest.main()
