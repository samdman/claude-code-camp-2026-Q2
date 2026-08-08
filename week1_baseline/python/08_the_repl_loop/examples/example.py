import os
import sys
from pathlib import Path

import boukensha

# The banner prints box-drawing characters and status glyphs unconditionally
# on every REPL start (not just when the model's own reply happens to
# contain non-ASCII, like prior steps) — see that step's Confirmed Decision 5
# for why this fix is load-bearing here, not just defensive.
sys.stdout.reconfigure(encoding="utf-8")

os.environ.setdefault(
    "BOUKENSHA_DIR",
    str(Path(__file__).resolve().parents[3] / ".boukensha"),
)

print(f"Config: {boukensha.config()}")
print()

# The step 07 folder makes a good playground — it already has source files
# to read and list.
base_dir = Path(__file__).resolve().parents[2] / "07_the_run_dsl"


def register_tools(dsl):
    dsl.tool(
        "read_file",
        description="Read the contents of a file from disk",
        parameters={"path": {"type": "string", "description": "File path (relative to the working directory)"}},
        block=lambda path: open(os.path.join(base_dir, path), "r", encoding="utf-8").read(),
    )

    dsl.tool(
        "list_directory",
        description="List the files in a directory",
        parameters={
            "path": {"type": "string", "description": "Directory path (relative to the working directory, or '.' for root)"}
        },
        block=lambda path: ", ".join(
            sorted(f for f in os.listdir(os.path.join(base_dir, path)) if not f.startswith("."))
        ),
    )


boukensha.repl(block=register_tools)
