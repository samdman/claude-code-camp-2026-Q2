# Step 12 -- Context Management (MUD demo)
#
# Same MCP-hosted MUD tools as steps 10-11 (mud-manager --mcp via
# mcp_servers: in settings.yaml). New this step: accurate context/token
# tracking, auto-compaction, a max_turn_tokens circuit breaker, and
# cross-provider reasoning-block normalization -- all visible in the
# session's JSONL log (~/.boukensha/sessions/), not in this script's
# stdout output, which stays byte-similar to step 11's.

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
