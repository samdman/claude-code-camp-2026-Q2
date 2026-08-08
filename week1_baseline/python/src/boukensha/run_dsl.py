from __future__ import annotations

from typing import Callable

from .tool import Tool


class RunDSL:
    def __init__(self, registry) -> None:
        self._registry = registry

    def tool(self, name: str, *, description: str, parameters: dict | None = None, block: Callable) -> Tool:
        return self._registry.tool(name, description=description, parameters=parameters, block=block)
