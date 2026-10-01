"""Tests for recording processed MAF trains in the train lock."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import build_maf_package_plan as planner  # noqa: E402
import update_train_lock as lock_tool  # noqa: E402


def _plan(new_version: str = "1.18.0") -> dict:
    return {
        "schema_version": 1,
        "release": {"old_version": "1.17.0", "new_version": new_version},
        "surfaces": [
            {"slug": "core", "package": "Microsoft.Agents.AI", "status": "diffable",
             "new_package_version": new_version},
            {"slug": "hosting", "package": "Microsoft.Agents.AI.Hosting", "status": "diffable",
             "new_package_version": f"{new_version}-preview.260818.1"},
            {"slug": "durable", "package": "Microsoft.Agents.AI.DurableTask",
             "status": "informational", "new_package_version": None},
            {"slug": "harness", "package": "Microsoft.Agents.AI.Harness",
             "status": "unverifiable", "new_package_version": f"{new_version}"},
        ],
        "lifecycle_events": [],
    }


def _write(tmp_path: Path, payload: dict) -> Path:
    path = tmp_path / "plan.json"
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def test_entry_records_only_resolved_trusted_surfaces():
    train, entry = lock_tool.lock_entry_from_plan(_plan())
    assert train == "1.18.0"
    assert entry == {
        "Microsoft.Agents.AI": "1.18.0",
        "Microsoft.Agents.AI.Hosting": "1.18.0-preview.260818.1",
    }


def test_main_writes_new_train_and_is_idempotent(tmp_path):
    lock_path = tmp_path / "lock.json"
    plan_path = _write(tmp_path, _plan())
    assert lock_tool.main(["--plan", str(plan_path), "--lock", str(lock_path)]) == 0
    first = lock_path.read_text(encoding="utf-8")
    assert planner.load_train_lock(lock_path)["trains"]["1.18.0"]["Microsoft.Agents.AI"] == "1.18.0"
    assert lock_tool.main(["--plan", str(plan_path), "--lock", str(lock_path)]) == 0
    assert lock_path.read_text(encoding="utf-8") == first


def test_recorded_train_is_immutable(tmp_path):
    lock_path = tmp_path / "lock.json"
    lock_tool.main(["--plan", str(_write(tmp_path, _plan())), "--lock", str(lock_path)])
    changed = _plan()
    changed["surfaces"][1]["new_package_version"] = "1.18.0-preview.260901.1"
    assert lock_tool.main(["--plan", str(_write(tmp_path, changed)), "--lock", str(lock_path)]) == 1
    assert "260818" in lock_path.read_text(encoding="utf-8")


def test_render_orders_trains_numerically():
    lock = {"schema_version": 1, "trains": {
        "1.10.0": {"A": "1.10.0"}, "1.9.0": {"A": "1.9.0"}, "1.18.0": {"A": "1.18.0"}}}
    rendered = json.loads(lock_tool.render(lock))
    assert list(rendered["trains"]) == ["1.9.0", "1.10.0", "1.18.0"]


def test_plan_without_resolved_versions_is_rejected():
    plan = _plan()
    for surface in plan["surfaces"]:
        surface["status"] = "unverifiable"
    with pytest.raises(planner.TrainLockError):
        lock_tool.lock_entry_from_plan(plan)


def test_checked_in_lock_is_valid_and_covers_current_train():
    lock = planner.load_train_lock(planner.DEFAULT_TRAIN_LOCK)
    current = (Path(__file__).resolve().parents[3] / ".maf-version").read_text(encoding="utf-8").strip()
    assert current in lock["trains"]
    # The 1.16.0 durable packages were republished after externalization; the
    # lock must keep the pre-externalization build the 1.17 event recorded.
    assert lock["trains"]["1.16.0"]["Microsoft.Agents.AI.DurableTask"] == "1.16.0-preview.260730.1"
