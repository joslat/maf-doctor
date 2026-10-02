#!/usr/bin/env python3
"""
Cross-file consistency checks for MAF release artifacts.

Runs AFTER `maf-autopilot verify-registry` (which checks registry.yaml
entry-internal consistency). This script checks consistency ACROSS the
files the release-watcher touches.

Findings discovered 2026-05-17 on PR #26 and PR #36 motivated these checks:
- Bot ADDED a new `**1.6.1**` row to compat-matrix instead of UPDATING the
  watcher-inserted placeholder row → duplicate version rows.
- "Version Tracking" section still said the old version after a release.
- Bot left the `last-updated: YYYY-MM-DD` header date stale.
- Bot invented a `guide_section: "1"` reference that doesn't exist.

Checks performed:
1. docs/compatibility-matrix.md has no DUPLICATE `**X.Y.Z**` rows.
2. The TOP data row in the compat-matrix table matches `.maf-version`.
3. Exactly one "Version Tracking" section's "Current tracked version:
   **X.Y.Z**" line exists and matches `.maf-version`.
4. `guides/maf-{VERSION}-migration-guide.md` exists for `.maf-version`.
5. Exactly one valid `last-updated: YYYY-MM-DD` header date exists and is
   within the last 30 days (catches stale or duplicated release metadata).
6. Current-cycle registry entries have a non-empty, valid `guide_section` and
   the exact `applies_to_codebases: pre-{VERSION}` marker.
7. The current per-version guide has exactly one ordered AUTO marker pair and
   no unresolved generated `<!-- TODO:` placeholders.
8. The cumulative guide exactly matches deterministic regeneration from all
   checked-in per-version guides.

Exits 0 if all consistent, 1 if any check fails. Prints findings to stdout.
"""
from __future__ import annotations

import datetime as dt
import re
import sys
from pathlib import Path

import yaml

from gen_guide_section import AUTO_END, AUTO_START, build_cumulative_guide

ROOT = Path.cwd()
MAF_VERSION_FILE = ROOT / ".maf-version"
COMPAT_MATRIX = ROOT / "docs" / "compatibility-matrix.md"
GUIDES_DIR = ROOT / "guides"
REGISTRY = ROOT / ".github" / "skills" / "maf-obsolete-api-registry" / "registry.yaml"
COMPAT_TOOL = ROOT / "src" / "maf-autopilot" / "Tools" / "CompatibilityTool.cs"
MAX_LAST_UPDATED_AGE_DAYS = 30

# A "version-stem" row in compat-matrix table starts with: | **X.Y.Z** | ...
# Allow optional leading whitespace and a 2- or 3-part version.
VERSION_ROW_RE = re.compile(
    r'^\s*\|\s*\*\*(\d+\.\d+(?:\.\d+)?)\*\*\s*\|', re.MULTILINE
)
CURRENT_TRACKED_RE = re.compile(
    r'Current tracked version:\s*\*\*`?(\d+\.\d+(?:\.\d+)?)`?\*\*',
    re.IGNORECASE,
)
CURRENT_TRACKED_LABEL_RE = re.compile(
    r'Current tracked version\s*:', re.IGNORECASE
)
LAST_UPDATED_RE = re.compile(
    r'last-updated:\s*(\d{4}-\d{2}-\d{2})', re.IGNORECASE
)
LAST_UPDATED_LABEL_RE = re.compile(r'last-updated\s*:', re.IGNORECASE)
# Section headings in migration guides — match "## 1." "### 2.3." "#### 1.2.3." etc.
GUIDE_SECTION_HEADING_RE = re.compile(
    r'^##+\s+(\d+(?:\.\d+)*)\.', re.MULTILINE
)
GENERATED_TODO_RE = re.compile(r'<!--\s*TODO:', re.IGNORECASE)
UPSTREAM_FENCE_BEGIN_RE = re.compile(
    r"^<<<BEGIN_(USER_DATA_[0-9a-f]{32}_[A-Z0-9._-]{1,64})>>>$"
)


def find_duplicate_rows(text: str) -> list[str]:
    """Return version strings that appear more than once as a row stem."""
    versions = VERSION_ROW_RE.findall(text)
    seen: set[str] = set()
    dupes: list[str] = []
    for v in versions:
        if v in seen and v not in dupes:
            dupes.append(v)
        seen.add(v)
    return dupes


def top_version_row(text: str) -> str | None:
    """Return the first matched version (the top row of the table), or None."""
    m = VERSION_ROW_RE.search(text)
    return m.group(1) if m else None


def version_tracking_current(text: str) -> str | None:
    """Return the version mentioned in 'Current tracked version: **X.Y.Z**'."""
    m = CURRENT_TRACKED_RE.search(text)
    return m.group(1) if m else None


def last_updated_date(text: str) -> dt.date | None:
    """Return the date in the 'last-updated: YYYY-MM-DD' header comment."""
    m = LAST_UPDATED_RE.search(text)
    if not m:
        return None
    try:
        return dt.date.fromisoformat(m.group(1))
    except ValueError:
        return None


def check_compatibility_metadata(
    text: str,
    current_version: str,
    *,
    today: dt.date | None = None,
) -> list[str]:
    """Require singular, current release metadata in the compatibility matrix.

    Counting regex matches instead of accepting the first match is intentional:
    duplicated tracking/date fields make it ambiguous which value downstream
    automation or a reader should trust, even when the first happens to be right.
    """
    findings: list[str] = []

    tracked_labels = CURRENT_TRACKED_LABEL_RE.findall(text)
    tracked_versions = CURRENT_TRACKED_RE.findall(text)
    if len(tracked_labels) != 1 or len(tracked_versions) != 1:
        findings.append(
            "compatibility-matrix.md must contain exactly one valid "
            "'Current tracked version: **X.Y.Z**' line; found "
            f"{len(tracked_versions)} valid value(s) across "
            f"{len(tracked_labels)} field(s)."
        )
    elif tracked_versions[0] != current_version:
        findings.append(
            "compatibility-matrix.md 'Version Tracking' section says "
            f"current tracked version is `{tracked_versions[0]}` but .maf-version "
            f"is `{current_version}`. Update the 'Current tracked version' line."
        )

    date_labels = LAST_UPDATED_LABEL_RE.findall(text)
    date_values = LAST_UPDATED_RE.findall(text)
    if len(date_labels) != 1 or len(date_values) != 1:
        findings.append(
            "compatibility-matrix.md must contain exactly one valid "
            "'last-updated: YYYY-MM-DD' header; found "
            f"{len(date_values)} valid value(s) across {len(date_labels)} field(s)."
        )
        return findings

    try:
        updated = dt.date.fromisoformat(date_values[0])
    except ValueError:
        findings.append(
            "compatibility-matrix.md 'last-updated:' value "
            f"`{date_values[0]}` is not a valid calendar date."
        )
        return findings

    reference_date = today or dt.date.today()
    age = (reference_date - updated).days
    if age < 0:
        findings.append(
            "compatibility-matrix.md 'last-updated:' date is "
            f"{updated.isoformat()} — {-age} day(s) in the future relative to "
            f"{reference_date.isoformat()}."
        )
    elif age > MAX_LAST_UPDATED_AGE_DAYS:
        findings.append(
            "compatibility-matrix.md 'last-updated:' date is "
            f"{updated.isoformat()} — {age} days old (max "
            f"{MAX_LAST_UPDATED_AGE_DAYS}); update it for the current release cycle."
        )
    return findings


def collect_guide_sections(guide_path: Path) -> set[str]:
    """Return the set of section IDs (e.g. '1', '2.3') from a migration guide."""
    if not guide_path.is_file():
        return set()
    text = guide_path.read_text(encoding="utf-8")
    return set(GUIDE_SECTION_HEADING_RE.findall(text))


def collect_all_guide_sections(guides_dir: Path) -> set[str]:
    """Union of section IDs across EVERY per-version migration guide.

    The watcher now emits per-version guides (1.4.0 / 1.5.0 / …). A guide_section
    that legitimately points at a section in a newer guide must not be flagged
    'invented' just because it's absent from the 1.3.0 guide (task 4.2).
    """
    sections: set[str] = set()
    if not guides_dir.is_dir():
        return sections
    for guide in guides_dir.glob("maf-*-migration-guide.md"):
        if guide.name == "maf-current-migration-guide.md":
            continue  # cumulative file is a regenerated superset of the others
        sections |= collect_guide_sections(guide)
    return sections


# Watcher placeholder cells the fill bot must replace before merge (task 4.4).
MATRIX_PLACEHOLDERS = (">= unknown", "Auto-detected — verify")


def top_row_line(text: str) -> str | None:
    """Return the full markdown line of the top `**X.Y.Z**` data row, or None."""
    for line in text.splitlines():
        if VERSION_ROW_RE.search(line):
            return line
    return None


def unreplaced_placeholders(top_line: str | None) -> list[str]:
    """Watcher placeholders still present in the current (top) matrix row."""
    if not top_line:
        return []
    return [p for p in MATRIX_PLACEHOLDERS if p in top_line]


def check_invented_guide_sections(
    registry_path: Path,
    valid_sections: set[str],
    current_version: str,
) -> list[str]:
    """For each registry entry from the CURRENT release cycle, confirm its
    `guide_section` value is non-empty and either 'N/A' or a section ID present
    in a per-version guide. It must also carry the exact current-cycle
    `applies_to_codebases` marker.

    Scoped to the current cycle (entries with id `MAF{digits}-...` where
    digits == current_version stripped of dots) so legacy tech debt in
    older entries doesn't block PRs touching the registry. Legacy
    cleanup is a separate concern, handled by ad-hoc cleanup PRs."""
    if not registry_path.is_file():
        return [f"{registry_path} not found."]

    try:
        data = yaml.safe_load(registry_path.read_text(encoding="utf-8")) or {}
    except yaml.YAMLError as exc:
        return [f"registry.yaml is not valid YAML: {exc}"]

    findings: list[str] = []
    scoped_count = 0
    for entry in data.get("entries", []):
        eid = entry.get("id", "<missing id>")
        # Scope by the version_introduced FIELD, not an id-prefix guess: the
        # extractor caps the id digit-segment at 3 chars (1.11.1 -> "MAF111-",
        # NOT "MAF1111-"), so a prefix built from the full dotted version never
        # matched for 1.10+/1.11+ and this check was silently dead.
        if str(entry.get("version_introduced", "")).strip() != current_version:
            continue
        scoped_count += 1
        expected_applies = f"pre-{current_version}"
        actual_applies = str(entry.get("applies_to_codebases", "")).strip()
        if actual_applies != expected_applies:
            findings.append(
                f"{eid}: applies_to_codebases must be exactly "
                f"'{expected_applies}' for the current release cycle; found "
                f"'{actual_applies or '<missing>'}'."
            )

        gs = entry.get("guide_section")
        gs_str = str(gs).strip() if gs is not None else ""
        if not gs_str:
            findings.append(
                f"{eid}: guide_section is missing or empty — current-release "
                "entries must use a real section ID or the literal 'N/A'."
            )
            continue
        if gs_str.upper() == "N/A":
            continue
        if gs_str.upper() in ("TODO", "TBD"):
            findings.append(
                f"{eid}: guide_section is '{gs_str}' — must be a real section "
                f"ID from one of the migration guides (guides/maf-*-migration-"
                f"guide.md) OR the literal 'N/A'."
            )
            continue
        if gs_str not in valid_sections:
            findings.append(
                f"{eid}: guide_section '{gs_str}' is not a section heading in "
                f"any migration guide (guides/maf-*-migration-guide.md). Either "
                f"use a real section ID from one of those guides, or use 'N/A' "
                f"(when the change touches a surface that didn't exist yet). "
                f"NEVER invent section numbers."
            )
    print(
        f"Validated guide_section on {scoped_count} entries introduced in "
        f"{current_version} (current release cycle scope)."
    )
    return findings


def _mask_upstream_fences(text: str) -> str:
    """Hide fenced upstream data without changing offsets or line endings.

    Package diffs and release notes are deliberately embedded inside randomized
    ``USER_DATA`` fences. Their payload is evidence, not guide structure: a
    heading or TODO-looking comment supplied by an upstream package must not be
    counted as maintainer-authored analysis. Replacing every non-newline
    character with a space keeps regex match offsets aligned with ``text``.

    A recognized opening fence without its exact randomized closing marker is
    rejected instead of masking the rest of the guide, so malformed evidence
    cannot hide required sections.
    """
    masked: list[str] = []
    expected_end: str | None = None
    for line in text.splitlines(keepends=True):
        bare = line.rstrip("\r\n")
        if expected_end is None:
            begin = UPSTREAM_FENCE_BEGIN_RE.fullmatch(bare)
            if begin is None:
                masked.append(line)
                continue
            expected_end = f"<<<END_{begin.group(1)}>>>"
        elif bare == expected_end:
            expected_end = None

        masked.append("".join(ch if ch in "\r\n" else " " for ch in line))

    if expected_end is not None:
        raise ValueError("guide contains an unterminated upstream-data fence")
    return "".join(masked)


def check_current_guide_contract(guide_path: Path) -> list[str]:
    """Require a complete, substantive set of generated migration sections."""
    if not guide_path.is_file():
        return [f"missing migration guide: {guide_path}"]

    text = guide_path.read_text(encoding="utf-8")
    start_count = text.count(AUTO_START)
    end_count = text.count(AUTO_END)
    findings: list[str] = []
    if start_count != 1 or end_count != 1:
        findings.append(
            f"{guide_path}: expected exactly one AUTO-GENERATED START marker and "
            f"one AUTO-GENERATED END marker; found {start_count} start and "
            f"{end_count} end marker(s)."
        )
        return findings

    start = text.index(AUTO_START)
    end = text.index(AUTO_END)
    if start >= end:
        findings.append(
            f"{guide_path}: AUTO-GENERATED markers are out of order; START must "
            "appear before END."
        )
        return findings

    generated = text[start + len(AUTO_START):end]
    try:
        structural = _mask_upstream_fences(generated)
    except ValueError as exc:
        findings.append(f"{guide_path}: {exc}")
        return findings

    if GENERATED_TODO_RE.search(structural):
        findings.append(
            f"{guide_path}: the AUTO-GENERATED block still contains one or more "
            "'<!-- TODO:' placeholders; replace every generated placeholder "
            "before merging."
        )

    required = (
        ("Breaking Changes", r"^## Breaking Changes(?: \(requires human verification\))?$"),
        ("New Patterns", r"^## New Patterns$"),
        ("Obsolete APIs Added", r"^## Obsolete APIs Added$"),
        ("Known Misalignments", r"^## Known Misalignments$"),
    )
    matches: list[tuple[str, re.Match[str]]] = []
    for name, pattern in required:
        found = list(re.finditer(pattern, structural, re.MULTILINE))
        if len(found) != 1:
            findings.append(
                f"{guide_path}: expected exactly one '{name}' section inside "
                f"the AUTO-GENERATED block; found {len(found)}."
            )
        else:
            matches.append((name, found[0]))

    if len(matches) != len(required):
        return findings
    canonical_names = [name for name, _ in required]
    ordered_matches = sorted(matches, key=lambda item: item[1].start())
    if [name for name, _ in ordered_matches] != canonical_names:
        findings.append(
            f"{guide_path}: required migration sections are not in the canonical "
            "Breaking/New Patterns/Obsolete/Known Misalignments order."
        )

    for index, (name, match) in enumerate(ordered_matches):
        section_end = (
            ordered_matches[index + 1][1].start()
            if index + 1 < len(ordered_matches)
            else len(structural)
        )
        body = structural[match.end():section_end]
        body = re.sub(r"<!--.*?-->", "", body, flags=re.DOTALL)
        meaningful_lines = [
            line.strip()
            for line in body.splitlines()
            if line.strip()
            and not (
                line.lstrip().startswith(">")
                and "Auto-generated stub" in line
            )
        ]
        meaningful = "\n".join(meaningful_lines)
        if (
            len(re.sub(r"\s+", "", meaningful)) < 10
            or not re.search(r"[A-Za-z0-9]", meaningful)
            or re.search(r"\b(?:TODO|TBD|XXX)\b", meaningful, re.IGNORECASE)
        ):
            findings.append(
                f"{guide_path}: '{name}' has no substantive reviewed content "
                "after comments/stub warnings are ignored."
            )
    return findings


def check_cumulative_guide(guides_dir: Path) -> list[str]:
    """Compare cumulative content with regeneration without mutating the file."""
    expected = build_cumulative_guide(guides_dir)
    if expected is None:
        return [f"{guides_dir}: no per-version migration guides were found."]

    cumulative = guides_dir / "maf-current-migration-guide.md"
    if not cumulative.is_file():
        return [f"missing cumulative migration guide: {cumulative}"]
    actual = cumulative.read_text(encoding="utf-8")
    if actual != expected:
        return [
            f"{cumulative}: content is stale or hand-edited; regenerate it with "
            "`python3 .github/scripts/gen_guide_section.py --cumulative-only` "
            "and commit the exact output."
        ]
    return []


# Explicit ASCII boundary (not \b) — .NET and Python disagree on \b's Unicode
# word-class (~1400 code points flip), so both gates use an ASCII char-class
# boundary instead, kept byte-identical to the C# PlaceholderTokenRegex /
# TodoAtLineStart and locked by placeholder-fixtures.json.
_DRAFT_PLACEHOLDER = re.compile(r'^\s*(?:TODO|TBD|XXX)(?:[^A-Za-z0-9_]|$)', re.IGNORECASE)
_TODO_EXAMPLE_RE = re.compile(r'(?m)^\s*//\s*TODO(?:[^A-Za-z0-9_]|$)', re.IGNORECASE)
_CS_WARNING_RE = re.compile(r'^(?:CS[0-9]{4}|RUNTIME_SILENT|BINARY_BREAK)$')


def is_placeholder_token(value) -> bool:
    """A leading TODO/TBD/XXX word-token — the SHARED placeholder grammar. Kept
    identical to C# VerifyRegistryCommand.LooksLikePlaceholder and locked to
    placeholder-fixtures.json by a cross-language test so the two gates never
    drift (the historical bug: the extractor's 'TODO — …' em-dash passed C# but
    failed here). Empty is NOT a token — callers treat empty separately."""
    return bool(_DRAFT_PLACEHOLDER.match(str(value) if value is not None else ""))


def _looks_unfilled(value) -> bool:
    if value is None:
        return True
    s = str(value).strip()
    return s == "" or is_placeholder_token(s)


def _example_unfilled(value) -> bool:
    """True if an example_* field is missing, blank, or still the extractor's
    `// TODO …` stub — any of which leaves a current-release draft incomplete."""
    if value is None:
        return True
    s = str(value).strip()
    return s == "" or bool(_TODO_EXAMPLE_RE.search(s))


def _valid_cs_warning(value) -> bool:
    """True only for a reviewed compiler diagnostic, runtime marker, or binary break."""
    if value is None:
        return False
    warning = str(value).strip()
    return not is_placeholder_token(warning) and bool(_CS_WARNING_RE.fullmatch(warning))


def check_unfilled_current_drafts(registry_path: Path, current_version: str) -> list[str]:
    """The "keep breaking red" gate for the green-on-arrival watcher.

    A release the watcher classifies as ADDITIVE produces no registry entries,
    so this finds nothing and the scaffold PR is green. A BREAKING release
    produces draft entries (member removals / signature changes) whose migration
    text a human must write; until they do, the entry's core fields stay TODO
    placeholders and this gate fails the PR.

    Scoped to entries whose `version_introduced` equals the current release
    (`.maf-version`) so the intentional historical TODO drafts from older cycles
    never block an unrelated PR."""
    if not registry_path.is_file():
        return [f"{registry_path} not found."]
    try:
        data = yaml.safe_load(registry_path.read_text(encoding="utf-8")) or {}
    except yaml.YAMLError as exc:
        return [f"registry.yaml is not valid YAML: {exc}"]

    findings: list[str] = []
    checked = 0
    for entry in data.get("entries", []):
        if str(entry.get("version_introduced", "")).strip() != current_version:
            continue
        checked += 1
        # Fire if ANY core field is still a placeholder. An OR (not the old
        # all-three-AND, which missed SignatureChanged entries whose signatures
        # the extractor auto-fills) so a current-release entry can't merge with a
        # TODO/blank signature, prose, example stub, OR unreviewed diagnostic.
        if (
            _looks_unfilled(entry.get("obsolete_signature"))
            or _looks_unfilled(entry.get("replacement_signature"))
            or _looks_unfilled(entry.get("fix_description"))
            or _example_unfilled(entry.get("example_before"))
            or _example_unfilled(entry.get("example_after"))
            or not _valid_cs_warning(entry.get("cs_warning"))
        ):
            findings.append(
                f"{entry.get('id', '<missing id>')}: registry draft for the current "
                f"release ({current_version}) is unfilled — a breaking release's entries "
                f"need a human-written migration. Fill obsolete_signature / "
                f"replacement_signature / fix_description / before/after examples and "
                f"set cs_warning to CS\\d{{4}}, RUNTIME_SILENT, or BINARY_BREAK "
                f"(or, if this release is genuinely additive, the watcher should not have "
                f"created the entry)."
            )
    print(
        f"Checked {checked} registry entr(y/ies) introduced in {current_version} "
        f"for unfilled drafts (keep-breaking-red gate)."
    )
    return findings


# A CompatibilityTool.Matrix key line: `["X.Y.Z"] = """`
COMPAT_TOOL_KEY_RE = re.compile(r'^\s*\["(\d+\.\d+\.\d+)"\]\s*=\s*"""', re.MULTILINE)


def check_matrix_covers_registry(
    registry_path: Path, matrix_text: str, tool_text: str | None
) -> list[str]:
    """Every MAF release the registry has entries for needs a compatibility row.

    Without one, `maf_compatibility X.Y.Z` answers "unknown version" and the
    regression-plan ladder skips the release, even though the registry knows its
    changes (found 2026-10-02: 1.6.0, 1.6.2 and 1.7.0 to 1.9.0 were backfilled
    into the registry but had no rows). Checks the doc table and, when given,
    the `CompatibilityTool.Matrix` keys the MCP tool serves.
    """
    try:
        data = yaml.safe_load(registry_path.read_text(encoding="utf-8")) or {}
    except (OSError, yaml.YAMLError) as exc:
        return [f"could not read {registry_path} for the matrix-coverage check: {exc}"]
    entries = data.get("entries", []) if isinstance(data, dict) else []
    wanted = {
        str(e.get("version_introduced")).strip()
        for e in entries
        if isinstance(e, dict) and re.fullmatch(r"\d+\.\d+\.\d+", str(e.get("version_introduced", "")).strip())
    }
    key = lambda s: tuple(int(x) for x in s.split("."))
    findings: list[str] = []
    missing_md = sorted(wanted - set(VERSION_ROW_RE.findall(matrix_text)), key=key)
    if missing_md:
        findings.append(
            "compatibility-matrix.md has no `**X.Y.Z**` row for MAF release(s) the "
            f"registry has entries for: {', '.join(missing_md)}. Add a row per release."
        )
    if tool_text is not None:
        missing_cs = sorted(wanted - set(COMPAT_TOOL_KEY_RE.findall(tool_text)), key=key)
        if missing_cs:
            findings.append(
                "CompatibilityTool.Matrix has no entry for MAF release(s) the registry "
                f"has entries for: {', '.join(missing_cs)}. `maf_compatibility` answers "
                "\"unknown version\" for them; add a `[\"X.Y.Z\"] = \"\"\"...` block."
            )
    return findings


def main() -> int:
    findings: list[str] = []

    if not MAF_VERSION_FILE.is_file():
        print(f"ERROR: {MAF_VERSION_FILE} not found.", file=sys.stderr)
        return 1

    current = MAF_VERSION_FILE.read_text(encoding="utf-8").strip()
    print(f"Current .maf-version: {current}")

    if not COMPAT_MATRIX.is_file():
        findings.append(f"{COMPAT_MATRIX} not found.")
    else:
        text = COMPAT_MATRIX.read_text(encoding="utf-8")

        dupes = find_duplicate_rows(text)
        if dupes:
            findings.append(
                f"compatibility-matrix.md has DUPLICATE version rows: "
                f"{', '.join(dupes)}. Each version must appear at most once "
                f"as a `**X.Y.Z**` row. The watcher inserts a placeholder row "
                f"at the top — the filler bot should UPDATE that row, not "
                f"add a new one below."
            )

        top = top_version_row(text)
        if top is None:
            findings.append(
                "compatibility-matrix.md: could not find any `**X.Y.Z**` "
                "version row — table may be malformed."
            )
        elif top != current:
            findings.append(
                f"compatibility-matrix.md top data row is `**{top}**` but "
                f".maf-version is `{current}`. The newest version should "
                f"be at the top of the table."
            )

        # Task 4.4 — the watcher inserts placeholder cells (`>= unknown`,
        # `Auto-detected — verify…`) that the fill bot must replace. Nothing
        # else verified they were replaced, so a literal placeholder could reach
        # main and silently defeat the matrix. Fail if the current (top) row
        # still carries them.
        leftover = unreplaced_placeholders(top_row_line(text))
        if leftover:
            findings.append(
                f"compatibility-matrix.md top row still contains watcher "
                f"placeholder(s) {leftover} — replace `>= unknown` with the real "
                f"minimum versions and the `Auto-detected — verify…` note with a "
                f"real note before merging."
            )

        findings.extend(check_compatibility_metadata(text, current))
        findings.extend(check_matrix_covers_registry(
            REGISTRY, text,
            COMPAT_TOOL.read_text(encoding="utf-8") if COMPAT_TOOL.is_file() else None,
        ))

    guide_file = GUIDES_DIR / f"maf-{current}-migration-guide.md"
    findings.extend(check_current_guide_contract(guide_file))
    findings.extend(check_cumulative_guide(GUIDES_DIR))

    # Check for invented guide_section values in registry entries from
    # the CURRENT release cycle (older entries' TODOs are tech debt to
    # be cleaned up in dedicated PRs, not blockers for new fills).
    valid_sections = collect_all_guide_sections(GUIDES_DIR)
    if not valid_sections:
        print(
            "Note: no numeric section IDs were found in per-version guides; "
            "current entries must therefore use guide_section: N/A."
        )
    else:
        print(f"Guide section IDs available (all per-version guides): {sorted(valid_sections)}")
    section_findings = check_invented_guide_sections(
        REGISTRY, valid_sections, current
    )
    findings.extend(section_findings)

    # Keep-breaking-red gate — unfilled drafts for the CURRENT release fail the
    # PR (an additive release creates none, so it stays green).
    findings.extend(check_unfilled_current_drafts(REGISTRY, current))

    if findings:
        print("")
        print("=" * 72)
        print("CROSS-FILE CONSISTENCY ISSUES:")
        print("=" * 72)
        for f in findings:
            print(f"  * {f}")
        print("")
        return 1

    print("All cross-file consistency checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
