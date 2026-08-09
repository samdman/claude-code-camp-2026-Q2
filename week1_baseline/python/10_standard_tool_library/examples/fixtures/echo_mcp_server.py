#!/usr/bin/env python
"""Minimal standalone MCP server used only as a test fixture for boukensha.mcp.Client."""

import json
import os
import sys

if os.environ.get("NOISY_PREFIX"):
    print("not json — a stray banner line printed before any real protocol traffic", flush=True)

_flood_kb = os.environ.get("STDERR_FLOOD_KB")
STDERR_FLOOD_KB = int(_flood_kb) if _flood_kb else None

TOOLS = {
    "echo": {
        "description": "Returns 'you said: <message>'",
        "inputSchema": {"type": "object", "properties": {"message": {"type": "string"}}, "required": []},
    },
    "boom": {
        "description": "Always returns an error",
        "inputSchema": {"type": "object", "properties": {}, "required": []},
    },
}


def respond(request_id, result) -> None:
    print(json.dumps({"jsonrpc": "2.0", "id": request_id, "result": result}), flush=True)


for raw_line in sys.stdin:
    line = raw_line.strip()
    if not line:
        continue

    request = json.loads(line)
    request_id = request.get("id")
    method = request.get("method")

    if method == "initialize":
        respond(
            request_id,
            {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "echo-fixture", "version": "0.0.1"},
            },
        )
    elif method == "notifications/initialized":
        continue
    elif method == "tools/list":
        respond(
            request_id,
            {
                "tools": [
                    {"name": name, "description": t["description"], "inputSchema": t["inputSchema"]}
                    for name, t in TOOLS.items()
                ]
            },
        )
    elif method == "tools/call":
        params = request.get("params") or {}
        name = params.get("name")
        args = params.get("arguments") or {}

        if STDERR_FLOOD_KB:
            sys.stderr.write("x" * (STDERR_FLOOD_KB * 1024))
            sys.stderr.flush()

        if name == "echo":
            respond(request_id, {"content": [{"type": "text", "text": f"you said: {args.get('message')}"}], "isError": False})
        elif name == "boom":
            respond(request_id, {"content": [{"type": "text", "text": "boom: intentional failure"}], "isError": True})
        else:
            respond(request_id, {"content": [{"type": "text", "text": f"error: unknown tool '{name}'"}], "isError": True})
    else:
        if request_id is not None:
            print(
                json.dumps({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": f"Method not found: {method}"}}),
                flush=True,
            )
