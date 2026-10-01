#!/usr/bin/env python3
"""Signature oracle for registry entries (ROADMAP Q-02, oracle 2a).

For every registry entry introduced in ``--version``, check the API claims
against the real packages: the member named in ``obsolete_signature`` must
exist on its type in the OLD package, and the member named in
``replacement_signature`` must exist in the NEW package (unless the entry
says it was removed). Package versions come from the train lock, so the
check uses exactly the packages the watcher diffed.

This catches the most common fill errors mechanically: an invented
replacement method, a wrong type, a member that never existed. It does not
compile examples (that needs declared locals); it checks names only.

Exit 0 when every checkable claim holds, 1 when a claim is contradicted,
2 when the oracle cannot run (missing lock entries, tool failure).
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[2]
REGISTRY = REPO / ".github" / "skills" / "maf-obsolete-api-registry" / "registry.yaml"
TRAIN_LOCK = REPO / ".github" / "maf-train-lock.json"
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
REMOVED = re.compile(r"^\s*removed\b", re.IGNORECASE)
# The member a signature names: the identifier before "(" or before "{ get".
# No space before "(": prose such as "AgentSessionStore (and ...)" is not a call.
MEMBER = re.compile(r"(?:^|[\s.])([A-Za-z_][A-Za-z0-9_]*)(?:<[^>()]*>)?(?:\(|\s*\{\s*get)")


@dataclass(frozen=True)
class Finding:
    entry_id: str
    side: str
    message: str


# First member reference, with its qualifying type when written "Owner.Member(".
FIRST_CALL = re.compile(r"(?:^|[\s(,])(?:([A-Z][A-Za-z0-9_]*)\.)?([A-Za-z_][A-Za-z0-9_]*)(?:<[^>()]*>)?\(")


def member_owner(signature: str) -> str | None:
    """Type qualifying the FIRST member call (``ShellExecutor`` in ``ShellExecutor.AsAIFunction(``)."""
    if re.search(r"void \.ctor\(", signature or ""):
        return None
    match = FIRST_CALL.search(signature or "")
    if match and match.group(2) == member_name(signature):
        return match.group(1)
    return None


def member_name(signature: str) -> str | None:
    """Name of the member a C# signature declares (``.ctor`` for constructors)."""
    if not signature or REMOVED.match(signature):
        return None
    if re.search(r"\bvoid \.ctor\(", signature):
        return ".ctor"
    match = MEMBER.search(signature)
    return match.group(1) if match else None


def short_type(type_name: str) -> str:
    name = (type_name or "").strip().split("<")[0].split("`")[0]
    return name.rsplit(".", 1)[-1]


class Inspector:
    """Caches dotnet-inspect type listings per (type, package, version)."""

    def __init__(self, command: list[str], timeout: int = 180):
        self.command = command
        self.timeout = timeout
        self.cache: dict[tuple[str, str, str], str | None] = {}

    def members(self, type_name: str, package: str, version: str) -> str | None:
        key = (type_name, package, version)
        if key not in self.cache:
            try:
                run = subprocess.run(
                    [*self.command, "type", type_name, "--package", f"{package}@{version}"],
                    capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=self.timeout,
                )
                text = ANSI.sub("", run.stdout)
                self.cache[key] = text if run.returncode == 0 and type_name in text else None
            except (OSError, subprocess.TimeoutExpired):
                self.cache[key] = None
        return self.cache[key]


def has_member(listing: str, member: str, type_name: str) -> bool:
    if member == ".ctor":
        return ".ctor(" in listing
    return re.search(rf"\b{re.escape(member)}\b\s*(?:<[^>]*>)?\s*(?:\(|\{{)", listing) is not None


def check_entry(entry: dict, old_pkgs: dict, new_pkgs: dict, inspector: Inspector) -> list[Finding]:
    findings: list[Finding] = []
    package = entry.get("package", "")
    type_name = short_type(entry.get("type", ""))
    for side, field, packages in (("old", "obsolete_signature", old_pkgs), ("new", "replacement_signature", new_pkgs)):
        signature = str(entry.get(field) or "")
        member = member_name(signature)
        if member is None:
            continue  # a type-level change, prose, or "removed": nothing to check by name
        version = packages.get(package)
        if version is None:
            continue  # package not in this train's lock (new or externalized): not checkable
        owner = member_owner(signature)
        if owner and owner == member:
            owner = None
        target_type = owner or type_name
        if member != ".ctor" and member[0].isupper() and owner is None and member != type_name:
            # "ShellEnvironmentProvider(ShellExecutor, ...)": a constructor of another type.
            search = [(package, version)] + ([(o, ov) for o, ov in sorted(packages.items()) if o != package] if side == "new" else [])
            if any((listing := inspector.members(member, pkg, ver)) is not None and ".ctor(" in listing for pkg, ver in search):
                continue
        listing = inspector.members(target_type, package, version)
        if listing is None and side == "new":
            # The replacement may live in another package of the same train
            # (MAF 1.22 moved AgentSessionStore from Hosting to Abstractions).
            for other, other_version in sorted(packages.items()):
                if other != package:
                    listing = inspector.members(target_type, other, other_version)
                    if listing is not None:
                        break
        if listing is None:
            findings.append(Finding(entry["id"], side, f"type {target_type} not found in {package}@{version}"))
        elif not has_member(listing, member, target_type):
            findings.append(Finding(entry["id"], side, f"{target_type}.{member} not found in {package}@{version}"))
    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="registry version_introduced to check (the train's new side)")
    parser.add_argument("--old-version", required=True, help="the train's old side")
    parser.add_argument("--registry", type=Path, default=REGISTRY)
    parser.add_argument("--train-lock", type=Path, default=TRAIN_LOCK)
    parser.add_argument("--dotnet-inspect", default="dotnet-inspect")
    parser.add_argument("--report-out", type=Path)
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    lock = json.loads(args.train_lock.read_text(encoding="utf-8"))["trains"]
    if args.version not in lock or args.old_version not in lock:
        print(f"error: train lock has no entry for {args.old_version} and/or {args.version}", file=sys.stderr)
        return 2
    entries = [e for e in yaml.safe_load(args.registry.read_text(encoding="utf-8"))["entries"]
               if str(e.get("version_introduced")) == args.version]
    inspector = Inspector(args.dotnet_inspect.split())
    findings = [f for e in entries for f in check_entry(e, lock[args.old_version], lock[args.version], inspector)]

    lines = [f"Signature oracle: {len(entries)} entries for MAF {args.version} checked against "
             f"the {args.old_version} and {args.version} packages in the train lock."]
    if findings:
        lines.append(f"{len(findings)} claim(s) contradicted by the packages:")
        lines += [f"  - {f.entry_id} ({f.side} side): {f.message}" for f in findings]
    else:
        lines.append("Every member named in an obsolete/replacement signature exists where the entry says.")
    report = "\n".join(lines) + "\n"
    print(report)
    if args.report_out:
        args.report_out.write_text(report, encoding="utf-8")
    return 1 if findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
