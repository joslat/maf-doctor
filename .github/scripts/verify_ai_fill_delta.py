#!/usr/bin/env python3
"""Build and verify immutable obligations for a watcher AI-fill scaffold."""
from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path
from typing import Any

import yaml

SCHEMA_VERSION = 1
OBLIGATIONS_DIR = ".github/maf-scaffold-obligations"
TRAIN_LOCK_REL = ".github/maf-train-lock.json"
REGISTRY_REL = ".github/skills/maf-obsolete-api-registry/registry.yaml"
MATRIX_REL = "docs/compatibility-matrix.md"
TOOL_REL = "src/maf-autopilot/Tools/CompatibilityTool.cs"
MAX_BYTES = 8 * 1024 * 1024
MAX_OBLIGATIONS_BYTES = 1024 * 1024
VERSION_RE = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
SHA_RE = re.compile(r"^[0-9a-f]{40,64}$")
BRANCH_RE = re.compile(r"^[A-Za-z0-9._/-]+$")
GUIDE_RE = re.compile(r"^maf-[0-9]+\.[0-9]+\.[0-9]+-migration-guide\.md$")
REVIEW_RE = re.compile(r"^MAF[0-9]+-REVIEW-[0-9]+$", re.IGNORECASE)
# Trusted copy (this script runs from the base checkout, never from PR data).
SURFACE_MANIFEST = Path(__file__).resolve().parents[1] / "maf-package-surfaces.json"


def _tracked_packages() -> set[str]:
    """Casefolded package ids the watcher diffs (the package-surface manifest)."""
    try:
        manifest = json.loads(SURFACE_MANIFEST.read_text(encoding="utf-8"))
        return {str(s["package"]).casefold() for s in manifest.get("surfaces", [])}
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise ValueError(f"cannot read trusted package-surface manifest: {exc}") from exc
AUTO_START = "<!-- AUTO-GENERATED START — anything between AUTO-GENERATED START and AUTO-GENERATED END is overwritten on re-run -->"
AUTO_END = "<!-- AUTO-GENERATED END -->"
BREAKING_RE = re.compile(
    r"^## Breaking Changes(?: \(requires human verification\))?\r?$", re.MULTILINE
)
FENCE_BEGIN_RE = re.compile(
    r"^<<<BEGIN_(USER_DATA_[0-9a-f]{32}_[A-Z0-9._-]{1,64})>>>$"
)


def _contract_rel(version: str) -> str:
    return f"{OBLIGATIONS_DIR}/maf-{version}.json"


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _canonical_hash(value: Any) -> str:
    raw = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return _sha(raw.encode("utf-8"))


def _safe_file(root: Path, relative: str, label: str) -> Path:
    rel = Path(relative)
    if not relative or rel.is_absolute() or ".." in rel.parts:
        raise ValueError(f"unsafe {label} path: {relative!r}")
    path = root / rel
    if path.is_symlink():
        raise ValueError(f"{label} must not be a symlink: {relative}")
    try:
        resolved, resolved_root = path.resolve(strict=True), root.resolve(strict=True)
    except OSError as exc:
        raise ValueError(f"cannot resolve {label} {relative}: {exc}") from exc
    if resolved_root not in resolved.parents or not resolved.is_file():
        raise ValueError(f"{label} is not a repository file: {relative}")
    return resolved


def _bytes(path: Path, label: str, limit: int = MAX_BYTES) -> bytes:
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise ValueError(f"cannot read {label}: {exc}") from exc
    if len(raw) > limit:
        raise ValueError(f"{label} exceeds {limit} bytes")
    return raw


def _text(path: Path, label: str, limit: int = MAX_BYTES) -> str:
    try:
        return _bytes(path, label, limit).decode("utf-8")
    except UnicodeDecodeError as exc:
        raise ValueError(f"{label} is not UTF-8: {exc}") from exc


def _registry(path: Path, label: str) -> dict[str, Any]:
    try:
        doc = yaml.safe_load(_text(path, f"{label} registry")) or {}
    except yaml.YAMLError as exc:
        raise ValueError(f"cannot parse {label} registry: {exc}") from exc
    if not isinstance(doc, dict) or not isinstance(doc.get("entries"), list):
        raise ValueError(f"{label} registry entries must be an array")
    if any(not isinstance(entry, dict) for entry in doc["entries"]):
        raise ValueError(f"{label} registry contains a non-object entry")
    return doc


def _field(entry: dict[str, Any], key: str) -> str:
    value = entry.get(key)
    return value.strip() if isinstance(value, str) else ""


def _target_entries(doc: dict[str, Any], target: str) -> list[dict[str, Any]]:
    return [e for e in doc["entries"] if _field(e, "version_introduced") == target]


def _historical_registry_hash(doc: dict[str, Any], target: str) -> str:
    historical = copy.deepcopy(doc)
    historical["entries"] = [
        entry
        for entry in historical["entries"]
        if _field(entry, "version_introduced") != target
    ]
    return _canonical_hash(historical)


def _structural_markdown(text: str, label: str) -> str:
    """Mask fenced upstream data while preserving source offsets and newlines."""
    visible: list[str] = []
    expected_end: str | None = None
    for line in text.splitlines(keepends=True):
        bare = line.rstrip("\r\n")
        if expected_end is None:
            begin = FENCE_BEGIN_RE.fullmatch(bare)
            if begin is None:
                visible.append(line)
                continue
            expected_end = f"<<<END_{begin.group(1)}>>>"
        elif bare == expected_end:
            expected_end = None
        visible.append("".join(ch if ch in "\r\n" else " " for ch in line))
    if expected_end is not None:
        raise ValueError(f"{label} guide contains an unterminated upstream-data fence")
    return "".join(visible)


def _guide_regions(text: str, label: str) -> tuple[str, str, str]:
    if text.count(AUTO_START) != 1 or text.count(AUTO_END) != 1:
        raise ValueError(f"{label} guide must contain exactly one AUTO marker pair")
    auto_start, auto_end = text.index(AUTO_START), text.index(AUTO_END)
    if auto_start >= auto_end:
        raise ValueError(f"{label} guide AUTO markers are out of order")
    structural = _structural_markdown(text, label)
    breaks = [m for m in BREAKING_RE.finditer(structural) if auto_start < m.start() < auto_end]
    packages = [
        m
        for m in re.finditer(r"^## Package API Evidence\r?$", structural, re.MULTILINE)
        if auto_start < m.start() < auto_end
    ]
    notes = [
        m
        for m in re.finditer(r"^## Release Notes Extract\r?$", structural, re.MULTILINE)
        if auto_start < m.start() < auto_end
    ]
    if len(breaks) != 1 or len(packages) != 1 or len(notes) != 1:
        raise ValueError(
            f"{label} guide needs one Package API Evidence, Release Notes Extract, and Breaking Changes heading"
        )
    if not packages[0].start() < notes[0].start() < breaks[0].start():
        raise ValueError(f"{label} guide evidence headings are out of order")
    return (
        text[: breaks[0].start()],
        text[auto_end:],
        text[packages[0].start() : breaks[0].start()],
    )


def _matrix_regions(text: str, target: str, label: str) -> tuple[str, str]:
    rows = list(
        re.finditer(
            rf"^\|\s*\*\*{re.escape(target)}\*\*\s*\|[^\r\n]*\r?$",
            text,
            re.MULTILINE,
        )
    )
    if len(rows) != 1:
        raise ValueError(f"{label} matrix must contain exactly one {target} row")
    return text[: rows[0].start()], text[rows[0].end() :]


def _tool_regions(text: str, target: str, label: str) -> tuple[str, str]:
    starts = list(
        re.finditer(
            rf'^ {{12}}\["{re.escape(target)}"\]\s*=\s*"""\r?$',
            text,
            re.MULTILINE,
        )
    )
    if len(starts) != 1:
        raise ValueError(f"{label} CompatibilityTool needs exactly one {target} entry")
    close = re.compile(r'^ {16}""",\r?$', re.MULTILINE).search(text, starts[0].end())
    if close is None:
        raise ValueError(f"{label} CompatibilityTool {target} entry is unterminated")
    block = text[starts[0].start() : close.end()]
    if f"## MAF {target} Compatibility" not in block:
        raise ValueError(f"{label} CompatibilityTool target heading is wrong")
    return text[: starts[0].start()], text[close.end() :]


def _version(value: str, label: str) -> str:
    if not VERSION_RE.fullmatch(value):
        raise ValueError(f"{label} is not stable X.Y.Z: {value!r}")
    return value


def build_obligations(
    root: Path,
    *,
    old_version: str,
    target_version: str,
    base_commit: str,
    target_branch: str,
) -> dict[str, Any]:
    """Create deterministic obligations from the completed watcher scaffold."""
    old, target = _version(old_version, "old version"), _version(target_version, "target version")
    if not SHA_RE.fullmatch(base_commit.lower()):
        raise ValueError("base commit must be a full hexadecimal Git object id")
    if not BRANCH_RE.fullmatch(target_branch) or target_branch in {"main", "master"}:
        raise ValueError("target branch must be a safe non-protected branch")

    version_raw = _bytes(_safe_file(root, ".maf-version", ".maf-version"), ".maf-version", 128)
    if version_raw.decode("utf-8").strip() != target:
        raise ValueError(".maf-version does not match target version")
    registry = _registry(_safe_file(root, REGISTRY_REL, "registry"), "scaffold")
    registry_target = registry.get("target_maf_version")
    if registry_target != target:
        raise ValueError(
            f"registry target_maf_version must match target {target}; got {registry_target!r}"
        )
    last_updated = registry.get("last_updated")
    if not isinstance(last_updated, str):
        raise ValueError("registry last_updated must be a canonical YYYY-MM-DD string")
    try:
        parsed_last_updated = dt.date.fromisoformat(last_updated)
    except ValueError as exc:
        raise ValueError(
            f"registry last_updated is not a valid ISO calendar date: {last_updated!r}"
        ) from exc
    if parsed_last_updated.isoformat() != last_updated:
        raise ValueError(
            f"registry last_updated must use canonical YYYY-MM-DD form: {last_updated!r}"
        )
    generated: list[dict[str, Any]] = []
    seen: set[str] = set()
    for entry in _target_entries(registry, target):
        entry_id, package = _field(entry, "id"), _field(entry, "package")
        if not entry_id or not package:
            raise ValueError("target registry entry lacks id/package")
        if entry_id.casefold() in seen:
            raise ValueError(f"duplicate target registry id {entry_id}")
        seen.add(entry_id.casefold())
        generated.append({"id": entry_id, "package": package, "review": bool(REVIEW_RE.fullmatch(entry_id))})

    target_guide_rel = f"guides/maf-{target}-migration-guide.md"
    target_guide = _text(_safe_file(root, target_guide_rel, "target guide"), "target guide")
    guide_prefix, guide_suffix, evidence = _guide_regions(target_guide, "scaffold")
    historical_guides: list[dict[str, str]] = []
    for path in sorted((root / "guides").glob("maf-*-migration-guide.md"), key=lambda p: p.name):
        if path.name == Path(target_guide_rel).name or not GUIDE_RE.fullmatch(path.name):
            continue
        relative = f"guides/{path.name}"
        historical_guides.append(
            {"path": relative, "sha256": _sha(_bytes(_safe_file(root, relative, "historical guide"), "historical guide"))}
        )

    historical_contracts: list[dict[str, str]] = []
    contracts_dir = root / OBLIGATIONS_DIR
    if contracts_dir.is_dir():
        for path in sorted(contracts_dir.glob("maf-*.json"), key=lambda p: p.name):
            relative = f"{OBLIGATIONS_DIR}/{path.name}"
            if relative == _contract_rel(target):
                continue
            historical_contracts.append(
                {
                    "path": relative,
                    "sha256": _sha(
                        _bytes(_safe_file(root, relative, "historical obligations"), "historical obligations", MAX_OBLIGATIONS_BYTES)
                    ),
                }
            )

    matrix_text = _text(_safe_file(root, MATRIX_REL, "matrix"), "matrix")
    matrix_prefix, matrix_suffix = _matrix_regions(matrix_text, target, "scaffold")
    tool_text = _text(_safe_file(root, TOOL_REL, "CompatibilityTool"), "CompatibilityTool")
    tool_prefix, tool_suffix = _tool_regions(tool_text, target, "scaffold")
    return {
        "schema_version": SCHEMA_VERSION,
        "scaffold": {
            "source": "maf-release-watcher",
            "base_commit": base_commit.lower(),
            "target_branch": target_branch,
            "old_version": old,
            "target_version": target,
        },
        "maf_version": {"path": ".maf-version", "value": target, "sha256": _sha(version_raw)},
        "registry": {
            "path": REGISTRY_REL,
            "historical_sha256": _historical_registry_hash(registry, target),
            "generated_target_entries": generated,
            "review_rule": "replace_each_with_new_concrete_same_package_target_entry",
        },
        "target_guide": {
            "path": target_guide_rel,
            "protected_prefix_sha256": _sha(guide_prefix.encode()),
            "protected_suffix_sha256": _sha(guide_suffix.encode()),
            "evidence_sha256": _sha(evidence.encode()),
        },
        "historical_guides": historical_guides,
        "historical_obligations": historical_contracts,
        "compatibility_matrix": {
            "path": MATRIX_REL,
            "protected_prefix_sha256": _sha(matrix_prefix.encode()),
            "protected_suffix_sha256": _sha(matrix_suffix.encode()),
        },
        "compatibility_tool": {
            "path": TOOL_REL,
            "protected_prefix_sha256": _sha(tool_prefix.encode()),
            "protected_suffix_sha256": _sha(tool_suffix.encode()),
        },
    }


def serialize_obligations(document: dict[str, Any]) -> bytes:
    return (json.dumps(document, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode()


def _object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"obligations {label} must be an object")
    return value


def _path(section: dict[str, Any], expected: str, label: str) -> None:
    if section.get("path") != expected:
        raise ValueError(f"obligations {label} path must be {expected}")


def _check_hash(value: bytes | str, expected: Any, label: str, errors: list[str]) -> None:
    raw = value.encode() if isinstance(value, str) else value
    if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected):
        errors.append(f"obligations {label} hash is malformed")
    elif _sha(raw) != expected:
        errors.append(f"{label} changed outside the permitted fill region")


def verify_obligations(doc: dict[str, Any], head_root: Path, canonical_raw: bytes) -> list[str]:
    """Validate a PR HEAD tree against its canonical obligations blob."""
    errors: list[str] = []
    try:
        if doc.get("schema_version") != SCHEMA_VERSION:
            raise ValueError("unsupported obligations schema_version")
        scaffold = _object(doc.get("scaffold"), "scaffold")
        if scaffold.get("source") != "maf-release-watcher":
            raise ValueError("obligations source is not maf-release-watcher")
        target = _version(str(scaffold.get("target_version", "")), "target version")
        _version(str(scaffold.get("old_version", "")), "old version")
        version_spec = _object(doc.get("maf_version"), "maf_version")
        registry_spec = _object(doc.get("registry"), "registry")
        guide_spec = _object(doc.get("target_guide"), "target_guide")
        matrix_spec = _object(doc.get("compatibility_matrix"), "compatibility_matrix")
        tool_spec = _object(doc.get("compatibility_tool"), "compatibility_tool")
        _path(version_spec, ".maf-version", "maf_version")
        _path(registry_spec, REGISTRY_REL, "registry")
        _path(guide_spec, f"guides/maf-{target}-migration-guide.md", "target guide")
        _path(matrix_spec, MATRIX_REL, "matrix")
        _path(tool_spec, TOOL_REL, "CompatibilityTool")
        if version_spec.get("value") != target:
            raise ValueError("obligations maf_version disagrees with target")
        if registry_spec.get("review_rule") != "replace_each_with_new_concrete_same_package_target_entry":
            raise ValueError("unsupported REVIEW replacement rule")

        contract_rel = _contract_rel(target)
        head_contract = _bytes(
            _safe_file(head_root, contract_rel, "HEAD obligations"),
            "HEAD obligations",
            MAX_OBLIGATIONS_BYTES,
        )
        if head_contract != canonical_raw:
            errors.append("HEAD obligations artifact differs from the immutable scaffold blob")
        version_raw = _bytes(_safe_file(head_root, ".maf-version", ".maf-version"), ".maf-version", 128)
        if version_raw.decode("utf-8").strip() != target:
            errors.append(f".maf-version must remain {target}")
        _check_hash(version_raw, version_spec.get("sha256"), ".maf-version", errors)

        registry = _registry(_safe_file(head_root, REGISTRY_REL, "registry"), "HEAD")
        if _historical_registry_hash(registry, target) != registry_spec.get("historical_sha256"):
            errors.append("historical registry content changed")
        generated = registry_spec.get("generated_target_entries")
        if not isinstance(generated, list):
            raise ValueError("generated_target_entries must be an array")
        head_target = _target_entries(registry, target)
        by_id: dict[str, list[dict[str, Any]]] = {}
        for entry in head_target:
            if _field(entry, "id"):
                by_id.setdefault(_field(entry, "id").casefold(), []).append(entry)
        expected_ids: set[str] = set()
        reviews: Counter[str] = Counter()
        for expected in generated:
            if not isinstance(expected, dict):
                raise ValueError("generated target obligation is not an object")
            entry_id, package = _field(expected, "id"), _field(expected, "package")
            review = expected.get("review")
            if not entry_id or not package or not isinstance(review, bool):
                raise ValueError("generated target obligation lacks id/package/review")
            folded = entry_id.casefold()
            if folded in expected_ids:
                raise ValueError(f"duplicate obligation id {entry_id}")
            expected_ids.add(folded)
            matches = by_id.get(folded, [])
            if review:
                if not REVIEW_RE.fullmatch(entry_id):
                    raise ValueError(f"non-REVIEW id marked as review: {entry_id}")
                if matches:
                    errors.append(f"{entry_id}: REVIEW sentinel must be replaced")
                reviews[package.casefold()] += 1
            else:
                if REVIEW_RE.fullmatch(entry_id):
                    raise ValueError(f"REVIEW id not marked review: {entry_id}")
                if len(matches) != 1:
                    errors.append(f"required target registry id {entry_id} is missing or duplicated")
                elif _field(matches[0], "package").casefold() != package.casefold():
                    errors.append(f"{entry_id}: package identity changed from {package}")
        replacements: Counter[str] = Counter()
        for entry in head_target:
            entry_id, package = _field(entry, "id"), _field(entry, "package")
            if entry_id and package and entry_id.casefold() not in expected_ids and not REVIEW_RE.fullmatch(entry_id):
                replacements[package.casefold()] += 1
        # A REVIEW sentinel means "classified breaking, but no tracked surface
        # produced diff evidence". When the break lives in a MAF package the
        # watcher does not diff (MAF 1.19: Microsoft.Agents.AI.Mcp), the
        # sentinel's package is only the default guess. Concrete entries for
        # untracked Microsoft.Agents.AI* packages may therefore satisfy any
        # sentinel; entries for other *tracked* packages still may not, because
        # those packages had their own diff evidence.
        tracked = _tracked_packages() if reviews else set()
        remaining = Counter(replacements)
        missing_by_package: dict[str, int] = {}
        for package, count in reviews.items():
            own = min(count, remaining[package])
            remaining[package] -= own
            missing_by_package[package] = count - own
        untracked_left = sum(
            count
            for package, count in remaining.items()
            if package not in tracked and package.startswith("microsoft.agents.ai")
        )
        for package, missing in missing_by_package.items():
            used = min(missing, untracked_left)
            untracked_left -= used
            if missing - used > 0:
                count = reviews[package]
                errors.append(
                    f"{count} REVIEW sentinel(s) for {package} require distinct new concrete same-package "
                    f"(or untracked MAF package) {target} entries; found {count - (missing - used)}"
                )

        guide = _text(_safe_file(head_root, str(guide_spec["path"]), "target guide"), "target guide")
        prefix, suffix, evidence = _guide_regions(guide, "HEAD")
        _check_hash(prefix, guide_spec.get("protected_prefix_sha256"), "target-guide prefix", errors)
        _check_hash(suffix, guide_spec.get("protected_suffix_sha256"), "target-guide suffix", errors)
        _check_hash(evidence, guide_spec.get("evidence_sha256"), "target-guide evidence", errors)

        historical = doc.get("historical_guides")
        if not isinstance(historical, list):
            raise ValueError("historical_guides must be an array")
        expected_guides: dict[str, str] = {}
        for item in historical:
            if not isinstance(item, dict):
                raise ValueError("historical guide obligation is not an object")
            relative, digest = item.get("path"), item.get("sha256")
            if not isinstance(relative, str) or not relative.startswith("guides/") or not GUIDE_RE.fullmatch(Path(relative).name):
                raise ValueError(f"invalid historical guide path {relative!r}")
            if relative in expected_guides or not isinstance(digest, str):
                raise ValueError(f"duplicate/malformed historical guide {relative}")
            expected_guides[relative] = digest
        actual_guides = {
            f"guides/{path.name}"
            for path in (head_root / "guides").glob("maf-*-migration-guide.md")
            if path.name != Path(str(guide_spec["path"])).name and GUIDE_RE.fullmatch(path.name)
        }
        if actual_guides != set(expected_guides):
            errors.append("historical migration-guide file set changed")
        for relative, digest in expected_guides.items():
            _check_hash(
                _bytes(_safe_file(head_root, relative, "historical guide"), "historical guide"),
                digest,
                f"historical guide {relative}",
                errors,
            )

        historical_contracts = doc.get("historical_obligations")
        if not isinstance(historical_contracts, list):
            raise ValueError("historical_obligations must be an array")
        expected_contracts: dict[str, str] = {}
        for item in historical_contracts:
            if not isinstance(item, dict):
                raise ValueError("historical obligations item is not an object")
            relative, digest = item.get("path"), item.get("sha256")
            if (
                not isinstance(relative, str)
                or not re.fullmatch(r"\.github/maf-scaffold-obligations/maf-[0-9]+\.[0-9]+\.[0-9]+\.json", relative)
                or not isinstance(digest, str)
                or relative in expected_contracts
            ):
                raise ValueError(f"invalid historical obligations item {relative!r}")
            expected_contracts[relative] = digest
        contract_dir = head_root / OBLIGATIONS_DIR
        actual_contracts = {
            f"{OBLIGATIONS_DIR}/{path.name}"
            for path in contract_dir.glob("maf-*.json")
            if f"{OBLIGATIONS_DIR}/{path.name}" != contract_rel
        } if contract_dir.is_dir() else set()
        if actual_contracts != set(expected_contracts):
            errors.append("historical scaffold-obligations file set changed")
        for relative, digest in expected_contracts.items():
            _check_hash(
                _bytes(
                    _safe_file(head_root, relative, "historical obligations"),
                    "historical obligations",
                    MAX_OBLIGATIONS_BYTES,
                ),
                digest,
                f"historical obligations {relative}",
                errors,
            )

        matrix = _text(_safe_file(head_root, MATRIX_REL, "matrix"), "matrix")
        prefix, suffix = _matrix_regions(matrix, target, "HEAD")
        _check_hash(prefix, matrix_spec.get("protected_prefix_sha256"), "matrix prefix", errors)
        _check_hash(suffix, matrix_spec.get("protected_suffix_sha256"), "matrix suffix", errors)
        tool = _text(_safe_file(head_root, TOOL_REL, "CompatibilityTool"), "CompatibilityTool")
        prefix, suffix = _tool_regions(tool, target, "HEAD")
        _check_hash(prefix, tool_spec.get("protected_prefix_sha256"), "CompatibilityTool prefix", errors)
        _check_hash(suffix, tool_spec.get("protected_suffix_sha256"), "CompatibilityTool suffix", errors)
    except (KeyError, TypeError, UnicodeDecodeError, ValueError) as exc:
        errors.append(str(exc))
    return errors


def _git(repo: Path, *args: str) -> bytes:
    try:
        result = subprocess.run(
            ["git", "-C", str(repo), *args],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=20,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ValueError(f"git {' '.join(args)} failed: {exc}") from exc
    if result.returncode:
        detail = result.stderr.decode(errors="replace").strip()
        raise ValueError(f"git {' '.join(args)} failed: {detail}")
    return result.stdout


def _git_text(repo: Path, *args: str) -> str:
    return _git(repo, *args).decode("utf-8").strip()


def _has(repo: Path, commit: str, relative: str) -> bool:
    try:
        result = subprocess.run(
            ["git", "-C", str(repo), "cat-file", "-e", f"{commit}:{relative}"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=20,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ValueError(f"cannot inspect Git history: {exc}") from exc
    return result.returncode == 0


def recover_canonical_obligations(
    repo: Path, *, base_sha: str, head_sha: str, base_ref: str, head_ref: str
) -> bytes | None:
    """Recover the first contract and prove it stayed byte-identical."""
    if not SHA_RE.fullmatch(base_sha.lower()) or not SHA_RE.fullmatch(head_sha.lower()):
        raise ValueError("base/head SHA must be full hexadecimal object ids")
    if _git_text(repo, "rev-parse", "HEAD").lower() != head_sha.lower():
        raise ValueError("PR data checkout does not match the event HEAD SHA")
    try:
        base_version = _git(repo, "show", f"{base_sha}:.maf-version").decode("utf-8").strip()
        head_version = _git(repo, "show", f"{head_sha}:.maf-version").decode("utf-8").strip()
    except UnicodeDecodeError as exc:
        raise ValueError("base/head .maf-version is not UTF-8") from exc
    _version(base_version, "base .maf-version")
    _version(head_version, "head .maf-version")
    merge_base = _git_text(repo, "merge-base", base_sha, head_sha).lower()
    commits = _git_text(
        repo, "rev-list", "--reverse", "--first-parent", f"{merge_base}..{head_sha}"
    ).splitlines()
    first = commits[0] if commits else ""
    added_contracts: list[str] = []
    if first:
        changes = _git_text(repo, "diff-tree", "--no-commit-id", "--name-status", "-r", first)
        for line in changes.splitlines():
            fields = line.split("\t")
            if len(fields) == 2 and fields[0] == "A" and re.fullmatch(
                r"\.github/maf-scaffold-obligations/maf-[0-9]+\.[0-9]+\.[0-9]+\.json",
                fields[1],
            ):
                added_contracts.append(fields[1])
    if len(added_contracts) > 1:
        raise ValueError("first scaffold commit added multiple obligations artifacts")
    if added_contracts:
        contract_rel = added_contracts[0]
        parents = _git_text(repo, "show", "-s", "--format=%P", first).split()
        if parents != [merge_base]:
            raise ValueError("obligations must be added by the first linear watcher scaffold commit")
        canonical = _git(repo, "show", f"{first}:{contract_rel}")
        expected_branch, previous = head_ref, merge_base
    else:
        touched_contract = False
        for commit in commits:
            names = _git_text(repo, "diff-tree", "--no-commit-id", "--name-only", "-r", commit)
            if any(name.startswith(f"{OBLIGATIONS_DIR}/") for name in names.splitlines()):
                touched_contract = True
                break
        if touched_contract:
            raise ValueError("obligations artifact was not added in the first scaffold commit")
        if base_version != head_version:
            raise ValueError("watcher-managed version bump has no obligations artifact")
        return None
    if len(canonical) > MAX_OBLIGATIONS_BYTES:
        raise ValueError("canonical obligations artifact is oversized")
    try:
        document = json.loads(canonical.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"canonical obligations artifact is invalid JSON: {exc}") from exc
    scaffold = _object(document.get("scaffold") if isinstance(document, dict) else None, "scaffold")
    if scaffold.get("base_commit") != merge_base:
        raise ValueError("obligations base_commit does not match PR merge-base")
    if scaffold.get("target_branch") != expected_branch:
        raise ValueError("obligations target_branch does not match PR topology")
    target = _version(str(scaffold.get("target_version", "")), "contract target version")
    if contract_rel != _contract_rel(target):
        raise ValueError("obligations filename does not match its target version")
    if head_version != target:
        raise ValueError("HEAD .maf-version does not match the scaffold contract target")
    # The watcher records the train's resolved package versions in the train
    # lock in the same first scaffold commit. A filler (human or agent) may
    # never change it afterwards: it pins the next train's diff baseline.
    scaffold_lock = (
        _git(repo, "show", f"{first}:{TRAIN_LOCK_REL}") if _has(repo, first, TRAIN_LOCK_REL) else None
    )
    for commit in commits:
        parents = _git_text(repo, "show", "-s", "--format=%P", commit).split()
        if parents != [previous]:
            raise ValueError("scaffold/fill history must remain linear")
        if not _has(repo, commit, contract_rel):
            raise ValueError(f"obligations artifact is missing at commit {commit}")
        if _git(repo, "show", f"{commit}:{contract_rel}") != canonical:
            raise ValueError(f"obligations artifact changed at commit {commit}")
        if scaffold_lock is not None and (
            not _has(repo, commit, TRAIN_LOCK_REL)
            or _git(repo, "show", f"{commit}:{TRAIN_LOCK_REL}") != scaffold_lock
        ):
            raise ValueError(f"train lock changed after the watcher scaffold commit (at {commit})")
        previous = commit
    return canonical


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--head-root", type=Path, required=True)
    parser.add_argument("--base-sha", required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--base-ref", required=True)
    parser.add_argument("--head-ref", required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        canonical = recover_canonical_obligations(
            args.repo,
            base_sha=args.base_sha,
            head_sha=args.head_sha,
            base_ref=args.base_ref,
            head_ref=args.head_ref,
        )
        if canonical is None:
            print("No watcher version bump/contract applies; ordinary verification remains in force.")
            return 0
        doc = json.loads(canonical.decode("utf-8"))
        errors = verify_obligations(doc, args.head_root, canonical)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as exc:
        errors = [str(exc)]
    if errors:
        print("AI-fill scaffold obligations failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    print("AI-fill scaffold obligations passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
