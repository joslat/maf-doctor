"""Tests for verify_crossfile_consistency.py helpers (tasks 4.2 / 4.4)."""
from __future__ import annotations

import datetime as dt
import sys
from pathlib import Path

import pytest

SCRIPT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIR))

import verify_crossfile_consistency as v  # noqa: E402


# --- Task 4.2: union across all per-version guides, not just 1.3.0 -----------

def test_collect_all_guide_sections_unions_versions(tmp_path):
    guides = tmp_path / "guides"
    guides.mkdir()
    (guides / "maf-1.3.0-migration-guide.md").write_text("## 1. Intro\n## 2. Foo\n", encoding="utf-8")
    (guides / "maf-1.6.1-migration-guide.md").write_text("## 7. NewThing\n", encoding="utf-8")
    # The cumulative file is excluded (regenerated superset).
    (guides / "maf-current-migration-guide.md").write_text("## 99. Cumulative\n", encoding="utf-8")

    sections = v.collect_all_guide_sections(guides)
    assert {"1", "2", "7"} <= sections   # the 4.2 fix: 1.6.1's "7" is now valid
    assert "99" not in sections          # cumulative file skipped


def test_collect_all_guide_sections_missing_dir():
    assert v.collect_all_guide_sections(Path("/no/such/dir")) == set()


# --- singular current-cycle compatibility metadata -------------------------

def _metadata_text(*, tracked: tuple[str, ...], dates: tuple[str, ...]) -> str:
    return "\n".join(
        [f"Current tracked version: **`{version}`**" for version in tracked]
        + [f"<!-- last-updated: {value} -->" for value in dates]
    )


def test_exact_current_cycle_metadata_passes():
    today = dt.date(2026, 8, 17)
    text = _metadata_text(tracked=("1.14.0",), dates=("2026-07-18",))

    assert v.check_compatibility_metadata(
        text, "1.14.0", today=today
    ) == []


@pytest.mark.parametrize(
    ("tracked", "expected"),
    [
        ((), "0 valid value(s) across 0 field(s)"),
        (("1.14.0", "1.14.0"), "2 valid value(s) across 2 field(s)"),
        (("1.14.0", "unknown"), "1 valid value(s) across 2 field(s)"),
    ],
)
def test_tracking_metadata_requires_exactly_one_occurrence(tracked, expected):
    text = _metadata_text(tracked=tracked, dates=("2026-08-17",))

    findings = v.check_compatibility_metadata(
        text, "1.14.0", today=dt.date(2026, 8, 17)
    )

    assert any(expected in finding for finding in findings)


def test_tracking_metadata_must_match_maf_version():
    text = _metadata_text(tracked=("1.13.0",), dates=("2026-08-17",))

    findings = v.check_compatibility_metadata(
        text, "1.14.0", today=dt.date(2026, 8, 17)
    )

    assert any(".maf-version is `1.14.0`" in finding for finding in findings)


@pytest.mark.parametrize(
    ("dates", "expected"),
    [
        ((), "found 0"),
        (("2026-08-17", "2026-08-17"), "2 valid value(s) across 2 field(s)"),
        (("2026-08-17", "yesterday"), "1 valid value(s) across 2 field(s)"),
        (("2026-02-31",), "not a valid calendar date"),
        (("2026-07-17",), "31 days old"),
        (("2026-08-18",), "in the future"),
    ],
)
def test_last_updated_requires_one_valid_current_cycle_date(dates, expected):
    text = _metadata_text(tracked=("1.14.0",), dates=dates)

    findings = v.check_compatibility_metadata(
        text, "1.14.0", today=dt.date(2026, 8, 17)
    )

    assert any(expected in finding for finding in findings)


# --- current guide and cumulative-guide contracts --------------------------

def _write_current_guide(tmp_path: Path, generated: str) -> Path:
    guide = tmp_path / "maf-1.12.0-migration-guide.md"
    guide.write_text(
        f"# MAF 1.12.0\n\n{v.AUTO_START}\n{generated}\n{v.AUTO_END}\n"
        "\n## Human additions\n",
        encoding="utf-8",
    )
    return guide


COMPLETE_GUIDE_SECTIONS = """## Breaking Changes (requires human verification)

None detected in the validated public API diffs.

## New Patterns

- Continue using the supported agent construction pattern.

## Obsolete APIs Added

None detected in the validated public API diffs.

## Known Misalignments

None documented yet.
"""

UPSTREAM_FENCE_START = (
    "<<<BEGIN_USER_DATA_0123456789abcdef0123456789abcdef_"
    "UPSTREAM-MAF-DIFF-CORE>>>"
)
UPSTREAM_FENCE_END = (
    "<<<END_USER_DATA_0123456789abcdef0123456789abcdef_"
    "UPSTREAM-MAF-DIFF-CORE>>>"
)


def _fenced_upstream(body: str, *, close: bool = True) -> str:
    ending = f"\n{UPSTREAM_FENCE_END}" if close else ""
    return f"{UPSTREAM_FENCE_START}\n{body}{ending}"


def test_unresolved_generated_guide_is_flagged(tmp_path):
    guide = _write_current_guide(
        tmp_path,
        "## Breaking Changes\n\n<!-- TODO: review the package evidence -->",
    )
    findings = v.check_current_guide_contract(guide)
    assert any("<!-- TODO:" in finding for finding in findings)


def test_resolved_generated_guide_passes(tmp_path):
    guide = _write_current_guide(
        tmp_path,
        COMPLETE_GUIDE_SECTIONS,
    )
    assert v.check_current_guide_contract(guide) == []


def test_package_evidence_headings_and_todos_are_ignored(tmp_path):
    evidence = _fenced_upstream(
        """## Breaking Changes
<!-- TODO: this is untrusted upstream text, not an analysis placeholder -->
## New Patterns
## Obsolete APIs Added
## Known Misalignments"""
    )
    guide = _write_current_guide(
        tmp_path,
        f"## Package API Evidence\n\n{evidence}\n\n{COMPLETE_GUIDE_SECTIONS}",
    )

    assert v.check_current_guide_contract(guide) == []


def test_fenced_headings_cannot_satisfy_required_analysis_sections(tmp_path):
    evidence_only = _fenced_upstream(
        """## Breaking Changes
Upstream-controlled prose that looks substantive.
## New Patterns
Upstream-controlled prose that looks substantive.
## Obsolete APIs Added
Upstream-controlled prose that looks substantive.
## Known Misalignments
Upstream-controlled prose that looks substantive."""
    )
    findings = v.check_current_guide_contract(
        _write_current_guide(tmp_path, evidence_only)
    )

    for section in (
        "Breaking Changes",
        "New Patterns",
        "Obsolete APIs Added",
        "Known Misalignments",
    ):
        assert any(f"exactly one '{section}'" in finding for finding in findings)


def test_fenced_payload_cannot_make_analysis_section_substantive(tmp_path):
    body = COMPLETE_GUIDE_SECTIONS.replace(
        "- Continue using the supported agent construction pattern.",
        _fenced_upstream("This upstream-controlled text is not reviewed analysis."),
    )
    findings = v.check_current_guide_contract(_write_current_guide(tmp_path, body))

    assert any(
        "New Patterns" in finding and "substantive" in finding
        for finding in findings
    )


def test_unterminated_upstream_fence_is_flagged(tmp_path):
    guide = _write_current_guide(
        tmp_path,
        _fenced_upstream("## Breaking Changes", close=False),
    )

    findings = v.check_current_guide_contract(guide)

    assert any("unterminated upstream-data fence" in finding for finding in findings)


def test_deleting_required_guide_sections_is_flagged(tmp_path):
    guide = _write_current_guide(
        tmp_path,
        "## Breaking Changes\n\nNone detected in validated API diffs.",
    )
    findings = v.check_current_guide_contract(guide)
    assert any("New Patterns" in finding for finding in findings)
    assert any("Known Misalignments" in finding for finding in findings)


def test_empty_or_comment_only_guide_section_is_flagged(tmp_path):
    body = COMPLETE_GUIDE_SECTIONS.replace(
        "- Continue using the supported agent construction pattern.",
        "<!-- reviewed -->\n> ⚠️ Auto-generated stub",
    )
    guide = _write_current_guide(tmp_path, body)
    findings = v.check_current_guide_contract(guide)
    assert any("New Patterns" in finding and "substantive" in finding for finding in findings)


def test_duplicate_or_reordered_guide_sections_are_flagged(tmp_path):
    duplicated = COMPLETE_GUIDE_SECTIONS + "\n## New Patterns\n\n- Duplicate.\n"
    assert any(
        "exactly one 'New Patterns'" in finding
        for finding in v.check_current_guide_contract(
            _write_current_guide(tmp_path, duplicated)
        )
    )

    reordered = COMPLETE_GUIDE_SECTIONS.replace(
        "## New Patterns\n\n- Continue using the supported agent construction pattern.\n\n"
        "## Obsolete APIs Added",
        "## Obsolete APIs Added",
    ) + "\n## New Patterns\n\n- Continue using the supported agent construction pattern.\n"
    assert any(
        "canonical" in finding
        for finding in v.check_current_guide_contract(
            _write_current_guide(tmp_path, reordered)
        )
    )


@pytest.mark.parametrize(
    "body",
    [
        "no markers",
        f"{v.AUTO_END}\nresolved\n{v.AUTO_START}",
        f"{v.AUTO_START}\nresolved\n{v.AUTO_END}\n{v.AUTO_END}",
    ],
)
def test_guide_requires_exactly_one_ordered_marker_pair(tmp_path, body):
    guide = tmp_path / "maf-1.12.0-migration-guide.md"
    guide.write_text(body, encoding="utf-8")
    assert v.check_current_guide_contract(guide)


def _seed_cumulative_guides(tmp_path: Path) -> Path:
    guides = tmp_path / "guides"
    guides.mkdir()
    (guides / "maf-1.3.0-migration-guide.md").write_text(
        "# Guide 1.3.0\n", encoding="utf-8"
    )
    (guides / "maf-1.12.0-migration-guide.md").write_text(
        "# Guide 1.12.0\n", encoding="utf-8"
    )
    return guides


def test_exact_cumulative_guide_passes(tmp_path):
    guides = _seed_cumulative_guides(tmp_path)
    expected = v.build_cumulative_guide(guides)
    assert expected is not None
    (guides / "maf-current-migration-guide.md").write_text(
        expected, encoding="utf-8"
    )
    assert v.check_cumulative_guide(guides) == []


def test_stale_cumulative_guide_fails_without_mutating_it(tmp_path):
    guides = _seed_cumulative_guides(tmp_path)
    cumulative = guides / "maf-current-migration-guide.md"
    stale = "# stale hand-edited cumulative guide\n"
    cumulative.write_text(stale, encoding="utf-8")

    findings = v.check_cumulative_guide(guides)

    assert len(findings) == 1
    assert "stale or hand-edited" in findings[0]
    assert cumulative.read_text(encoding="utf-8") == stale


# --- Task 4.4: detect unreplaced watcher placeholders in the top row ---------

def test_placeholder_row_is_flagged():
    row = ("| **1.7.0** | `>= unknown` | `>= 8.0` | `>= unknown` | `1.7.0` | "
           "Auto-detected — verify versions in PR review. |")
    leftover = v.unreplaced_placeholders(v.top_row_line(row))
    assert ">= unknown" in leftover
    assert "Auto-detected — verify" in leftover


def test_clean_row_has_no_placeholders():
    row = ("| **1.7.0** | `>= 9.7.0` | `>= 8.0` | `>= 2.1.0` | `1.7.0` | "
           "Verified against the 1.7.0 release. |")
    assert v.unreplaced_placeholders(v.top_row_line(row)) == []


def test_top_row_line_returns_first_version_row():
    text = "| Version | X |\n|---|---|\n| **1.7.0** | a |\n| **1.6.0** | b |\n"
    assert "1.7.0" in v.top_row_line(text)


def test_top_row_line_none_when_no_rows():
    assert v.top_row_line("no version rows here") is None
    assert v.unreplaced_placeholders(None) == []


# --- keep-breaking-red gate: unfilled drafts for the current release ---------

def _write_registry(tmp_path, body: str) -> Path:
    reg = tmp_path / "registry.yaml"
    reg.write_text(body, encoding="utf-8")
    return reg


def test_current_empty_guide_section_is_flagged(tmp_path):
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-EMPTY-GUIDE
    version_introduced: "1.12.0"
    guide_section:
    applies_to_codebases: pre-1.12.0
""")
    findings = v.check_invented_guide_sections(reg, {"1"}, "1.12.0")
    assert len(findings) == 1
    assert "guide_section is missing or empty" in findings[0]


@pytest.mark.parametrize("marker", [None, "", "pre-1.0.0", "1.12.0+"])
def test_current_missing_or_wrong_applies_marker_is_flagged(tmp_path, marker):
    applies = "" if marker is None else f"    applies_to_codebases: {marker}\n"
    reg = _write_registry(tmp_path, f"""
entries:
  - id: MAF120-APPLIES
    version_introduced: "1.12.0"
    guide_section: N/A
{applies}""")
    findings = v.check_invented_guide_sections(reg, set(), "1.12.0")
    assert len(findings) == 1
    assert "applies_to_codebases must be exactly 'pre-1.12.0'" in findings[0]


def test_current_exact_registry_crossfile_metadata_passes(tmp_path):
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-METADATA
    version_introduced: "1.12.0"
    guide_section: N/A
    applies_to_codebases: pre-1.12.0
""")
    assert v.check_invented_guide_sections(reg, set(), "1.12.0") == []


def test_legacy_empty_guide_and_missing_applies_marker_are_allowed(tmp_path):
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF110-LEGACY
    version_introduced: "1.11.0"
    guide_section:
""")
    assert v.check_invented_guide_sections(reg, set(), "1.12.0") == []


def test_unfilled_current_draft_is_flagged(tmp_path):
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-FOO-001
    version_introduced: "1.12.0"
    obsolete_signature: TODO
    replacement_signature: TODO
    fix_description: TODO — review the diff entry below
    cs_warning: TODO
""")
    findings = v.check_unfilled_current_drafts(reg, "1.12.0")
    assert len(findings) == 1
    assert "MAF120-FOO-001" in findings[0]


def test_filled_current_entry_passes(tmp_path):
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-FOO-001
    version_introduced: "1.12.0"
    obsolete_signature: Foo.OldMethod()
    replacement_signature: Foo.NewMethod()
    fix_description: Rename OldMethod to NewMethod; the signature is unchanged.
    example_before: |
      foo.OldMethod();
    example_after: |
      foo.NewMethod();
    cs_warning: CS0618
""")
    assert v.check_unfilled_current_drafts(reg, "1.12.0") == []


def test_missing_examples_is_flagged(tmp_path):
    # fix_description filled, but no example_before/after at all -> incomplete.
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-FOO-001
    version_introduced: "1.12.0"
    obsolete_signature: Foo.Bar()
    replacement_signature: Foo.Baz()
    fix_description: Rename Bar to Baz everywhere; same semantics.
    cs_warning: CS0618
""")
    assert len(v.check_unfilled_current_drafts(reg, "1.12.0")) == 1


def test_unfilled_signature_is_flagged(tmp_path):
    # Prose + examples filled, but a signature is still TODO -> incomplete draft.
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-FOO-001
    version_introduced: "1.12.0"
    obsolete_signature: TODO
    replacement_signature: Foo.Baz()
    fix_description: Rename Bar to Baz everywhere; same semantics.
    example_before: |
      foo.Bar();
    example_after: |
      foo.Baz();
    cs_warning: CS0618
""")
    assert len(v.check_unfilled_current_drafts(reg, "1.12.0")) == 1


def test_signature_change_draft_with_filled_sigs_is_flagged(tmp_path):
    # The extractor auto-fills BOTH signatures for a SignatureChanged entry, so an
    # all-three-AND gate would miss it. The strengthened gate fires on the still-
    # TODO fix_description.
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-FOO-001
    version_introduced: "1.12.0"
    obsolete_signature: Foo.Bar(int)
    replacement_signature: Foo.Bar(int, bool)
    fix_description: TODO — review the diff entry below and document the canonical fix
    cs_warning: CS0618
""")
    assert len(v.check_unfilled_current_drafts(reg, "1.12.0")) == 1


def test_todo_example_stub_is_flagged(tmp_path):
    # fix_description filled, but the example is still the `// TODO` stub.
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF120-FOO-001
    version_introduced: "1.12.0"
    obsolete_signature: Foo.Bar()
    replacement_signature: Foo.Baz()
    fix_description: Rename Bar to Baz; same semantics, just a new name everywhere.
    example_before: |
      // TODO — show a real call site that breaks
    example_after: |
      var x = foo.Baz();
    cs_warning: CS0618
""")
    assert len(v.check_unfilled_current_drafts(reg, "1.12.0")) == 1


@pytest.mark.parametrize(
    "warning", ["TODO", "", "CS618", "CS06180", "runtime_silent", "binary_break"]
)
def test_current_entry_invalid_cs_warning_is_flagged(tmp_path, warning):
    reg = _write_registry(tmp_path, f"""
entries:
  - id: MAF120-DIAG-001
    version_introduced: "1.12.0"
    obsolete_signature: Foo.OldMethod()
    replacement_signature: Foo.NewMethod()
    fix_description: Rename OldMethod to NewMethod; the signature is unchanged.
    example_before: |
      foo.OldMethod();
    example_after: |
      foo.NewMethod();
    cs_warning: "{warning}"
""")

    findings = v.check_unfilled_current_drafts(reg, "1.12.0")
    assert len(findings) == 1
    assert "cs_warning" in findings[0]


@pytest.mark.parametrize(
    "warning", ["CS0618", "CS0246", "CS1234", "RUNTIME_SILENT", "BINARY_BREAK"]
)
def test_valid_cs_warning_contract(warning):
    assert v._valid_cs_warning(warning)


def test_old_cycle_unfilled_draft_is_ignored(tmp_path):
    # An unfilled draft from an OLDER release must not block a newer PR.
    reg = _write_registry(tmp_path, """
entries:
  - id: MAF150-BAR-001
    version_introduced: "1.5.0"
    obsolete_signature: TODO
    replacement_signature: TODO
    fix_description: TODO
""")
    assert v.check_unfilled_current_drafts(reg, "1.12.0") == []


def test_additive_release_no_entries_passes(tmp_path):
    assert v.check_unfilled_current_drafts(_write_registry(tmp_path, "entries: []\n"), "1.12.0") == []


def test_looks_unfilled_classifier():
    assert v._looks_unfilled(None)
    assert v._looks_unfilled("")
    assert v._looks_unfilled("TODO")
    assert v._looks_unfilled("TODO — fill me")
    assert not v._looks_unfilled("Foo.Bar()")


# --- Matrix covers every release the registry knows (2026-10-02) -------------

def _registry(tmp_path, versions):
    reg = tmp_path / "registry.yaml"
    reg.write_text("entries:\n" + "".join(
        f'  - id: X-{i}\n    version_introduced: "{ver}"\n' for i, ver in enumerate(versions)
    ), encoding="utf-8")
    return reg


def test_matrix_coverage_passes_when_every_release_has_rows(tmp_path):
    reg = _registry(tmp_path, ["1.7.0", "1.10.0"])
    md = "| **1.10.0** | a |\n| **1.7.0** | b |\n"
    cs = '            ["1.10.0"] = """\n            ["1.7.0"] = """\n'
    assert v.check_matrix_covers_registry(reg, md, cs) == []


def test_matrix_coverage_names_releases_missing_from_doc_and_tool(tmp_path):
    reg = _registry(tmp_path, ["1.6.2", "1.7.0", "1.10.0", "TODO"])
    md = "| **1.10.0** | a |\n"
    cs = '            ["1.10.0"] = """\n            ["1.7.0"] = """\n'
    findings = v.check_matrix_covers_registry(reg, md, cs)
    assert len(findings) == 2
    assert "1.6.2, 1.7.0" in findings[0]  # doc: version-sorted, TODO ignored
    assert "CompatibilityTool" in findings[1]
    assert "1.6.2" in findings[1] and "1.7.0" not in findings[1]


def test_matrix_coverage_ignores_unbolded_rows(tmp_path):
    # Rows must use the `**X.Y.Z**` stem the other matrix gates parse.
    reg = _registry(tmp_path, ["1.2.0"])
    assert v.check_matrix_covers_registry(reg, "| 1.2.0 | a |\n", None) != []


def test_matrix_coverage_skips_tool_when_absent(tmp_path):
    reg = _registry(tmp_path, ["1.7.0"])
    assert v.check_matrix_covers_registry(reg, "| **1.7.0** | a |\n", None) == []


def test_real_repo_matrix_covers_registry():
    root = SCRIPT_DIR.parents[1]
    reg = root / ".github" / "skills" / "maf-obsolete-api-registry" / "registry.yaml"
    md = (root / "docs" / "compatibility-matrix.md").read_text(encoding="utf-8")
    cs = (root / "src" / "maf-autopilot" / "Tools" / "CompatibilityTool.cs").read_text(encoding="utf-8")
    assert v.check_matrix_covers_registry(reg, md, cs) == []

