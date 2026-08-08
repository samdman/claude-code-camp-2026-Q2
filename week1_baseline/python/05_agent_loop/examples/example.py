import os
import sys
from pathlib import Path

from boukensha import Agent, Client, Config, Context, Player, PromptBuilder, Registry
from boukensha.backends import Anthropic, Gemini, Ollama, OllamaCloud, OpenAI

# Agent's tool-call trace prints a "→" (U+2192). On Windows, stdout defaults
# to the console's ANSI codepage (not UTF-8) whenever it isn't an
# interactive terminal (e.g. piped/redirected for parity checks), which
# raises UnicodeEncodeError on that character. Force UTF-8 so this runs the
# same way piped or interactive, on any platform.
sys.stdout.reconfigure(encoding="utf-8")

os.environ.setdefault(
    "BOUKENSHA_DIR",
    str(Path(__file__).resolve().parents[3] / ".boukensha"),
)

config = Config()
player_settings = config.tasks("player")
system_prompt = Player.system_prompt(
    player_settings,
    user_prompts_dir=config.user_prompts_dir,
    default_prompts_dir=Config.PROMPTS_DIR,
)
base_dir = Path(__file__).resolve().parent.parent

ctx = Context(task=Player, system=system_prompt)
registry = Registry(ctx)

provider = Player.provider(player_settings)
model = Player.model(player_settings)

if provider == "anthropic":
    backend = Anthropic(api_key=os.environ["ANTHROPIC_API_KEY"], model=model)
elif provider == "openai":
    backend = OpenAI(api_key=os.environ["OPENAI_API_KEY"], model=model)
elif provider == "gemini":
    backend = Gemini(api_key=os.environ["GEMINI_API_KEY"], model=model)
elif provider == "ollama":
    backend = Ollama(model=model)
elif provider == "ollama_cloud":
    backend = OllamaCloud(api_key=os.environ["OLLAMA_API_KEY"], model=model)
else:
    raise ValueError(f"Unsupported provider for player task: {provider}")

builder = PromptBuilder(ctx, backend)
client = Client(builder)
agent = Agent(
    context=ctx,
    registry=registry,
    builder=builder,
    client=client,
    task_settings=player_settings,
)

registry.tool(
    "read_file",
    description="Read the contents of a file from disk",
    parameters={"path": {"type": "string", "description": "The file path to read"}},
    block=lambda path: open(os.path.join(base_dir, path), "r", encoding="utf-8").read(),
)

registry.tool(
    "list_directory",
    description="List the files in a directory",
    parameters={"path": {"type": "string", "description": "The directory path to list"}},
    block=lambda path: ", ".join(f for f in os.listdir(os.path.join(base_dir, path)) if not f.startswith(".")),
)

ctx.add_message("user", "Read the README.md file and summarise what this MUD player assistant framework can do.")

print("=== BOUKENSHA Step 5: Agent Loop ===")
print()
print(f"Config: {config}")
print(f"Provider: {provider}")
print(f"Model: {model}")
print(f"Max iterations: {Player.max_iterations(player_settings)}")
print(f"Max output tokens: {Player.max_output_tokens(player_settings)}")
print()

result = agent.run()

print()
print("=== FINAL RESPONSE ===")
print(result)
