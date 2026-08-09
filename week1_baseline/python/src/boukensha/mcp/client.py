from __future__ import annotations

import json
import os
import subprocess
import threading

from .. import VERSION


class Client:
    class Error(Exception):
        pass

    PROTOCOL_VERSION = "2024-11-05"

    def __init__(self, *, name: str, command: str, args: list | None = None, env: dict | None = None) -> None:
        self.name = name
        self._command = command
        self._args = args or []
        self._env = {str(k): str(v) for k, v in (env or {}).items()}
        self._next_id = 0
        self._process: subprocess.Popen | None = None
        self._stderr_thread: threading.Thread | None = None
        self._stderr_buf = ""
        self._stderr_lock = threading.Lock()

    def start(self) -> "Client":
        try:
            self._process = subprocess.Popen(
                [self._command, *self._args],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                env={**os.environ, **self._env},
                text=True,
                bufsize=1,
            )
        except OSError as e:
            raise self.Error(f"failed to start MCP server '{self.name}' ({self._command}): {e}") from e

        self._stderr_thread = threading.Thread(target=self._drain_stderr, daemon=True)
        self._stderr_thread.start()
        self._handshake()
        return self

    def tools_list(self) -> list:
        return self._request("tools/list", {}).get("tools") or []

    def tools_call(self, tool_name: str, arguments: dict) -> str:
        result = self._request("tools/call", {"name": tool_name, "arguments": arguments})
        text = "".join(b["text"] for b in (result.get("content") or []) if b.get("type") == "text")
        if result.get("isError"):
            raise self.Error(f"tool '{tool_name}' on '{self.name}' failed: {text}")
        return text

    def stop(self) -> None:
        if self._process is None:
            return

        process = self._process
        try:
            process.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            process.stdout.close()
        except (OSError, ValueError):
            pass
        if self._stderr_thread is not None:
            self._stderr_thread.join(2)
        try:
            process.stderr.close()
        except (OSError, ValueError):
            pass
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            pass
        finally:
            self._process = None
            self._stderr_thread = None

    def _handshake(self) -> None:
        self._request(
            "initialize",
            {
                "protocolVersion": self.PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "boukensha", "version": VERSION},
            },
        )
        self._notify("notifications/initialized", {})

    def _request(self, method: str, params: dict) -> dict:
        self._next_id += 1
        request_id = self._next_id
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        response = self._read_response(request_id)
        if response.get("error"):
            raise self.Error(f"{self.name}: {response['error']['message']}")
        return response.get("result") or {}

    def _notify(self, method: str, params: dict) -> None:
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def _write(self, payload: dict) -> None:
        try:
            self._process.stdin.write(json.dumps(payload) + "\n")
            self._process.stdin.flush()
        except (BrokenPipeError, OSError) as e:
            raise self.Error(f"MCP server '{self.name}' closed its input unexpectedly") from e

    def _read_response(self, expected_id: int) -> dict:
        while True:
            line = self._process.stdout.readline()
            if line == "":
                raise self.Error(
                    f"MCP server '{self.name}' closed its output unexpectedly (stderr: {self._drain_snapshot()})"
                )

            line = line.strip()
            if not line:
                continue

            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                continue

            if message.get("id") is None:
                continue
            if message["id"] == expected_id:
                return message

    def _drain_stderr(self) -> None:
        try:
            for line in self._process.stderr:
                with self._stderr_lock:
                    self._stderr_buf += line
        except (OSError, ValueError):
            pass

    def _drain_snapshot(self) -> str:
        with self._stderr_lock:
            return self._stderr_buf
