from __future__ import annotations

import os

from .message import Message


class Context:
    def __init__(self, *, task, system: str | None = None, working_dir: str | bool | None = None) -> None:
        self.task = task
        self.system = system
        self.working_dir = os.path.abspath(working_dir) if working_dir else None
        self.messages: list[Message] = []
        self.tools: dict = {}

    def register_tool(self, tool) -> None:
        self.tools[tool.name] = tool

    def add_message(self, role: str, content: str, *, tool_use_id: str | None = None) -> None:
        self.messages.append(Message(role, content, tool_use_id))

    def clear_messages(self) -> None:
        self.messages = []

    @property
    def tool_count(self) -> int:
        return len(self.tools)

    @property
    def turn_count(self) -> int:
        return len(self.messages)

    def __repr__(self) -> str:
        task_name = self.task.task_name() if self.task else ""
        return f"#<Context task={task_name} turns={self.turn_count} tools={self.tool_count}>"

    __str__ = __repr__
