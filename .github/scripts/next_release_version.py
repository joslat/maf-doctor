#!/usr/bin/env python3
"""Decide whether main needs a new maf-doctor release (ROADMAP A-03).

A release is due when ``.maf-version`` on HEAD covers a newer MAF than the one
shipped by the latest stable ``vX.Y.Z`` tag. The next version bumps the minor:
each MAF train becomes one maf-doctor minor release. Writes ``release``,
``version`` and ``reason`` to ``$GITHUB_OUTPUT`` when it is set.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
from pathlib import Path

STABLE_TAG = re.compile(r"v(\d+)\.(\d+)\.(\d+)")
VERSION = re.compile(r"\d+\.\d+\.\d+")


def _key(version: str) -> tuple[int, int, int]:
    major, minor, patch = (int(part) for part in version.split("."))
    return major, minor, patch


def latest_stable_tag(tags: list[str]) -> str | None:
    stable = [t for t in tags if STABLE_TAG.fullmatch(t.strip())]
    return max(stable, key=lambda t: _key(t.strip()[1:])) if stable else None


def decide(tags: list[str], head_maf: str, maf_at_tag) -> dict[str, str]:
    """``maf_at_tag`` maps a tag to the .maf-version it shipped (or None)."""
    if not VERSION.fullmatch(head_maf):
        raise ValueError(f"HEAD .maf-version is not X.Y.Z: {head_maf!r}")
    tag = latest_stable_tag(tags)
    if tag is None:
        raise ValueError("no stable vX.Y.Z tag found; cut the first release by hand")
    shipped = maf_at_tag(tag)
    if shipped is not None and VERSION.fullmatch(shipped) and _key(head_maf) <= _key(shipped):
        return {"release": "false", "version": "", "reason": f"{tag} already ships MAF {shipped}"}
    major, minor, _ = _key(tag[1:])
    version = f"{major}.{minor + 1}.0"
    if f"v{version}" in {t.strip() for t in tags}:
        raise ValueError(f"v{version} already exists but does not ship MAF {head_maf}")
    return {"release": "true", "version": version, "reason": f"main covers MAF {head_maf}; {tag} ships MAF {shipped}"}


def _git(repo: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True).stdout


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path("."))
    args = parser.parse_args(argv)

    tags = _git(args.repo, "tag", "--list", "v*").split()
    head_maf = (args.repo / ".maf-version").read_text(encoding="utf-8").strip()

    def maf_at_tag(tag: str) -> str | None:
        try:
            return _git(args.repo, "show", f"{tag}:.maf-version").strip()
        except subprocess.CalledProcessError:
            return None

    result = decide(tags, head_maf, maf_at_tag)
    for key, value in result.items():
        print(f"{key}={value}")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            for key, value in result.items():
                handle.write(f"{key}={value}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
