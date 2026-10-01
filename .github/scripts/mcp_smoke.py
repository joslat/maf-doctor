#!/usr/bin/env python3
"""MCP stdio smoke test for a maf-doctor build (ROADMAP A-03 canary).

Starts the server, completes the MCP handshake, lists tools, prompts,
resources and resource templates, reads every resource, renders every
argument-free prompt, makes one real tool call, then repeats discovery and a
call over the stateless 2026-07-28 flow (no initialize). build-test runs it on
every PR (M-02); release.yml runs it on the packed tool before publishing.
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
STATELESS_PROTOCOL = "2026-07-28"
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

        lists = {}
        for request_id, (method, key) in enumerate(
            [("tools/list", "tools"), ("prompts/list", "prompts"),
             ("resources/list", "resources"), ("resources/templates/list", "resourceTemplates")],
            start=2,
        ):
            lists[key] = server.request(request_id, method).get(key, [])
        print("lists:", ", ".join(f"{k}={len(v)}" for k, v in lists.items()))
        if min(len(v) for v in lists.values()) == 0:
            raise SystemExit("FAIL: a capability list is empty")

        request_id = 100
        for resource in lists["resources"]:
            request_id += 1
            contents = server.request(request_id, "resources/read", {"uri": resource["uri"]}).get("contents", [])
            if not any(c.get("text") for c in contents):
                raise SystemExit(f"FAIL: resource {resource['uri']} is empty")
        request_id += 1
        skill = server.request(request_id, "resources/read", {"uri": "maf://skills?name=maf-release-watcher"})
        if not any(c.get("text") for c in skill.get("contents", [])):
            raise SystemExit("FAIL: resource template maf://skills{?name} returned nothing")
        print(f"resources/read: all {len(lists['resources'])} resources + 1 template non-empty")

        no_arg_prompts = [
            p["name"] for p in lists["prompts"]
            if not any(a.get("required") for a in p.get("arguments", []))
        ]
        for name in no_arg_prompts:
            request_id += 1
            messages = server.request(request_id, "prompts/get", {"name": name}).get("messages", [])
            if not messages:
                raise SystemExit(f"FAIL: prompt {name} returned no messages")
        print(f"prompts/get: {len(no_arg_prompts)} argument-free prompts render")

        result = server.request(9, "tools/call", {
            "name": "maf_registry_lookup", "arguments": {"entryId": PROBE_ENTRY},
        })
        text = "".join(c.get("text", "") for c in result.get("content", []))
        if result.get("isError") or PROBE_ENTRY not in text:
            raise SystemExit(f"FAIL: maf_registry_lookup({PROBE_ENTRY}) did not return the entry")
        print(f"tools/call maf_registry_lookup({PROBE_ENTRY}): ok")
    finally:
        server.close()

    stateless_probe(command, args.timeout, len(lists["tools"]))
    print("MCP smoke test passed.")
    return 0


def stateless_probe(command: list[str], timeout: float, expected_tools: int) -> None:
    """MCP 2026-07-28: no initialize handshake; every request carries _meta."""
    meta = {"_meta": {
        "io.modelcontextprotocol/protocolVersion": STATELESS_PROTOCOL,
        "io.modelcontextprotocol/clientCapabilities": {},
        "io.modelcontextprotocol/clientInfo": {"name": "maf-doctor-canary", "version": "1"},
    }}
    server = Server(command, timeout)
    try:
        discover = server.request(1, "server/discover", meta)
        if STATELESS_PROTOCOL not in discover.get("supportedVersions", []):
            raise SystemExit(f"FAIL: server/discover does not list {STATELESS_PROTOCOL}")
        tools = server.request(2, "tools/list", meta).get("tools", [])
        if len(tools) != expected_tools:
            raise SystemExit(f"FAIL: stateless tools/list returned {len(tools)}, handshake returned {expected_tools}")
        result = server.request(3, "tools/call", {**meta, "name": "maf_registry_lookup", "arguments": {"entryId": PROBE_ENTRY}})
        if result.get("isError"):
            raise SystemExit("FAIL: stateless tools/call failed")
        print(f"stateless {STATELESS_PROTOCOL}: server/discover, tools/list ({len(tools)}), tools/call ok")
    finally:
        server.close()


if __name__ == "__main__":
    raise SystemExit(main())
