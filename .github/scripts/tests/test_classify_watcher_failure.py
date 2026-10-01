"""Tests for the self-update failure classifier (ROADMAP O-02)."""
from __future__ import annotations

import json
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import classify_watcher_failure as cwf  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[3]

EVIDENCE_LOG = "\n".join(
    [
        "analyze-and-update\tGate\t2026-10-01T06:24:52.0664844Z ##[error]Release evidence is incomplete; refusing to advance .maf-version or create a watcher commit.",
        "analyze-and-update\tGate\t2026-10-01T06:24:52.0667045Z - durable: no exact or aligned package version exists for train 1.17.0",
        "analyze-and-update\tGate\t2026-10-01T06:24:52.0668608Z - azure-functions: no exact or aligned package version exists for train 1.18.0",
        "analyze-and-update\tGate\t2026-10-01T06:24:52.0715402Z ##[error]Process completed with exit code 1.",
    ]
)


def test_evidence_gap_is_classified_with_surface_lines():
    failure, evidence = cwf.classify(EVIDENCE_LOG)
    assert failure.key == "evidence-gap"
    assert evidence[0].startswith("Release evidence is incomplete")
    assert any(line.startswith("- durable:") for line in evidence)
    assert any(line.startswith("- azure-functions:") for line in evidence)
    assert not any("Process completed" in line for line in evidence)


def test_credential_failure_is_classified():
    log = "2026-08-17T10:00:00Z ::error::COPILOT_ASSIGN_PAT is expired, revoked, or cannot read joslat/maf-doctor."
    assert cwf.classify(log)[0].key == "credential"


def test_unknown_failure_keeps_last_error_lines():
    log = "\n".join(f"2026-01-01T00:00:00Z ##[error]boom {i}" for i in range(30))
    failure, evidence = cwf.classify(log)
    assert failure.key == "unknown"
    assert evidence[-1] == "boom 29"
    assert len(evidence) <= cwf.MAX_EVIDENCE_LINES


def test_code_block_cannot_be_broken_out_of():
    block = cwf.code_block(["```", "```` evil", "## heading"])
    fence = block.split("text", 1)[0]
    assert len(fence) >= 5
    assert block.endswith(fence)


def test_control_characters_and_ansi_are_stripped():
    lines = cwf.clean_lines("2026-01-01T00:00:00Z \x1b[31mred\x1b[0m\x07 text")
    assert lines == ["red text"]


def test_streak_skips_in_progress_and_other_branches():
    runs = [
        {"status": "in_progress", "conclusion": "", "createdAt": "2026-10-08T06:00:00Z", "headBranch": "main"},
        {"status": "completed", "conclusion": "success", "createdAt": "2026-10-02T00:00:00Z", "headBranch": "dry-run"},
        {"status": "completed", "conclusion": "failure", "createdAt": "2026-10-01T06:00:00Z", "headBranch": "main"},
        {"status": "completed", "conclusion": "failure", "createdAt": "2026-09-24T06:00:00Z", "headBranch": "main"},
        {"status": "completed", "conclusion": "success", "createdAt": "2026-08-17T21:00:00Z", "headBranch": "main"},
        {"status": "completed", "conclusion": "failure", "createdAt": "2026-08-01T06:00:00Z", "headBranch": "main"},
    ]
    assert cwf.streak_from_runs(runs) == (3, "2026-09-24T06:00:00Z")


def test_main_renders_body_and_summary(tmp_path):
    log = tmp_path / "failed.log"
    log.write_text(EVIDENCE_LOG, encoding="utf-8")
    runs = tmp_path / "runs.json"
    runs.write_text(json.dumps([
        {"status": "completed", "conclusion": "failure", "createdAt": "2026-08-20T06:00:00Z", "headBranch": "main"},
    ]), encoding="utf-8")
    body, summary = tmp_path / "body.md", tmp_path / "summary.json"
    rc = cwf.main([
        "--log", str(log), "--runs", str(runs), "--workflow", "MAF Release Watcher",
        "--run-url", "https://example.invalid/run/1", "--repo-url", "https://github.com/o/r",
        "--now", "2026-10-01T06:00:00Z", "--body-out", str(body), "--summary-out", str(summary),
    ])
    assert rc == 0
    text = body.read_text(encoding="utf-8")
    assert "`evidence-gap`" in text
    assert "| Consecutive failed runs | **2** |" in text
    assert "| Failing since | 2026-08-20 |" in text
    assert "docs/runbooks/self-update.md#evidence-gap" in text
    assert json.loads(summary.read_text(encoding="utf-8"))["streak"] == 2


def test_missing_log_still_produces_an_unknown_issue(tmp_path):
    body, summary = tmp_path / "body.md", tmp_path / "summary.json"
    rc = cwf.main([
        "--log", str(tmp_path / "absent.log"), "--workflow", "W", "--run-url", "u",
        "--repo-url", "r", "--now", "2026-10-01T00:00:00Z",
        "--body-out", str(body), "--summary-out", str(summary),
    ])
    assert rc == 0
    assert json.loads(summary.read_text(encoding="utf-8"))["class"] == "unknown"


def test_every_failure_class_has_a_runbook_section():
    runbook = (REPO_ROOT / cwf.RUNBOOK).read_text(encoding="utf-8")
    for failure in (*cwf.CLASSES, cwf.UNKNOWN):
        assert f"\n## {failure.key}\n" in runbook, failure.key
