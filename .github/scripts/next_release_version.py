#!/usr/bin/env python3
"""Decide whether main needs a new maf-doctor release (ROADMAP A-03, R-05).

``--mode train`` (default, on a ``.maf-version`` push): a release is due when
``.maf-version`` on HEAD covers a newer MAF than the one shipped by the latest
stable ``vX.Y.Z`` tag. Each MAF train becomes one maf-doctor minor release.

``--mode product`` (weekly, from maf-freshness): also due when the latest
release is at least ``PRODUCT_MIN_AGE_DAYS`` old and ``main`` has commits since
it that touch files shipped in the packages. Without it, product fixes waited
for Microsoft's next MAF release (v1.17.0 needed a manual tag).

The next version bumps the minor. Writes ``release``, ``version`` and
``reason`` to ``$GITHUB_OUTPUT`` when it is set.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import time
from pathlib import Path

STABLE_TAG = re.compile(r"v(\d+)\.(\d+)\.(\d+)")
VERSION = re.compile(r"\d+\.\d+\.\d+")

PRODUCT_MIN_AGE_DAYS = 30
# Everything that ends up in the maf-doctor or maf-doctor.Analyzers packages
# (see the csproj EmbeddedResource / Pack items), plus central dependency pins.
SHIPPED_PATHS = (
    "src/maf-autopilot/",
    "src/maf-autopilot.Analyzers/",
    "Directory.Packages.props",
    "Directory.Build.props",
    "NUGET_README.md",
    "docs/steering/",
    "docs/migration/",
    "guides/maf-current-migration-guide.md",
    ".github/skills/",
    ".github/instructions/maf-constraints.instructions.md",
)


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


def decide_product(tags: list[str], head_maf: str, maf_at_tag, tag_age_days: int, shipped_commits: int) -> dict[str, str]:
    """Train decision first; otherwise release product changes once the last release is old enough.

    ``tag_age_days`` is the age of the latest stable tag's commit; ``shipped_commits``
    counts commits since that tag touching ``SHIPPED_PATHS``.
    """
    train = decide(tags, head_maf, maf_at_tag)
    if train["release"] == "true":
        return train
    tag = latest_stable_tag(tags)
    if shipped_commits <= 0:
        return {"release": "false", "version": "", "reason": f"no shipped changes since {tag}"}
    if tag_age_days < PRODUCT_MIN_AGE_DAYS:
        return {"release": "false", "version": "",
                "reason": f"{shipped_commits} shipped change(s) since {tag}, but it is only {tag_age_days} day(s) old (< {PRODUCT_MIN_AGE_DAYS})"}
    major, minor, _ = _key(tag[1:])
    version = f"{major}.{minor + 1}.0"
    if f"v{version}" in {t.strip() for t in tags}:
        raise ValueError(f"v{version} already exists")
    return {"release": "true", "version": version,
            "reason": f"{shipped_commits} commit(s) touching shipped files since {tag} ({tag_age_days} days old); still MAF {head_maf}"}


def _git(repo: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True).stdout


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path("."))
    parser.add_argument("--mode", choices=("train", "product"), default="train")
    args = parser.parse_args(argv)

    tags = _git(args.repo, "tag", "--list", "v*").split()
    head_maf = (args.repo / ".maf-version").read_text(encoding="utf-8").strip()

    def maf_at_tag(tag: str) -> str | None:
        try:
            return _git(args.repo, "show", f"{tag}:.maf-version").strip()
        except subprocess.CalledProcessError:
            return None

    if args.mode == "train":
        result = decide(tags, head_maf, maf_at_tag)
    else:
        tag = latest_stable_tag(tags)
        if tag is None:
            raise ValueError("no stable vX.Y.Z tag found; cut the first release by hand")
        tag_time = int(_git(args.repo, "log", "-1", "--format=%ct", f"{tag}^{{commit}}").strip())
        age_days = int((time.time() - tag_time) // 86400)
        commits = _git(args.repo, "rev-list", f"{tag}..HEAD", "--", *SHIPPED_PATHS).split()
        result = decide_product(tags, head_maf, maf_at_tag, age_days, len(commits))
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
