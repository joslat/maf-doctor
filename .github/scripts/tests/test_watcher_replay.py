"""Replay harness: every adjacent MAF train must plan cleanly offline.

The 2026-08-20 → 10-01 watcher stall happened because a lifecycle exception
only matched the single release it was declared for. Unit tests passed; the
first real train after it (1.18.0) failed the evidence gate seven weeks in a
row. This harness replays every adjacent stable train against a recorded
nuget.org snapshot with the checked-in manifest and train lock, so any manifest,
lock, or planner change that would strand a past or pending train fails CI.

Refresh the snapshot when MAF ships new trains: run the planner live and
re-capture ``fixtures/nuget-index-snapshot-<date>.json`` (see its ``note``).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import build_maf_package_plan as planner  # noqa: E402
import maf_package_evidence as evidence  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[3]
SNAPSHOT = Path(__file__).resolve().parent / "fixtures" / "nuget-index-snapshot-2026-10-01.json"
EXTERNALIZED = {"durable", "azure-functions"}


def _key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))


def _snapshot() -> dict[str, list[str]]:
    return json.loads(SNAPSHOT.read_text(encoding="utf-8"))["indexes"]


def _trains() -> list[str]:
    stable = [v for v in _snapshot()["Microsoft.Agents.AI"] if "-" not in v]
    return sorted(stable, key=_key)


def _pairs() -> list[tuple[str, str]]:
    trains = _trains()
    return list(zip(trains, trains[1:]))


def _plan(old: str, new: str) -> dict:
    indexes = _snapshot()
    lock = planner.load_train_lock(planner.DEFAULT_TRAIN_LOCK)
    processed = new in lock["trains"]
    return planner.build_plan(
        planner.load_manifest(planner.DEFAULT_MANIFEST),
        old,
        new,
        fetch_versions=lambda package: indexes[package],
        train_lock=lock,
        # Already-processed trains replay exactly what was recorded; pending
        # trains use the live resolution rules the watcher will apply.
        lock_new_side=processed,
    )


def test_snapshot_covers_the_pending_backlog():
    current = (REPO_ROOT / ".maf-version").read_text(encoding="utf-8").strip()
    trains = _trains()
    assert current in trains
    assert trains[-1] != current, "snapshot has no pending train; refresh it"


@pytest.mark.parametrize(("old", "new"), _pairs(), ids=lambda v: v)
def test_every_adjacent_train_plans_without_unverifiable_surfaces(old, new, tmp_path):
    plan = _plan(old, new)

    unverifiable = {
        s["slug"]: s["reason"] for s in plan["surfaces"] if s["status"] == "unverifiable"
    }
    assert unverifiable == {}

    # The plan must also satisfy the downstream evidence contract.
    path = tmp_path / "maf-package-plan.json"
    path.write_text(planner.render_plan(plan), encoding="utf-8")
    evidence.load_package_plan(path)


@pytest.mark.parametrize(
    ("old", "new"), [p for p in _pairs() if _key(p[1]) > (1, 17, 0)], ids=lambda v: v
)
def test_externalized_surfaces_stay_informational_after_1_17(old, new):
    plan = _plan(old, new)
    by_slug = {s["slug"]: s for s in plan["surfaces"]}
    for slug in EXTERNALIZED:
        assert by_slug[slug]["status"] in {"informational", "diffable"}
    assert all(e["release_version"] == new for e in plan["lifecycle_events"])
    diffable = [s for s in plan["surfaces"] if s["status"] == "diffable"]
    assert len(diffable) >= 8
