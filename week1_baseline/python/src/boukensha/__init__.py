from .version import VERSION

import os
import sys

from .config import Config
from .tasks.player import Player
from .models import context_window as _model_context_window

_debug = False
_config_instance: Config | None = None


def config() -> Config:
    global _config_instance
    if _config_instance is None:
        _config_instance = Config()
    return _config_instance


def debug() -> None:
    global _debug
    _debug = True


def is_debug() -> bool:
    return _debug


def run(
    *,
    task: str,
    system: str | None = None,
    model: str | None = None,
    backend: str | None = None,
    api_key: str | None = None,
    ollama_host: str = "http://localhost:11434",
    log: str | None = None,
    context_window: int | None = None,
    max_output_tokens: int | None = None,
    working_dir: str | bool | None = None,
    mcp_servers: dict | None = None,
    block=None,
) -> str:
    from .backends import Anthropic, Gemini, Ollama, OllamaCloud, OpenAI

    cfg = config()  # loads .env; populates os.environ
    task_class = Player
    task_settings = cfg.tasks(task_class.task_name())

    if system is None:
        system = task_class.system_prompt(
            task_settings,
            user_prompts_dir=cfg.user_prompts_dir,
            default_prompts_dir=Config.PROMPTS_DIR,
        )
    if model is None:
        model = task_class.model(task_settings)
    resolved_context_window = context_window or _model_context_window(model)
    if backend is None:
        backend = task_class.provider(task_settings)
    if api_key is None:
        api_key = {
            "anthropic": os.environ.get("ANTHROPIC_API_KEY"),
            "openai": os.environ.get("OPENAI_API_KEY"),
            "gemini": os.environ.get("GEMINI_API_KEY"),
            "ollama_cloud": os.environ.get("OLLAMA_API_KEY"),
        }.get(backend)
    if working_dir is None:
        working_dir = os.getcwd()

    ctx = Context(
        task=task_class,
        system=system,
        context_window=resolved_context_window,
        working_dir=working_dir,
        compaction_threshold=cfg.agent_compaction_threshold,
    )
    registry = Registry(ctx)

    clients = None
    logger = None
    try:
        clients = _start_mcp_servers(registry, mcp_servers if mcp_servers is not None else cfg.mcp_servers)

        if block is not None:
            block(RunDSL(registry))

        if backend == "anthropic":
            be = Anthropic(api_key=api_key, model=model)
        elif backend == "openai":
            be = OpenAI(api_key=api_key, model=model)
        elif backend == "gemini":
            be = Gemini(api_key=api_key, model=model)
        elif backend == "ollama":
            be = Ollama(host=ollama_host, model=model)
        elif backend == "ollama_cloud":
            be = OllamaCloud(api_key=api_key, model=model)
        else:
            raise ValueError(
                f"Unknown backend {backend!r}. Use 'anthropic', 'openai', 'gemini', 'ollama', or 'ollama_cloud'."
            )

        builder = PromptBuilder(ctx, be)
        client = Client(builder)
        effective_max_iterations = task_class.max_iterations(task_settings)
        effective_max_output_tokens = (
            max_output_tokens if max_output_tokens is not None else task_class.max_output_tokens(task_settings)
        )

        logger = Logger(
            log=log,
            snapshot={
                "task": task_class.task_name(),
                "max_iterations": effective_max_iterations,
                "max_turn_tokens": cfg.agent_max_turn_tokens,
                "max_output_tokens": effective_max_output_tokens,
                "context_window": resolved_context_window,
                "model": model,
                "provider": backend,
            },
        )
        agent = Agent(
            context=ctx,
            registry=registry,
            builder=builder,
            client=client,
            logger=logger,
            task_settings=task_settings,
            max_iterations=effective_max_iterations,
            max_turn_tokens=cfg.agent_max_turn_tokens,
            max_output_tokens=effective_max_output_tokens,
        )

        ctx.add_message("user", task)
        return agent.run()
    finally:
        if clients:
            for c in clients:
                c.stop()
        if logger is not None:
            logger.close()


# Imported here, before `repl` is defined below, not in the bottom import
# block with everything else: `from .repl import Repl` also binds the
# submodule itself as `boukensha.repl` (Python auto-attributes every
# imported submodule onto its parent package) -- Ruby has no such collision
# since `Boukensha.repl` the method and `boukensha/repl.rb` the file live in
# completely separate namespaces. Importing it before the `def repl(...)`
# below means the function definition (itself just `repl = <function>`)
# runs last and wins, permanently shadowing the submodule attribute.
from .repl import Repl


# tui: accepted for signature parity with Ruby's Boukensha.repl(tui:), but
# this port has no equivalent of Ruby's charm-based Tui class (native
# bubbletea/lipgloss/bubbles bindings, no Python equivalent) -- tui=True and
# tui=False are currently identical, both running the plain Repl loop.
def repl(
    *,
    system: str | None = None,
    model: str | None = None,
    backend: str | None = None,
    api_key: str | None = None,
    ollama_host: str = "http://localhost:11434",
    log: str | None = None,
    context_window: int | None = None,
    max_output_tokens: int | None = None,
    working_dir: str | bool | None = None,
    mcp_servers: dict | None = None,
    tui: bool = True,
    block=None,
) -> None:
    from .backends import Anthropic, Gemini, Ollama, OllamaCloud, OpenAI

    cfg = config()  # loads .env; populates os.environ
    task_class = Player
    task_settings = cfg.tasks(task_class.task_name())

    if system is None:
        system = task_class.system_prompt(
            task_settings,
            user_prompts_dir=cfg.user_prompts_dir,
            default_prompts_dir=Config.PROMPTS_DIR,
        )
    if model is None:
        model = task_class.model(task_settings)
    resolved_context_window = context_window or _model_context_window(model)
    if backend is None:
        backend = task_class.provider(task_settings)
    if api_key is None:
        api_key = {
            "anthropic": os.environ.get("ANTHROPIC_API_KEY"),
            "openai": os.environ.get("OPENAI_API_KEY"),
            "gemini": os.environ.get("GEMINI_API_KEY"),
            "ollama_cloud": os.environ.get("OLLAMA_API_KEY"),
        }.get(backend)
    if working_dir is None:
        working_dir = os.getcwd()

    ctx = Context(
        task=task_class,
        system=system,
        context_window=resolved_context_window,
        working_dir=working_dir,
        compaction_threshold=cfg.agent_compaction_threshold,
    )
    registry = Registry(ctx)

    clients = None
    logger = None
    try:
        clients = _start_mcp_servers(registry, mcp_servers if mcp_servers is not None else cfg.mcp_servers)

        if block is not None:
            block(RunDSL(registry))

        if backend == "anthropic":
            be = Anthropic(api_key=api_key, model=model)
        elif backend == "openai":
            be = OpenAI(api_key=api_key, model=model)
        elif backend == "gemini":
            be = Gemini(api_key=api_key, model=model)
        elif backend == "ollama":
            be = Ollama(host=ollama_host, model=model)
        elif backend == "ollama_cloud":
            be = OllamaCloud(api_key=api_key, model=model)
        else:
            raise ValueError(
                f"Unknown backend {backend!r}. Use 'anthropic', 'openai', 'gemini', 'ollama', or 'ollama_cloud'."
            )

        builder = PromptBuilder(ctx, be)
        client = Client(builder)
        effective_max_iterations = task_class.max_iterations(task_settings)
        effective_max_output_tokens = (
            max_output_tokens if max_output_tokens is not None else task_class.max_output_tokens(task_settings)
        )

        logger = Logger(
            log=log,
            snapshot={
                "task": task_class.task_name(),
                "max_iterations": effective_max_iterations,
                "max_turn_tokens": cfg.agent_max_turn_tokens,
                "max_output_tokens": effective_max_output_tokens,
                "context_window": resolved_context_window,
                "model": model,
                "provider": backend,
            },
        )
        Repl(
            context=ctx,
            registry=registry,
            builder=builder,
            client=client,
            logger=logger,
            task_settings=task_settings,
            max_iterations=effective_max_iterations,
            max_turn_tokens=cfg.agent_max_turn_tokens,
            max_output_tokens=effective_max_output_tokens,
            config_dir=cfg.dir,
            provider=backend,
            model=model,
            version=VERSION,
            api_key=api_key,
            mcp_server_names=[c.name for c in clients],
        ).start()
    except KeyboardInterrupt:
        print("\nInterrupted.")
    finally:
        if clients:
            for c in clients:
                c.stop()
        if logger is not None:
            logger.close()


def _start_mcp_servers(registry, servers: dict | None) -> list:
    if not servers:
        return []

    from .mcp import Client as McpClient
    from .tools import Mcp as ToolsMcp

    started: list = []
    for server_name, raw_opts in servers.items():
        client = None
        required = True

        try:
            required = raw_opts.get("required", True)

            client = McpClient(
                name=str(server_name),
                command=raw_opts["command"],
                args=raw_opts.get("args") or [],
                env=raw_opts.get("env") or {},
            )

            client.start()
            ToolsMcp.register(registry, client=client, prefix=raw_opts.get("prefix"))
            started.append(client)
        except McpClient.Error as e:
            if client is not None:
                client.stop()
            if required is False:
                print(
                    f"[boukensha] MCP server '{server_name}' failed to start: {e} (continuing without it)",
                    file=sys.stderr,
                )
            else:
                for c in started:
                    c.stop()
                raise
        except Exception:
            if client is not None:
                client.stop()
            for c in started:
                c.stop()
            raise

    return started


from .tool import Tool
from .message import Message
from .context import Context
from .errors import UnknownToolError, UnsupportedModelError, ApiError, LoopError
from .registry import Registry
from .prompt_builder import PromptBuilder
from .logger import Logger
from .client import Client
from .agent import Agent
from .run_dsl import RunDSL

__all__ = [
    "Config",
    "Player",
    "config",
    "debug",
    "is_debug",
    "run",
    "repl",
    "VERSION",
    "Tool",
    "Message",
    "Context",
    "UnknownToolError",
    "UnsupportedModelError",
    "ApiError",
    "LoopError",
    "Registry",
    "PromptBuilder",
    "Logger",
    "Client",
    "Agent",
    "RunDSL",
    "Repl",
]
