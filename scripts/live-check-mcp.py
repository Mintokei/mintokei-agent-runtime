#!/usr/bin/env python3
"""A one-tool stdio MCP server, for scripts/live-check.sh.

`lookup_badge` returns a value that exists nowhere on disk, so an agent can only know it by having
called the tool — or by having the call in its transcript. That is what makes it a usable probe for
whether an MCP call survives a move into a CLI that has no such server.
"""
import json
import sys

TOOL = {
    "name": "lookup_badge",
    "description": "Look up the badge for an employee id.",
    "inputSchema": {
        "type": "object",
        "properties": {"id": {"type": "integer", "description": "employee id"}},
        "required": ["id"],
    },
}

BADGES = {4821: "FALCON-13", 9003: "HERON-88"}


def reply(msg_id, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": msg_id, "result": result}) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
    except json.JSONDecodeError:
        continue

    method, msg_id = msg.get("method"), msg.get("id")

    if method == "initialize":
        reply(msg_id, {
            "protocolVersion": "2024-11-05",
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "ledger", "version": "1.0.0"},
        })
    elif method == "tools/list":
        reply(msg_id, {"tools": [TOOL]})
    elif method == "tools/call":
        args = msg.get("params", {}).get("arguments", {})
        badge = BADGES.get(int(args.get("id", 0)), "UNKNOWN")
        reply(msg_id, {"content": [{"type": "text", "text": badge}], "isError": False})
    elif msg_id is not None:
        reply(msg_id, {})
