"""R-03 guard: the shipped analyzer stays on Roslyn 4.x.

The analyzer (maf-doctor.Analyzers) is loaded by the USER's compiler, so it
must not reference a newer Roslyn than the oldest SDK/IDE it supports. The
tool moved to Roslyn 5.x for C# 14 parsing; the analyzer is pinned with
VersionOverride. A central-version bump alone must never move it.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ANALYZER = ROOT / "src" / "maf-autopilot.Analyzers" / "maf-autopilot.Analyzers.csproj"


def _references() -> dict[str, dict[str, str]]:
    tree = ET.parse(ANALYZER)
    return {
        element.get("Include", ""): dict(element.attrib)
        for element in tree.iter()
        if element.tag.endswith("PackageReference")
    }


def test_analyzer_pins_roslyn_4x_explicitly():
    reference = _references()["Microsoft.CodeAnalysis.CSharp"]
    version = reference.get("VersionOverride", "")
    assert version.startswith("4."), f"analyzer Roslyn must stay on 4.x, got {version!r}"
    assert reference.get("PrivateAssets", "").lower() == "all"


def test_analyzer_authoring_analyzers_match_the_roslyn_4x_line():
    version = _references()["Microsoft.CodeAnalysis.Analyzers"].get("VersionOverride", "")
    assert version.startswith("3."), f"expected the 3.x authoring analyzers, got {version!r}"
