import os
import sys
from pathlib import Path

import boukensha

sys.stdout.reconfigure(encoding="utf-8")

os.environ.setdefault(
    "BOUKENSHA_DIR",
    str(Path(__file__).resolve().parents[3] / ".boukensha"),
)

cfg = boukensha.config()
print(f"Config: {cfg}")
print(f"API key set? {str(os.environ.get('ANTHROPIC_API_KEY') is not None).lower()}")
print()

boukensha.run(
    task=(
        "Connect to the MUD, look at your surroundings, check your score, "
        "then look at the available exits and tell me what you see."
    ),
    # system/model/api_key all come from config automatically
    working_dir=False,  # no filesystem tools needed for MUD play
    # mcp_servers: comes from config (settings.yaml's mcp_servers: block) automatically
)
