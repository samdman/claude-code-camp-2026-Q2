from __future__ import annotations

from .base import Base


class Gemini(Base):
    BASE_URL = "https://generativelanguage.googleapis.com/v1beta/models"
    MODELS = {
        "gemini-3.5-flash": {
            "context_window": 1_048_576,
            "cost_per_million": {"input": 1.5, "output": 9.0},
            "usage_unit": "tokens",
        },
        "gemini-3.1-flash-lite": {
            "context_window": 1_048_576,
            "cost_per_million": {"input": 0.25, "output": 1.5},
            "usage_unit": "tokens",
        },
    }

    def __init__(self, *, api_key: str, model: str) -> None:
        self.api_key = api_key
        self._configure_model(model)

    def to_messages(self, messages: list) -> list[dict]:
        result = []
        for msg in messages:
            if msg.role == "assistant":
                result.append({"role": "model", "parts": self._assistant_parts(msg.content)})
            elif msg.role == "tool_result":
                result.append({
                    "role": "user",
                    "parts": [{"functionResponse": {"name": msg.tool_use_id, "response": {"content": msg.content}}}],
                })
            else:
                result.append({"role": msg.role, "parts": [{"text": msg.content}]})
        return result

    def to_tools(self, tools: dict) -> list[dict]:
        if not tools:
            return []
        return [{
            "functionDeclarations": [
                {
                    "name": tool.name,
                    "description": tool.description,
                    "parameters": {"type": "object", "properties": tool.parameters, "required": list(tool.parameters.keys())},
                }
                for tool in tools.values()
            ]
        }]

    def to_payload(self, context, *, max_output_tokens: int = 1024, tools: list | None = None) -> dict:
        return {
            "systemInstruction": {"parts": [{"text": context.system}]},
            "contents": self.to_messages(context.messages),
            "tools": self.to_tools(context.tools) if tools is None else tools,
            "generationConfig": {"maxOutputTokens": max_output_tokens, "thinkingConfig": self._thinking_config()},
        }

    @property
    def headers(self) -> dict:
        return {"Content-Type": "application/json", "x-goog-api-key": self.api_key}

    @property
    def url(self) -> str:
        return f"{self.BASE_URL}/{self.model}:generateContent"

    # Normalizes a Gemini generateContent response into the common shape.
    # Gemini doesn't assign call ids, so the function name is reused as the
    # id (Gemini also matches functionResponse back to a call by name).
    def parse_response(self, response: dict) -> dict:
        candidates = response.get("candidates") or []
        first = candidates[0] if candidates else {}
        parts = (first.get("content") or {}).get("parts") or []

        content = []
        tool_used = False

        for part in parts:
            function_call = part.get("functionCall")
            if function_call:
                content.append({
                    "type": "tool_use",
                    "id": function_call["name"],
                    "name": function_call["name"],
                    "input": function_call.get("args") or {},
                    "signature": part.get("thoughtSignature"),
                })
                tool_used = True
            elif part.get("thought"):
                content.append({"type": "reasoning", "text": str(part.get("text") or ""), "signature": part.get("thoughtSignature")})
            elif part.get("text"):
                content.append({"type": "text", "text": part["text"]})

        return {"stop_reason": "tool_use" if tool_used else "end_turn", "content": content}

    def _thinking_config(self) -> dict:
        if self.model == "gemini-3.1-pro-preview-customtools":
            return {"thinkingLevel": "LOW"}  # full disable not supported on this model
        return {"thinkingBudget": 0}  # gemini-3.5-flash, gemini-3.1-flash-lite

    # Rebuilds Gemini "model" parts from normalized content blocks (the
    # inverse of parse_response). Text-only turns are stored as a bare str,
    # so wrap it back into a single text block before mapping.
    def _assistant_parts(self, content) -> list[dict]:
        blocks = [{"type": "text", "text": content}] if isinstance(content, str) else content
        parts = []
        for block in blocks:
            if block["type"] == "tool_use":
                part = {"functionCall": {"name": block["name"], "args": block["input"]}}
                if block.get("signature"):
                    part["thoughtSignature"] = block["signature"]
                parts.append(part)
            elif block["type"] == "reasoning":
                part = {"text": str(block.get("text") or ""), "thought": True}
                if block.get("signature"):
                    part["thoughtSignature"] = block["signature"]
                parts.append(part)
            else:
                parts.append({"text": block["text"]})
        return parts
