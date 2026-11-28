#!/usr/bin/env python3
"""Capture or compare the danger reads on a paused game, through a fresh stdio proxy.

capture writes one JSON file of the danger reads; compare reads them again and prints any difference.
Use the same paused save for both. The bridge address and token come from the RIMCP_URL and
RIMCP_TOKEN environment variables, or from a Claude config's mcpServers entry with --claude-config.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

PROTOCOL = "2025-06-18"
PROXY_WAIT_SECONDS = 10
# The reads that report a danger to the colony, each with the arguments used for the golden.
READS = [
    ("get_turn_digest", {}),
    ("get_colony_status", {}),
    ("list_threats", {"detail": "full"}),
    ("get_environment", {}),
    ("list_work", {"sweep": True}),
]
# Envelope fields that change between identical reads of a paused game.
VOLATILE = {"loadId", "requestId", "revision", "generatedAt", "elapsedMs", "replyBytes", "observedAt", "build"}


def bridge_env(claude_config, project):
    env = dict(os.environ)
    if claude_config is None:
        return env
    servers = json.loads(Path(claude_config).read_text())["projects"][project]["mcpServers"]
    env.update(servers["rimcp"].get("env", {}))
    return env


class Proxy:
    def __init__(self, binary, env):
        self.process = subprocess.Popen([binary], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=env)
        self.next_id = 0
        self.call("initialize", {"protocolVersion": PROTOCOL, "capabilities": {}, "clientInfo": {"name": "danger-golden", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def call(self, method, params):
        self.next_id += 1
        self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        while True:
            reply = json.loads(self.process.stdout.readline())
            if reply.get("id") == self.next_id:
                return reply

    def tool(self, name, arguments):
        reply = self.call("tools/call", {"name": name, "arguments": arguments})
        text = "".join(part.get("text", "") for part in reply.get("result", {}).get("content", []))
        # The proxy puts a one-line summary before the reply's JSON.
        start = text.find("{")
        return json.loads(text[start:]) if start >= 0 else {"text": text, "reply": reply}

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=PROXY_WAIT_SECONDS)


def stable(value):
    if isinstance(value, dict):
        return {key: stable(item) for key, item in value.items() if key not in VOLATILE}
    if isinstance(value, list):
        return [stable(item) for item in value]
    return value


def read_all(proxy):
    return {name: stable(proxy.tool(name, arguments)) for name, arguments in READS}


def differences(old, new, path=""):
    if isinstance(old, dict) and isinstance(new, dict):
        for key in sorted(set(old) | set(new)):
            yield from differences(old.get(key), new.get(key), f"{path}.{key}")
        return
    if isinstance(old, list) and isinstance(new, list) and len(old) == len(new):
        for index, (left, right) in enumerate(zip(old, new)):
            yield from differences(left, right, f"{path}[{index}]")
        return
    if old != new:
        yield f"{path}: {json.dumps(old)} -> {json.dumps(new)}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["capture", "compare"])
    parser.add_argument("golden", type=Path)
    parser.add_argument("--proxy", required=True)
    parser.add_argument("--claude-config")
    parser.add_argument("--project", default=os.getcwd())
    args = parser.parse_args()
    proxy = Proxy(args.proxy, bridge_env(args.claude_config, args.project))
    reads = read_all(proxy)
    proxy.close()
    if args.mode == "capture":
        args.golden.write_text(json.dumps(reads, indent=1, sort_keys=True) + "\n")
        print(f"captured {len(reads)} reads to {args.golden}")
        return 0
    changed = list(differences(json.loads(args.golden.read_text()), reads))
    print("\n".join(changed) if changed else "identical")
    return 1 if changed else 0


if __name__ == "__main__":
    sys.exit(main())
