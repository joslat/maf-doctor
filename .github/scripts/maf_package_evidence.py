#!/usr/bin/env python3
"""Shared, fail-closed reader for release-watcher package evidence.

The checked-in surface manifest and ``build_maf_package_plan.py`` define a
targeted release-critical NuGet surface set. This module is the consumer-side
contract: it validates the deterministic plan/results shape and independently
revalidates every captured ``dotnet-inspect`` report before downstream scripts
may use it for classification, guides, the matrix, or registry extraction.
"""
from __future__ import annotations

import json
import pathlib
import re
from typing import Any

from validate_dotnet_inspect_diff import MAX_DIFF_FILE_BYTES, validate_diff_file


PLAN_FILENAME = "maf-package-plan.json"
RESULTS_FILENAME = "maf-diff-results.json"
SCHEMA_VERSION = 1
# Bound on planned surfaces (defense against an oversized/forged plan). MAF ships
# ~33 train-aligned Microsoft.Agents.AI* packages (2026-10); 64 leaves headroom.
MAX_SURFACES = 64
MAX_METADATA_BYTES = 1024 * 1024

_STATUS_VALUES = {"diffable", "unverifiable", "informational"}
_RESULT_VALUES = {"passed", "failed", "not_applicable"}
_EVENT_KINDS = {"package_split", "repository_externalization"}
_SLUG_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_PACKAGE_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_VERSION_RE = re.compile(
    r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$"
)
_ID_SCOPE_RE = re.compile(r"^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$")
_REPOSITORY_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


class PackageEvidenceError(ValueError):
    """The plan/results contract is absent, malformed, or internally inconsistent."""


def _load_json(path: pathlib.Path, label: str) -> dict[str, Any]:
    try:
        size = path.stat().st_size
    except FileNotFoundError as exc:
        raise PackageEvidenceError(f"{label} does not exist: {path}") from exc
    if size > MAX_METADATA_BYTES:
        raise PackageEvidenceError(
            f"{label} exceeds the {MAX_METADATA_BYTES}-byte metadata limit: {path}"
        )
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PackageEvidenceError(f"cannot read {label} {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise PackageEvidenceError(f"{label} root must be a JSON object")
    return value


def _require_string(value: Any, field: str, *, allow_empty: bool = False) -> str:
    if not isinstance(value, str) or (not allow_empty and not value):
        suffix = "string" if allow_empty else "non-empty string"
        raise PackageEvidenceError(f"{field} must be a {suffix}")
    return value


def _validate_version(value: Any, field: str) -> str:
    version = _require_string(value, field)
    if not _VERSION_RE.fullmatch(version):
        raise PackageEvidenceError(f"{field} is not a supported package version: {version!r}")
    return version


def _validate_package_items(items: Any, field: str) -> None:
    if not isinstance(items, list) or not items:
        raise PackageEvidenceError(f"{field} must be a non-empty array")
    for index, item in enumerate(items):
        prefix = f"{field}[{index}]"
        if not isinstance(item, dict):
            raise PackageEvidenceError(f"{prefix} must be an object")
        package = _require_string(item.get("package"), f"{prefix}.package")
        if not _PACKAGE_RE.fullmatch(package):
            raise PackageEvidenceError(f"{prefix}.package is malformed: {package!r}")
        if item.get("version") is not None:
            _validate_version(item["version"], f"{prefix}.version")


def load_package_plan(path: pathlib.Path | str = PLAN_FILENAME) -> dict[str, Any]:
    """Load and strictly validate a deterministic package plan."""
    plan_path = pathlib.Path(path)
    plan = _load_json(plan_path, "package plan")
    if plan.get("schema_version") != SCHEMA_VERSION:
        raise PackageEvidenceError(
            f"package plan schema_version must be {SCHEMA_VERSION}"
        )

    release = plan.get("release")
    if not isinstance(release, dict):
        raise PackageEvidenceError("package plan release must be an object")
    old_release = _validate_version(release.get("old_version"), "release.old_version")
    new_release = _validate_version(release.get("new_version"), "release.new_version")
    if old_release == new_release:
        raise PackageEvidenceError("package plan release versions must differ")

    surfaces = plan.get("surfaces")
    if not isinstance(surfaces, list) or not surfaces:
        raise PackageEvidenceError("package plan surfaces must be a non-empty array")
    if len(surfaces) > MAX_SURFACES:
        raise PackageEvidenceError(
            f"package plan has {len(surfaces)} surfaces; maximum is {MAX_SURFACES}"
        )

    seen_slugs: set[str] = set()
    seen_packages: set[str] = set()
    seen_scopes: set[str] = set()
    seen_artifacts: set[str] = set()
    diffable_count = 0
    for index, surface in enumerate(surfaces):
        prefix = f"surfaces[{index}]"
        if not isinstance(surface, dict):
            raise PackageEvidenceError(f"{prefix} must be an object")
        if surface.get("order") != index:
            raise PackageEvidenceError(
                f"{prefix}.order must be contiguous manifest order {index}"
            )
        slug = _require_string(surface.get("slug"), f"{prefix}.slug")
        if not _SLUG_RE.fullmatch(slug):
            raise PackageEvidenceError(f"{prefix}.slug is malformed: {slug!r}")
        if slug in seen_slugs:
            raise PackageEvidenceError(f"duplicate surface slug: {slug}")
        seen_slugs.add(slug)

        package = _require_string(surface.get("package"), f"{prefix}.package")
        if not _PACKAGE_RE.fullmatch(package):
            raise PackageEvidenceError(f"{prefix}.package is malformed: {package!r}")
        package_key = package.casefold()
        if package_key in seen_packages:
            raise PackageEvidenceError(f"duplicate surface package: {package}")
        seen_packages.add(package_key)
        status = surface.get("status")
        if status not in _STATUS_VALUES:
            raise PackageEvidenceError(
                f"{prefix}.status must be one of {sorted(_STATUS_VALUES)}"
            )
        id_scope = _require_string(
            surface.get("id_scope"), f"{prefix}.id_scope", allow_empty=True
        )
        if slug == "core":
            if id_scope:
                raise PackageEvidenceError("the core surface id_scope must be empty")
        elif not _ID_SCOPE_RE.fullmatch(id_scope):
            raise PackageEvidenceError(
                f"non-Core surface {slug!r} must have a safe non-empty id_scope"
            )
        if id_scope:
            scope_key = id_scope.casefold()
            if scope_key in seen_scopes:
                raise PackageEvidenceError(f"duplicate non-Core id_scope: {id_scope}")
            seen_scopes.add(scope_key)

        diff_artifact = _require_string(
            surface.get("diff_artifact"), f"{prefix}.diff_artifact"
        )
        stderr_artifact = _require_string(
            surface.get("stderr_artifact"), f"{prefix}.stderr_artifact"
        )
        expected_diff = f"diff-{slug}.txt"
        expected_stderr = f"diff-{slug}.stderr.txt"
        if diff_artifact != expected_diff or stderr_artifact != expected_stderr:
            raise PackageEvidenceError(
                f"{prefix} artifact names must be {expected_diff!r} and "
                f"{expected_stderr!r}"
            )
        for artifact in (diff_artifact, stderr_artifact):
            if artifact in seen_artifacts:
                raise PackageEvidenceError(f"duplicate planned artifact: {artifact}")
            seen_artifacts.add(artifact)

        _require_string(surface.get("reason"), f"{prefix}.reason")
        if status == "diffable":
            diffable_count += 1
            _validate_version(
                surface.get("old_package_version"),
                f"{prefix}.old_package_version",
            )
            _validate_version(
                surface.get("new_package_version"),
                f"{prefix}.new_package_version",
            )
        else:
            for field in ("old_package_version", "new_package_version"):
                # The planner retains any side it resolved (useful provenance
                # for an expected externalization) and writes null for the
                # unresolved side. Non-diffable rows are never executed.
                if surface.get(field) is not None:
                    _validate_version(surface[field], f"{prefix}.{field}")

    if diffable_count == 0:
        raise PackageEvidenceError("package plan must contain at least one diffable surface")

    events = plan.get("lifecycle_events")
    if not isinstance(events, list):
        raise PackageEvidenceError("package plan lifecycle_events must be an array")
    seen_event_ids: set[str] = set()
    for index, event in enumerate(events):
        prefix = f"lifecycle_events[{index}]"
        if not isinstance(event, dict):
            raise PackageEvidenceError(f"{prefix} must be an object")
        event_id = _require_string(event.get("id"), f"{prefix}.id")
        if event_id in seen_event_ids:
            raise PackageEvidenceError(f"duplicate lifecycle event id: {event_id}")
        seen_event_ids.add(event_id)
        kind = event.get("kind")
        if kind not in _EVENT_KINDS:
            raise PackageEvidenceError(
                f"{prefix}.kind must be one of {sorted(_EVENT_KINDS)}"
            )
        if event.get("impact") not in {"breaking", "informational"}:
            raise PackageEvidenceError(
                f"{prefix}.impact must be 'breaking' or 'informational'"
            )
        if event.get("release_version") != new_release:
            raise PackageEvidenceError(
                f"{prefix}.release_version must equal release.new_version"
            )
        related = event.get("related_surfaces")
        if not isinstance(related, list) or not related:
            raise PackageEvidenceError(f"{prefix}.related_surfaces must be non-empty")
        if any(not isinstance(slug, str) or slug not in seen_slugs for slug in related):
            raise PackageEvidenceError(
                f"{prefix}.related_surfaces contains an unknown surface"
            )
        _validate_package_items(event.get("source_packages"), f"{prefix}.source_packages")
        _validate_package_items(event.get("target_packages"), f"{prefix}.target_packages")
        _require_string(event.get("reason"), f"{prefix}.reason")
        if kind == "package_split" and event["impact"] != "breaking":
            raise PackageEvidenceError(
                f"{prefix} package_split impact must be 'breaking'"
            )
        if kind == "repository_externalization":
            if event["impact"] != "informational":
                raise PackageEvidenceError(
                    f"{prefix} repository_externalization impact must be informational"
                )
            source_repository = _require_string(
                event.get("source_repository"), f"{prefix}.source_repository"
            )
            target_repository = _require_string(
                event.get("target_repository"), f"{prefix}.target_repository"
            )
            if not _REPOSITORY_RE.fullmatch(source_repository):
                raise PackageEvidenceError(
                    f"{prefix}.source_repository must be owner/repository"
                )
            if not _REPOSITORY_RE.fullmatch(target_repository):
                raise PackageEvidenceError(
                    f"{prefix}.target_repository must be owner/repository"
                )
            if event.get("same_package_ids") is not True:
                raise PackageEvidenceError(
                    f"{prefix}.same_package_ids must be true"
                )
            source_ids = {
                ref["package"].casefold() for ref in event["source_packages"]
            }
            target_ids = {
                ref["package"].casefold() for ref in event["target_packages"]
            }
            if source_ids != target_ids:
                raise PackageEvidenceError(
                    f"{prefix} same_package_ids requires identical package IDs"
                )

    return plan


def load_diff_results(path: pathlib.Path | str = RESULTS_FILENAME) -> dict[str, Any]:
    """Load a results document; cross-document validation happens separately."""
    return _load_json(pathlib.Path(path), "diff results")


def validate_results_document(
    results: dict[str, Any], plan: dict[str, Any]
) -> list[str]:
    """Return all plan/results reconciliation errors without raising."""
    errors: list[str] = []
    if results.get("schema_version") != SCHEMA_VERSION:
        errors.append(f"diff results schema_version must be {SCHEMA_VERSION}")
    if results.get("release") != plan.get("release"):
        errors.append("diff results release does not match the package plan")
    rows = results.get("surfaces")
    if not isinstance(rows, list):
        return errors + ["diff results surfaces must be an array"]
    planned = plan["surfaces"]
    if len(rows) != len(planned):
        errors.append(
            f"diff results contain {len(rows)} surfaces; plan requires {len(planned)}"
        )
    for index, surface in enumerate(planned):
        if index >= len(rows):
            errors.append(f"missing diff result for surface {surface['slug']!r}")
            continue
        row = rows[index]
        if not isinstance(row, dict):
            errors.append(f"diff results surfaces[{index}] must be an object")
            continue
        for field in (
            "order",
            "slug",
            "package",
            "status",
            "diff_artifact",
            "stderr_artifact",
        ):
            if row.get(field) != surface.get(field):
                errors.append(
                    f"diff result {surface['slug']!r} field {field!r} does not match plan"
                )
        validation_status = row.get("validation_status")
        if validation_status not in _RESULT_VALUES:
            errors.append(
                f"diff result {surface['slug']!r} has invalid validation_status"
            )
        validation_errors = row.get("validation_errors")
        if not isinstance(validation_errors, list) or any(
            not isinstance(item, str) or not item for item in validation_errors or []
        ):
            errors.append(
                f"diff result {surface['slug']!r} validation_errors must be non-empty strings"
            )
        if not isinstance(row.get("attempted"), bool):
            errors.append(
                f"diff result {surface['slug']!r} attempted must be a boolean"
            )
        tool_exit = row.get("tool_exit_code")
        if tool_exit is not None and (
            not isinstance(tool_exit, int) or isinstance(tool_exit, bool)
        ):
            errors.append(
                f"diff result {surface['slug']!r} tool_exit_code must be an integer or null"
            )
        if surface["status"] != "diffable" and (
            row.get("attempted") is not False
            or row.get("tool_exit_code") is not None
            or row.get("validation_status") != "not_applicable"
        ):
            errors.append(
                f"non-diffable result {surface['slug']!r} must be not_applicable"
            )
        if surface["status"] == "diffable" and validation_status == "not_applicable":
            errors.append(
                f"diffable result {surface['slug']!r} cannot be not_applicable"
            )
        if validation_status == "passed" and validation_errors:
            errors.append(
                f"passed diff result {surface['slug']!r} cannot contain validation errors"
            )
        if validation_status == "passed" and (
            row.get("attempted") is not True or tool_exit != 0
        ):
            errors.append(
                f"passed diff result {surface['slug']!r} requires attempted=true and exit 0"
            )
        if validation_status == "failed" and not validation_errors:
            errors.append(
                f"failed diff result {surface['slug']!r} requires validation errors"
            )
        if validation_status == "not_applicable" and validation_errors:
            errors.append(
                f"not_applicable diff result {surface['slug']!r} cannot contain validation errors"
            )
    if len(rows) > len(planned):
        errors.append("diff results contain unplanned trailing surfaces")
    return errors


def evaluate_package_evidence(
    plan: dict[str, Any],
    results: dict[str, Any],
    base_dir: pathlib.Path | str = ".",
) -> dict[str, Any]:
    """Reconcile and independently validate every planned surface.

    ``trustworthy`` is true only when every diffable surface passed the runner,
    still passes the strict report validator, and no surface is unverifiable.
    Informational lifecycle-only surfaces do not require an API diff.
    """
    root = pathlib.Path(base_dir)
    document_errors = validate_results_document(results, plan)
    result_rows = results.get("surfaces")
    rows_by_slug: dict[str, dict[str, Any]] = {}
    if isinstance(result_rows, list):
        for row in result_rows:
            if isinstance(row, dict) and isinstance(row.get("slug"), str):
                rows_by_slug.setdefault(row["slug"], row)

    evaluated = []
    required_ok = not document_errors
    combined = []
    for surface in plan["surfaces"]:
        artifact_path = root / surface["diff_artifact"]
        try:
            with artifact_path.open("rb") as stream:
                raw = stream.read(MAX_DIFF_FILE_BYTES + 1)
            if len(raw) > MAX_DIFF_FILE_BYTES:
                raw_text = "(diff artifact exceeds the trusted evidence limit)"
            else:
                raw_text = raw.decode("utf-8")
        except (OSError, UnicodeDecodeError):
            raw_text = ""
        row = rows_by_slug.get(surface["slug"])
        errors: list[str] = []
        trusted = False
        if surface["status"] == "diffable":
            if row is None:
                errors.append("result row is missing")
            else:
                if row.get("validation_status") != "passed":
                    errors.append("runner did not mark this diff as passed")
                if row.get("attempted") is not True:
                    errors.append("dotnet-inspect was not attempted")
                if row.get("tool_exit_code") != 0:
                    errors.append(
                        f"dotnet-inspect exit was {row.get('tool_exit_code')!r}, not 0"
                    )
            errors.extend(
                validate_diff_file(
                    artifact_path,
                    surface["package"],
                    surface["old_package_version"],
                    surface["new_package_version"],
                )
            )
            trusted = not errors
            if trusted:
                combined.append(raw_text)
            else:
                required_ok = False
        elif surface["status"] == "unverifiable":
            errors.append(surface["reason"])
            required_ok = False
        else:
            trusted = True

        evaluated.append(
            {
                **surface,
                "trusted": trusted,
                "errors": errors,
                "text": raw_text,
                "result": row,
            }
        )

    breaking_events = [
        event for event in plan["lifecycle_events"] if event["impact"] == "breaking"
    ]
    informational_events = [
        event
        for event in plan["lifecycle_events"]
        if event["impact"] == "informational"
    ]
    return {
        "trustworthy": required_ok,
        "document_errors": document_errors,
        "surfaces": evaluated,
        "combined_diff_text": "\n".join(combined),
        "lifecycle_events": plan["lifecycle_events"],
        "breaking_lifecycle_events": breaking_events,
        "informational_lifecycle_events": informational_events,
    }


def load_evidence_from_files(
    plan_path: pathlib.Path | str = PLAN_FILENAME,
    results_path: pathlib.Path | str = RESULTS_FILENAME,
    base_dir: pathlib.Path | str = ".",
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Load the two documents and return ``(plan, evaluated_evidence)``."""
    plan = load_package_plan(plan_path)
    results = load_diff_results(results_path)
    return plan, evaluate_package_evidence(plan, results, base_dir)


def write_results(path: pathlib.Path | str, results: dict[str, Any]) -> None:
    """Write deterministic JSON, replacing the file after each surface."""
    target = pathlib.Path(path)
    target.write_text(
        json.dumps(results, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
