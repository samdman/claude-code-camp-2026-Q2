from __future__ import annotations

import os
import sys

from .agent import Agent
from .errors import ApiError, LoopError


class Repl:
    PROMPT = "boukensha> "

    HELP = (
        "Commands:\n"
        "  /quiet   suppress logging output\n"
        "  /loud    re-enable logging output\n"
        "  /clear   wipe conversation history (tools stay)\n"
        "  /exit    leave the REPL\n"
        "  /help    show this message\n"
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
        task_settings: dict | None = None,
        max_iterations: int | None = None,
        max_output_tokens: int | None = None,
    ) -> None:
        self._context = context
        self._registry = registry
        self._builder = builder
        self._client = client
        self._logger = logger
        self._task_settings = task_settings
        self._max_iterations = max_iterations
        self._max_output_tokens = max_output_tokens
        self._config_dir = config_dir
        self._provider = provider
        self._model = model
        self._version = version
        self._api_key = api_key
        self._turn = 0

    def start(self) -> None:
        print(self._banner(), end="")

        while True:
            print(self.PROMPT, end="")
            sys.stdout.flush()

            raw_line = sys.stdin.readline()
            if not raw_line:  # EOF / Ctrl-D
                break

            input_line = raw_line.strip()
            if not input_line:
                continue

            if input_line in ("/exit", "/quit"):
                print("Goodbye.")
                break
            elif input_line == "/help":
                print(self.HELP, end="")
                continue
            elif input_line == "/quiet":
                import boukensha

                boukensha.quiet()
                print("(logging suppressed — type /loud to re-enable)")
                continue
            elif input_line == "/loud":
                import boukensha

                boukensha.loud()
                print("(logging enabled)")
                continue
            elif input_line == "/clear":
                self._context.clear_messages()
                self._turn = 0
                print("(conversation history cleared)")
                continue

            self._run_turn(input_line)

    def _banner(self) -> str:
        key_status = "✗ API key not set" if not self._api_key or not self._api_key.strip() else "✓ API key set"
        provider_line = f"{self._provider or 'default'} ({self._model or 'default'})  {key_status}"
        config_exists = self._config_dir and os.path.isdir(self._config_dir)
        config_line = (
            self._config_dir if config_exists else f"{self._config_dir or '(default)'}  ✗ directory not found"
        )
        ver = self._version or "?.?.?"

        return (
            "\n"
            "╔══════════════════════════════════════╗\n"
            f"║  BOUKENSHA MUD Assistant (v{ver}){' ' * (9 - len(ver))}║\n"
            "╚══════════════════════════════════════╝\n"
            f"  config:    {config_line}\n"
            f"  provider:  {provider_line}\n"
            "\n"
            "  /quiet or /loud   toggle logging\n"
            "  /clear           reset conversation history\n"
            "  /exit or /quit    leave the REPL\n"
            "\n"
        )

    def _run_turn(self, input_line: str) -> None:
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
            max_output_tokens=self._max_output_tokens,
        )
        try:
            result = agent.run()
        except LoopError as e:
            print(f"\n[error] {e}")
            return
        except ApiError as e:
            print(f"\n[error] API call failed: {e}")
            return

        # Print the final response outside of the logger so it is always visible,
        # even when boukensha.quiet() is active.
        print()
        print(result)
