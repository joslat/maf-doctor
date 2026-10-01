"""Tests for MAF package auto-discovery (ROADMAP Z-06)."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import discover_maf_packages as dmp  # noqa: E402

MANIFEST = {
    "surfaces": [
        {"slug": "core", "package": "Microsoft.Agents.AI"},
        {"slug": "mcp", "package": "Microsoft.Agents.AI.Mcp"},
    ],
    "lifecycle_events": [
        {"source_packages": [{"package": "Microsoft.Agents.AI.AGUI", "version": "1.13.0"}],
         "target_packages": [{"package": "AGUI.Client", "version": "0.0.3"}]},
    ],
}
DISCOVERY = {"ignored": {"Microsoft.Agents.AI.AzureAI": "Pre-GA name, renamed to Foundry."}}


def _pkg(package: str, version: str = "1.0.0", verified: bool = True, owners=("Microsoft",)) -> dict:
    return {"id": package, "version": version, "verified": verified, "owners": list(owners)}


SEARCH = [
    _pkg("Microsoft.Agents.AI", "1.23.0"),
    _pkg("Microsoft.Agents.AI.Mcp", "1.23.0"),
    _pkg("Microsoft.Agents.AI.AGUI", "1.13.0-preview"),
    _pkg("Microsoft.Agents.AI.AzureAI", "1.0.0-rc5"),
    _pkg("Microsoft.Agents.AI.Brand.New", "1.24.0-preview.1"),
    _pkg("Microsoft.Agents.AI.Squatter", verified=False, owners=("someone",)),
    _pkg("Microsoft.Agents.AI.NotMicrosoft", owners=("someone",)),
    _pkg("Microsoft.Agents.Builder", "2.0.0"),
    _pkg("Contoso.Microsoft.Agents.AI", "1.0.0"),
]


def test_only_verified_microsoft_packages_under_the_prefix_are_candidates():
    assert set(dmp.candidates(SEARCH)) == {
        "Microsoft.Agents.AI",
        "Microsoft.Agents.AI.Mcp",
        "Microsoft.Agents.AI.AGUI",
        "Microsoft.Agents.AI.AzureAI",
        "Microsoft.Agents.AI.Brand.New",
    }


def test_surfaces_lifecycle_packages_and_ignored_packages_are_accounted_for():
    new = dmp.untracked(dmp.candidates(SEARCH), dmp.accounted_for(MANIFEST, DISCOVERY))
    assert new == {"Microsoft.Agents.AI.Brand.New": "1.24.0-preview.1"}


def test_ignored_package_without_a_reason_is_rejected():
    with pytest.raises(ValueError, match="reason"):
        dmp.accounted_for(MANIFEST, {"ignored": {"Microsoft.Agents.AI.AzureAI": " "}})


def _files(tmp_path: Path, search: list[dict]) -> list[str]:
    paths = {}
    for name, content in (("manifest", MANIFEST), ("discovery", DISCOVERY), ("search", search)):
        path = tmp_path / f"{name}.json"
        path.write_text(json.dumps(content), encoding="utf-8")
        paths[name] = str(path)
    return [
        "--manifest", paths["manifest"], "--discovery", paths["discovery"],
        "--search-json", paths["search"], "--report-out", str(tmp_path / "report.md"),
    ]


def test_new_package_exits_1_with_an_actionable_report(tmp_path):
    assert dmp.main(_files(tmp_path, SEARCH)) == 1
    report = (tmp_path / "report.md").read_text(encoding="utf-8")
    assert "`Microsoft.Agents.AI.Brand.New`" in report
    assert "maf-package-surfaces.json" in report and "maf-package-discovery.json" in report


def test_everything_accounted_for_exits_0(tmp_path):
    search = [p for p in SEARCH if p["id"] != "Microsoft.Agents.AI.Brand.New"]
    assert dmp.main(_files(tmp_path, search)) == 0
    assert "✅" in (tmp_path / "report.md").read_text(encoding="utf-8")


def test_empty_upstream_is_never_reported_as_all_tracked(tmp_path):
    assert dmp.main(_files(tmp_path, [])) == 2
    assert "unknown" in (tmp_path / "report.md").read_text(encoding="utf-8")


def test_live_config_files_are_valid():
    manifest = json.loads(dmp.DEFAULT_MANIFEST.read_text(encoding="utf-8"))
    discovery = json.loads(dmp.DEFAULT_DISCOVERY.read_text(encoding="utf-8"))
    known = dmp.accounted_for(manifest, discovery)
    assert "microsoft.agents.ai" in known and len(known) >= 35
