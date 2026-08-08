from __future__ import annotations

from dataclasses import dataclass
from typing import Callable


@dataclass
class Tool:
    name: str
    description: str
    parameters: dict
    block: Callable

    def __repr__(self) -> str:
        keys = ", ".join(f":{key}" for key in self.parameters.keys())
        return f"#<Tool name={self.name} description={self.description[:41]} params=[{keys}]>"

    __str__ = __repr__
