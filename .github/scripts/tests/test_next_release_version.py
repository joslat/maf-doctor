"""Tests for the automatic-release decision (ROADMAP A-03)."""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import next_release_version as nrv  # noqa: E402

TAGS = ["v1.9.0", "v1.15.0", "v1.16.0", "v1.3.0-alpha-5", "1.2.1", "v1.10.0"]
SHIPPED = {"v1.16.0": "1.23.0", "v1.15.0": "1.17.0"}


def test_latest_stable_tag_is_semantic_not_lexical():
    assert nrv.latest_stable_tag(TAGS) == "v1.16.0"
    assert nrv.latest_stable_tag(["v1.9.0", "v1.10.0"]) == "v1.10.0"


def test_new_maf_train_on_main_releases_the_next_minor():
    result = nrv.decide(TAGS, "1.24.0", SHIPPED.get)
    assert result["release"] == "true" and result["version"] == "1.17.0"


def test_nothing_new_since_the_last_release_is_a_no_op():
    result = nrv.decide(TAGS, "1.23.0", SHIPPED.get)
    assert result["release"] == "false" and "already ships MAF 1.23.0" in result["reason"]


def test_a_newer_tag_that_already_ships_head_is_a_no_op():
    shipped = {**SHIPPED, "v1.17.0": "1.24.0"}
    result = nrv.decide(TAGS + ["v1.17.0"], "1.24.0", shipped.get)
    assert result["release"] == "false"


def test_latest_tag_with_unknown_maf_version_still_releases_the_next_minor():
    result = nrv.decide(TAGS + ["v1.17.0"], "1.24.0", SHIPPED.get)
    assert result["release"] == "true" and result["version"] == "1.18.0"


def test_malformed_head_version_is_rejected():
    with pytest.raises(ValueError, match="X.Y.Z"):
        nrv.decide(TAGS, "1.24", SHIPPED.get)


def test_no_stable_tag_requires_a_manual_first_release():
    with pytest.raises(ValueError, match="by hand"):
        nrv.decide(["v1.3.0-alpha-5"], "1.24.0", lambda tag: None)


# --- R-05: product releases when MAF is quiet --------------------------------

def test_product_mode_releases_shipped_changes_after_thirty_days():
    result = nrv.decide_product(TAGS, "1.23.0", SHIPPED.get, tag_age_days=31, shipped_commits=4)
    assert result["release"] == "true" and result["version"] == "1.17.0"
    assert "4 commit(s)" in result["reason"] and "still MAF 1.23.0" in result["reason"]


def test_product_mode_waits_until_the_last_release_is_old_enough():
    result = nrv.decide_product(TAGS, "1.23.0", SHIPPED.get, tag_age_days=29, shipped_commits=4)
    assert result["release"] == "false" and "only 29 day(s) old" in result["reason"]


def test_product_mode_without_shipped_changes_is_a_no_op():
    result = nrv.decide_product(TAGS, "1.23.0", SHIPPED.get, tag_age_days=90, shipped_commits=0)
    assert result["release"] == "false" and "no shipped changes" in result["reason"]


def test_product_mode_still_releases_a_missed_train_immediately():
    result = nrv.decide_product(TAGS, "1.24.0", SHIPPED.get, tag_age_days=1, shipped_commits=0)
    assert result["release"] == "true" and "main covers MAF 1.24.0" in result["reason"]


def test_shipped_paths_cover_every_packed_file():
    # Each path the csproj packs or embeds must fall under SHIPPED_PATHS, or a
    # change to it would never trigger a product release.
    root = SCRIPT_DIR.parents[1]
    csproj = (root / "src" / "maf-autopilot" / "maf-autopilot.csproj").read_text(encoding="utf-8")
    import re
    packed = re.findall(r'Include="\.\.\\\.\.\\([^"]+)"', csproj)
    assert packed, "no packed/embedded files found in the csproj"
    for path in packed:
        posix = path.replace("\\", "/")
        assert posix.startswith(nrv.SHIPPED_PATHS) or posix.startswith("assets/"), posix


def _git(repo: Path, *args: str) -> None:
    subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True, check=True)


def test_product_mode_runs_against_a_tagged_repository(tmp_path, capsys, monkeypatch):
    # A throwaway repo, not this one: CI's lint checkout is shallow and has no tags
    # (the workflows that run product mode check out with fetch-depth: 0).
    monkeypatch.delenv("GITHUB_OUTPUT", raising=False)
    _git(tmp_path, "init")
    _git(tmp_path, "config", "user.email", "tests@example.invalid")
    _git(tmp_path, "config", "user.name", "Tests")
    (tmp_path / ".maf-version").write_text("1.23.0\n", encoding="utf-8")
    _git(tmp_path, "add", ".maf-version")
    _git(tmp_path, "commit", "-m", "release")
    _git(tmp_path, "tag", "v1.17.0")
    assert nrv.main(["--repo", str(tmp_path), "--mode", "product"]) == 0
    assert "release=false" in capsys.readouterr().out.splitlines()
