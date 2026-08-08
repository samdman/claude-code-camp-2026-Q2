from __future__ import annotations

import os


class Base:
    @classmethod
    def task_name(cls) -> str:
        raise NotImplementedError(f"{cls.__name__} must define task_name()")

    @classmethod
    def provider(cls, settings: dict) -> str:
        value = settings.get("provider")
        if not value:
            raise ValueError(f"tasks.{cls.task_name()}.provider is required in settings.yaml")
        return value

    @classmethod
    def model(cls, settings: dict) -> str:
        value = settings.get("model")
        if not value:
            raise ValueError(f"tasks.{cls.task_name()}.model is required in settings.yaml")
        return value

    @classmethod
    def prompt_override(cls, settings: dict, prompt: str = "system") -> bool:
        node = settings.get("prompt_override")
        if not isinstance(node, dict):
            return False
        return node.get(prompt) is True

    @classmethod
    def prompt(
        cls,
        settings: dict,
        name: str = "system",
        *,
        user_prompts_dir: str | None = None,
        default_prompts_dir: str | None = None,
    ) -> str | None:
        if cls.prompt_override(settings, name):
            text = cls._read_user_prompt(name, user_prompts_dir=user_prompts_dir)
            if text is not None:
                return text
        return cls._read_default_prompt(name, default_prompts_dir=default_prompts_dir)

    @classmethod
    def system_prompt(
        cls,
        settings: dict,
        *,
        user_prompts_dir: str | None = None,
        default_prompts_dir: str | None = None,
    ) -> str | None:
        return cls.prompt(
            settings,
            "system",
            user_prompts_dir=user_prompts_dir,
            default_prompts_dir=default_prompts_dir,
        )

    @classmethod
    def _read_user_prompt(cls, prompt_name: str, *, user_prompts_dir: str | None = None) -> str | None:
        if not user_prompts_dir:
            return None
        return cls._read_file(os.path.join(user_prompts_dir, cls.task_name(), f"{prompt_name}.md"))

    @classmethod
    def _read_default_prompt(cls, prompt_name: str, *, default_prompts_dir: str | None = None) -> str | None:
        if not default_prompts_dir:
            return None
        return cls._read_file(os.path.join(default_prompts_dir, f"{prompt_name}.md"))

    @staticmethod
    def _read_file(path: str) -> str | None:
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                return f.read().strip()
        return None
