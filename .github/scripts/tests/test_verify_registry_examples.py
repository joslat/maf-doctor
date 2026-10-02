"""Tests for the registry compiler oracle wrapper (ROADMAP Q-02, oracle 2b)."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import verify_registry_examples as vre  # noqa: E402


def test_only_maf_package_ids_and_versions_are_restored():
    ok = {"Microsoft.Agents.AI": "1.23.0", "Microsoft.Agents.AI.Foundry": "1.23.0-preview.260928.1"}
    assert vre.validate(ok) == ok
    for bad in ({"Contoso.Agents": "1.0.0"}, {"Microsoft.Agents.AI": "latest"},
                {"Microsoft.Agents.AI\" /><Import Project=\"x": "1.0.0"}):
        with pytest.raises(ValueError, match="will not restore"):
            vre.validate(bad)


def test_project_pins_exact_versions_outside_the_repository_props():
    xml = vre.project_xml({"Microsoft.Agents.AI": "1.23.0"}, "net10.0")
    assert '<PackageReference Include="Microsoft.Agents.AI" Version="[1.23.0]" />' in xml
    assert "<TargetFramework>net10.0</TargetFramework>" in xml


def test_reference_pack_picks_the_newest_matching_major(tmp_path: Path):
    for version in ("8.0.10", "10.0.2", "10.0.11", "9.0.9"):
        (tmp_path / "packs" / "Microsoft.NETCore.App.Ref" / version / "ref" / f"net{version.split('.')[0]}.0").mkdir(parents=True)
    pack = vre.reference_pack(tmp_path, "Microsoft.NETCore.App", "net10.0")
    assert pack is not None and pack.parts[-3] == "10.0.11"
    assert vre.pick_tfm(tmp_path) == "net10.0"
    assert vre.reference_pack(tmp_path, "Microsoft.AspNetCore.App", "net10.0") is None


def test_assets_references_lists_compile_assets_and_framework_packs(tmp_path: Path):
    packages = tmp_path / "nuget"
    dll = packages / "microsoft.agents.ai" / "1.23.0" / "lib" / "net10.0" / "Microsoft.Agents.AI.dll"
    dll.parent.mkdir(parents=True)
    dll.write_bytes(b"")
    for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        pack = tmp_path / "dotnet" / "packs" / f"{framework}.Ref" / "10.0.11" / "ref" / "net10.0"
        pack.mkdir(parents=True)
        (pack / f"{framework}.Stub.dll").write_bytes(b"")
    assets = {
        "packageFolders": {str(packages): {}},
        "libraries": {"Microsoft.Agents.AI/1.23.0": {"path": "microsoft.agents.ai/1.23.0"},
                      "Empty/1.0.0": {"path": "empty/1.0.0"}},
        "targets": {"net10.0": {
            "Microsoft.Agents.AI/1.23.0": {"type": "package", "compile": {"lib/net10.0/Microsoft.Agents.AI.dll": {}},
                                           "frameworkReferences": ["Microsoft.AspNetCore.App"]},
            "Empty/1.0.0": {"type": "package", "compile": {"lib/net10.0/_._": {}}},
        }},
        "project": {"frameworks": {"net10.0": {}}},
    }
    paths = vre.assets_references(assets, "net10.0", tmp_path / "dotnet")
    names = [Path(p).name for p in paths]
    assert names == ["Microsoft.Agents.AI.dll", "Microsoft.AspNetCore.App.Stub.dll", "Microsoft.NETCore.App.Stub.dll"]


def test_missing_train_lock_entry_cannot_run(tmp_path: Path, capsys):
    lock = tmp_path / "lock.json"
    lock.write_text(json.dumps({"trains": {"1.22.0": {"Microsoft.Agents.AI": "1.22.0"}}}), encoding="utf-8")
    assert vre.main(["--old-version", "1.22.0", "--version", "1.23.0", "--train-lock", str(lock)]) == 2
    assert "no entry" in capsys.readouterr().err
