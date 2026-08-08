import os
import sys
from pathlib import Path

import boukensha

# The final response text comes from the model and may contain non-ASCII
# characters. On Windows, sys.stdout defaults to the console's ANSI codepage
# rather than UTF-8 whenever it isn't an interactive terminal (e.g. piped for
# parity checks), which would raise UnicodeEncodeError. Force UTF-8 so this
# runs the same way piped or interactive, on any platform.
sys.stdout.reconfigure(encoding="utf-8")

os.environ.setdefault(
    "BOUKENSHA_DIR",
    str(Path(__file__).resolve().parents[3] / ".boukensha"),
)

# Config is loaded automatically inside boukensha.run() — system prompt,
# model, and API key all come from ~/.boukensha (or BOUKENSHA_DIR) by
# default. You can still override any of them as keyword arguments if you
# want.

print("=== BOUKENSHA Step 7: The boukensha.run DSL ===")
print()
print(f"Config: {boukensha.config()}")
print()

base_dir = Path(__file__).resolve().parent.parent


def register_tools(dsl):
    dsl.tool(
        "read_file",
        description="Read the contents of a file from disk",
        parameters={"path": {"type": "string", "description": "The file path to read"}},
        block=lambda path: open(os.path.join(base_dir, path), "r", encoding="utf-8").read(),
    )

    dsl.tool(
        "list_directory",
        description="List the files in a directory",
        parameters={"path": {"type": "string", "description": "The directory path to list"}},
        block=lambda path: ", ".join(f for f in os.listdir(os.path.join(base_dir, path)) if not f.startswith(".")),
    )


result = boukensha.run(
    task="Read the README.md file and summarise what this MUD player assistant framework can do.",
    block=register_tools,
)

print()
print("=== FINAL RESPONSE ===")
print(result)
