#!/usr/bin/env python3
"""MCP stdio smoke test for a maf-doctor build (ROADMAP A-03 canary).

Starts the server, completes the MCP handshake, lists tools, prompts,
resources and resource templates, and makes one real tool call. Used by
release.yml against the freshly packed tool before anything is published.
"""
from __future__ import annotations

import argparse
import json
import os
import queue
import subprocess
import sys
import threading

PROTOCOL = "2025-06-18"
PROBE_ENTRY = "MAF150-PROVIDER-001"


class Server:
    def __init__(self, command: list[str], timeout: float):
        env = {**os.environ, "MAF_DOCTOR_UPDATE_CHECK": "0"}
        self.proc = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", env=env,
        )
        self.timeout = timeout
        self.lines: queue.Queue[str] = queue.Queue()
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self) -> None:
        for line in self.proc.stdout:  # type: ignore[union-attr]
            self.lines.put(line)
        self.lines.put("")

    def send(self, message: dict) -> None:
        self.proc.stdin.write(json.dumps(message) + "\n")  # type: ignore[union-attr]
        self.proc.stdin.flush()  # type: ignore[union-attr]

    def request(self, request_id: int, method: str, params: dict | None = None) -> dict:
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}})
        while True:
            try:
                line = self.lines.get(timeout=self.timeout)
            except queue.Empty:
                raise SystemExit(f"FAIL: no response to {method} within {self.timeout}s")
            if not line:
                raise SystemExit(f"FAIL: server exited before answering {method}")
            message = json.loads(line)
            if message.get("id") == request_id:
                if "error" in message:
                    raise SystemExit(f"FAIL: {method} returned error {message['error']}")
                return message["result"]

    def close(self) -> None:
        try:
            self.proc.stdin.close()  # type: ignore[union-attr]
        finally:
            self.proc.terminate()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expect-version", help="serverInfo.version the build must report")
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument("command", nargs=argparse.REMAINDER, help="server command, after --")
    args = parser.parse_args(argv)
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("pass the server command after --")

    server = Server(command, args.timeout)
    try:
        init = server.request(1, "initialize", {
            "protocolVersion": PROTOCOL, "capabilities": {},
            "clientInfo": {"name": "maf-doctor-canary", "version": "1"},
        })
        info = init.get("serverInfo", {})
        print(f"initialize: protocol {init.get('protocolVersion')}, server {info.get('name')} {info.get('version')}")
        if args.expect_version and info.get("version") != args.expect_version:
            raise SystemExit(f"FAIL: server reports {info.get('version')}, expected {args.expect_version}")
        server.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        counts = {}
        for request_id, (method, key) in enumerate(
            [("tools/list", "tools"), ("prompts/list", "prompts"),
             ("resources/list", "resources"), ("resources/templates/list", "resourceTemplates")],
            start=2,
        ):
            counts[key] = len(server.request(request_id, method).get(key, []))
        print("lists:", ", ".join(f"{k}={v}" for k, v in counts.items()))
        if min(counts.values()) == 0:
            raise SystemExit("FAIL: a capability list is empty")

        result = server.request(9, "tools/call", {
            "name": "maf_registry_lookup", "arguments": {"entryId": PROBE_ENTRY},
        })
        text = "".join(c.get("text", "") for c in result.get("content", []))
        if result.get("isError") or PROBE_ENTRY not in text:
            raise SystemExit(f"FAIL: maf_registry_lookup({PROBE_ENTRY}) did not return the entry")
        print(f"tools/call maf_registry_lookup({PROBE_ENTRY}): ok")
    finally:
        server.close()
    print("MCP smoke test passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
