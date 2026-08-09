from __future__ import annotations

import os
import sys

from .agent import Agent
from .errors import ApiError, LoopError


class Repl:
    PROMPT = "boukensha> "

    HELP = (
        "Commands:\n"
        "  /clear    wipe conversation history (tools stay)\n"
        "  /compact  drop oldest 40% of messages to free context\n"
        "  /exit     leave the REPL\n"
        "  /help     show this message\n"
    )

    def __init__(
        self,
        *,
        context,
        registry,
        builder,
        client,
        logger,
        config_dir: str | None = None,
        provider: str | None = None,
        model: str | None = None,
        version: str | None = None,
        api_key: str | None = None,
        mcp_server_names: list | None = None,
        task_settings: dict | None = None,
        max_iterations: int | None = None,
        max_turn_tokens: int | None = None,
        max_output_tokens: int | None = None,
    ) -> None:
        self._context = context
        self._registry = registry
        self._builder = builder
        self._client = client
        self._logger = logger
        self._task_settings = task_settings
        self._max_iterations = max_iterations
        self._max_turn_tokens = max_turn_tokens
        self._max_output_tokens = max_output_tokens
        self._config_dir = config_dir
        self._provider = provider
        self._model = model
        self._version = version
        self._api_key = api_key
        self._mcp_server_names = mcp_server_names if mcp_server_names is not None else []
        self._turn = 0
        self._output_cb = None

    @property
    def logger(self):
        return self._logger

    @property
    def context(self):
        return self._context

    @property
    def model(self):
        return self._model

    @property
    def version(self):
        return self._version

    def on_output(self, callback) -> None:
        """Register a callback that receives every string this Repl would
        otherwise print to stdout. When set, the PROMPT is not printed either
        (a Tui-style front end draws its own input box) and all output is
        routed through the callback instead."""
        self._output_cb = callback

    def banner(self) -> str:
        key_status = "✗ API key not set" if not self._api_key or not self._api_key.strip() else "✓ API key set"
        provider_line = f"{self._provider or 'default'} ({self._model or 'default'})  {key_status}"
        config_exists = self._config_dir and os.path.isdir(self._config_dir)
        config_line = (
            self._config_dir if config_exists else f"{self._config_dir or '(default)'}  ✗ directory not found"
        )
        ver = self._version or "?.?.?"
        mcp_line = ", ".join(self._mcp_server_names) if self._mcp_server_names else "(none configured)"

        return (
            "\n"
            "╔══════════════════════════════════════╗\n"
            f"║  BOUKENSHA MUD Assistant (v{ver}){' ' * (9 - len(ver))}║\n"
            "╚══════════════════════════════════════╝\n"
            f"  config:      {config_line}\n"
            f"  provider:    {provider_line}\n"
            f"  mcp servers: {mcp_line}\n"
            "\n"
            "  /clear           reset conversation history\n"
            "  /compact         free context (drop oldest messages)\n"
            "  /exit or /quit    leave the REPL\n"
            "\n"
        )

    def handle_command(self, input_line: str) -> str | None:
        """Handle a slash command. Returns "quit", "command", or None (not a
        command). Output is routed through the registered on_output callback
        if present."""
        if input_line in ("/exit", "/quit"):
            self._output("Goodbye.")
            return "quit"
        elif input_line == "/help":
            self._output(self.HELP)
            return "command"
        elif input_line == "/clear":
            self._context.clear_messages()
            self._turn = 0
            self._output("(conversation history cleared)")
            return "command"
        elif input_line == "/compact":
            dropped = self._context.compact_messages()
            self._output(f"(compacted context — {dropped} messages dropped)")
            return "command"
        return None

    def run_turn(self, input_line: str) -> None:
        self._turn += 1
        self._logger.turn(n=self._turn)

        self._context.add_message("user", input_line)

        agent = Agent(
            context=self._context,
            registry=self._registry,
            builder=self._builder,
            client=self._client,
            logger=self._logger,
            task_settings=self._task_settings,
            max_iterations=self._max_iterations,
            max_turn_tokens=self._max_turn_tokens,
            max_output_tokens=self._max_output_tokens,
        )
        try:
            result = agent.run()
        except LoopError as e:
            self._output(f"\n[error] {e}")
            return
        except ApiError as e:
            self._output(f"\n[error] API call failed: {e}")
            return

        self._output("")
        self._output(result)

    def start(self) -> None:
        self._output(self.banner())
        while True:
            if not self._output_cb:
                print(self.PROMPT, end="")
                sys.stdout.flush()

            raw_line = sys.stdin.readline()
            if not raw_line:  # EOF / Ctrl-D
                break

            input_line = raw_line.strip()
            if not input_line:
                continue

            result = self.handle_command(input_line)
            if result == "quit":
                break
            if result:
                continue

            self.run_turn(input_line)

    def _output(self, s: str) -> None:
        s = str(s)
        if self._output_cb is not None:
            self._output_cb(s)
            return
        sys.stdout.write(s if s.endswith("\n") else s + "\n")
