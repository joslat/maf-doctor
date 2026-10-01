#!/usr/bin/env python3
"""Record the exact package versions a processed MAF train resolved to.

The release watcher runs this after the complete-evidence gate passes. It adds
``trains[<new_version>]`` to ``.github/maf-train-lock.json`` from the package
plan's resolved ``new_package_version`` values, so the *next* train diffs from
exactly what this train diffed, even if upstream later republishes another
prerelease for the same train.

A recorded train is immutable: re-recording identical values is a no-op, and
any difference fails closed instead of silently rewriting history.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_maf_package_plan import (  # noqa: E402
    DEFAULT_TRAIN_LOCK,
    SCHEMA_VERSION,
    TrainLockError,
    load_train_lock,
)

RECORDED_STATUSES = {"diffable", "informational"}


def lock_entry_from_plan(plan: dict) -> tuple[str, dict[str, str]]:
    """Return ``(new_version, {package: version})`` for the plan's resolved surfaces."""

    release = plan.get("release")
    if not isinstance(release, dict) or not isinstance(release.get("new_version"), str):
        raise TrainLockError("package plan has no release.new_version")
    entry: dict[str, str] = {}
    for surface in plan.get("surfaces", []):
        version = surface.get("new_package_version")
        if surface.get("status") in RECORDED_STATUSES and isinstance(version, str):
            entry[surface["package"]] = version
    if not entry:
        raise TrainLockError("package plan resolved no new package versions to record")
    return release["new_version"], dict(sorted(entry.items(), key=lambda kv: kv[0].casefold()))


def record(lock: dict, train: str, entry: dict[str, str]) -> bool:
    """Add ``entry`` for ``train``; return True when the lock changed."""

    trains = lock.setdefault("trains", {})
    existing = trains.get(train)
    if existing is not None:
        if existing == entry:
            return False
        raise TrainLockError(
            f"train {train} is already recorded with different versions; "
            "recorded trains are immutable"
        )
    trains[train] = entry
    return True


def render(lock: dict) -> str:
    def train_key(version: str) -> tuple[int, ...]:
        return tuple(int(part) for part in version.split("."))

    ordered = {
        "schema_version": SCHEMA_VERSION,
        "description": lock.get("description", ""),
        "trains": {
            train: lock["trains"][train]
            for train in sorted(lock.get("trains", {}), key=train_key)
        },
    }
    return json.dumps(ordered, indent=2, ensure_ascii=False) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, default=Path("maf-package-plan.json"))
    parser.add_argument("--lock", type=Path, default=DEFAULT_TRAIN_LOCK)
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print the updated lock without writing it",
    )
    args = parser.parse_args(argv)
    try:
        plan = json.loads(args.plan.read_text(encoding="utf-8"))
        lock = load_train_lock(args.lock)
        train, entry = lock_entry_from_plan(plan)
        changed = record(lock, train, entry)
    except (OSError, json.JSONDecodeError, TrainLockError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    rendered = render(lock)
    if args.dry_run:
        sys.stdout.write(rendered)
        return 0
    if changed:
        args.lock.write_text(rendered, encoding="utf-8")
        print(f"Recorded MAF {train} ({len(entry)} packages) in {args.lock}")
    else:
        print(f"MAF {train} already recorded with identical versions; lock unchanged")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
