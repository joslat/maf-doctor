"""Tests for the MAF knowledge freshness SLO (ROADMAP O-01)."""
from __future__ import annotations

import datetime as dt
import json
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import check_freshness as cf  # noqa: E402

RELEASES = [
    ("1.17.0", dt.date(2026, 8, 4)),
    ("1.18.0", dt.date(2026, 8, 18)),
    ("1.19.0", dt.date(2026, 8, 22)),
    ("1.22.0", dt.date(2026, 9, 18)),
    ("1.23.0", dt.date(2026, 9, 29)),
]


def test_current_registry_is_within_slo():
    status = cf.evaluate("main", "1.23.0", RELEASES, dt.date(2026, 10, 1), max_lag=1, max_age_days=10)
    assert status.ok and status.behind == []


def test_one_fresh_release_behind_is_within_slo():
    status = cf.evaluate("main", "1.22.0", RELEASES, dt.date(2026, 10, 1), max_lag=1, max_age_days=10)
    assert status.ok and len(status.behind) == 1 and status.oldest_uncovered_age_days == 2


def test_too_many_releases_behind_breaches():
    status = cf.evaluate("shipped", "1.17.0", RELEASES, dt.date(2026, 10, 1), max_lag=1, max_age_days=10)
    assert not status.ok
    assert len(status.behind) == 4
    assert status.oldest_uncovered_age_days == 44


def test_one_release_behind_but_old_breaches():
    status = cf.evaluate("main", "1.22.0", RELEASES, dt.date(2026, 10, 20), max_lag=1, max_age_days=10)
    assert not status.ok and status.oldest_uncovered_age_days == 21


def _releases_file(tmp_path: Path) -> Path:
    path = tmp_path / "releases.json"
    path.write_text(json.dumps([[v, d.isoformat()] for v, d in RELEASES]), encoding="utf-8")
    return path


def test_main_reports_breach_with_exit_1(tmp_path):
    report = tmp_path / "report.md"
    rc = cf.main([
        "--main-version", "1.21.0", "--shipped-version", "1.17.0", "--today", "2026-10-01",
        "--releases-json", str(_releases_file(tmp_path)), "--report-out", str(report),
    ])
    assert rc == 1
    text = report.read_text(encoding="utf-8")
    assert "SLO breached" in text and "| shipped | 1.17.0 | 4 |" in text


def test_main_healthy_exits_0(tmp_path):
    report = tmp_path / "report.md"
    rc = cf.main([
        "--main-version", "1.23.0", "--shipped-version", "1.23.0", "--today", "2026-10-01",
        "--releases-json", str(_releases_file(tmp_path)), "--report-out", str(report),
    ])
    assert rc == 0 and "within SLO" in report.read_text(encoding="utf-8")


def test_invalid_version_input_is_rejected(tmp_path):
    rc = cf.main(["--main-version", "", "--report-out", str(tmp_path / "r.md"), "--releases-json", str(_releases_file(tmp_path))])
    assert rc == 2


def test_unreadable_upstream_is_never_reported_fresh(tmp_path):
    report = tmp_path / "r.md"
    bad = tmp_path / "bad.json"
    bad.write_text("not json", encoding="utf-8")
    rc = cf.main(["--main-version", "1.23.0", "--releases-json", str(bad), "--report-out", str(report)])
    assert rc == 2 and "Freshness unknown" in report.read_text(encoding="utf-8")
