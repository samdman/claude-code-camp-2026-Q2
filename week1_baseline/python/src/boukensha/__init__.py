import os

from .config import Config
from .tasks.player import Player

_quiet = False
_debug = False
_config_instance: Config | None = None


def config() -> Config:
    global _config_instance
    if _config_instance is None:
        _config_instance = Config()
    return _config_instance


def quiet() -> None:
    global _quiet
    _quiet = True


def loud() -> None:
    global _quiet
    _quiet = False


def is_quiet() -> bool:
    return _quiet


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
    max_output_tokens: int | None = None,
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
    if backend is None:
        backend = task_class.provider(task_settings)
    if api_key is None:
        api_key = {
            "anthropic": os.environ.get("ANTHROPIC_API_KEY"),
            "openai": os.environ.get("OPENAI_API_KEY"),
            "gemini": os.environ.get("GEMINI_API_KEY"),
            "ollama_cloud": os.environ.get("OLLAMA_API_KEY"),
        }.get(backend)

    ctx = Context(task=task_class, system=system)
    registry = Registry(ctx)

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

    logger = None
    try:
        logger = Logger(
            log=log,
            snapshot={
                "task": task_class.task_name(),
                "max_iterations": effective_max_iterations,
                "max_output_tokens": effective_max_output_tokens,
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
            max_output_tokens=effective_max_output_tokens,
        )

        ctx.add_message("user", task)
        return agent.run()
    finally:
        if logger is not None:
            logger.close()


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
    "quiet",
    "loud",
    "is_quiet",
    "debug",
    "is_debug",
    "run",
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
]
