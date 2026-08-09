from __future__ import annotations

import os
from pathlib import Path

import yaml
from dotenv import load_dotenv


class Config:
    # The .boukensha config directory is resolved in this order:
    #   1. BOUKENSHA_DIR environment variable
    #   2. ~/.boukensha  (default)
    DEFAULT_DIR = str(Path.home() / ".boukensha")

    # Default prompts shipped alongside the library code.
    # src/boukensha/config.py -> src/boukensha -> src -> python, then /prompts.
    PROMPTS_DIR = str(Path(__file__).resolve().parent.parent.parent / "prompts")

    def __init__(self) -> None:
        self.dir = self._resolve_dir()
        self._load_env()
        self.settings = self._load_settings()

    # ---------- tasks -----------------------------------------------------

    def tasks(self, name: str | None = None):
        all_tasks = self.dig("tasks") or {}
        if name is None:
            return all_tasks
        return all_tasks.get(name)

    @property
    def user_prompts_dir(self) -> str:
        return os.path.join(self.dir, "prompts")

    # ---------- MCP servers -------------------------------------------------

    @property
    def mcp_servers(self) -> dict:
        return self.dig("mcp_servers") or {}

    # ---------- low-level helpers -------------------------------------------

    def dig(self, *keys: str):
        node = self.settings
        for key in keys:
            if isinstance(node, dict):
                node = node.get(key)
            else:
                return None
        return node

    def __repr__(self) -> str:
        return f"#<Boukensha::Config dir={self.dir} tasks={','.join(self.tasks().keys())}>"

    __str__ = __repr__

    # ---------- private -----------------------------------------------------

    def _resolve_dir(self) -> str:
        # Ruby's Pathname#expand_path always returns forward slashes, even on
        # Windows; match that so example.py's output is diff-identical to example.rb's.
        raw = os.environ.get("BOUKENSHA_DIR") or self.DEFAULT_DIR
        return Path(raw).expanduser().absolute().as_posix()

    def _load_env(self) -> None:
        env_file = os.path.join(self.dir, ".env")
        if os.path.exists(env_file):
            load_dotenv(env_file)

    def _load_settings(self) -> dict:
        settings_file = os.path.join(self.dir, "settings.yaml")
        if os.path.exists(settings_file):
            with open(settings_file, "r", encoding="utf-8") as f:
                return yaml.safe_load(f) or {}
        return {}
