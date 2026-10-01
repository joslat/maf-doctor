#!/usr/bin/env python3
"""Build a deterministic, package-aware MAF release diff plan.

The MAF release number is a release *train*, not necessarily the literal NuGet
version of every package in that train. Stable surfaces use ``X.Y.Z`` while
preview surfaces may publish ``X.Y.Z-preview.DATE.1`` or
``X.Y.Z-alpha.DATE.1``. This helper resolves each checked-in surface against
its own NuGet flat-container index and records the exact versions that later
workflow stages must diff.

Resolution is deliberately fail-closed: an exact version wins; otherwise one
and only one aligned ``X.Y.Z-*`` version is accepted. Missing, ambiguous, or
unreachable package evidence is recorded as ``unverifiable`` and is never
guessed away. Explicit lifecycle events can turn an expected missing target
into ``informational`` (for example, the MAF 1.17 Durable/Azure Functions
repository externalization). An externalization is a lasting state: every later
train treats the moved surfaces as informational unless both sides happen to
resolve again, in which case they are diffed normally.
"""
from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable
from urllib.parse import quote
from urllib.request import Request, urlopen


SCHEMA_VERSION = 1
DEFAULT_MANIFEST = Path(__file__).resolve().parents[1] / "maf-package-surfaces.json"
DEFAULT_TRAIN_LOCK = Path(__file__).resolve().parents[1] / "maf-train-lock.json"
DEFAULT_OUTPUT = Path("maf-package-plan.json")
NUGET_INDEX = "https://api.nuget.org/v3-flatcontainer/{package}/index.json"
MAX_NUGET_INDEX_BYTES = 2 * 1024 * 1024

_PACKAGE_RE = re.compile(r"^[A-Za-z0-9._-]+$")
_SLUG_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_ID_SCOPE_RE = re.compile(r"^[A-Z][A-Z0-9-]{0,31}$")
_VERSION_RE = re.compile(
    r"^[0-9]+\.[0-9]+\.[0-9]+"
    r"(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?"
    r"(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$"
)
_EVENT_ID_RE = re.compile(r"^[a-z0-9]+(?:[.-][a-z0-9]+)*$")
_REPOSITORY_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")

_EVENT_KINDS = {"package_split", "repository_externalization"}
_EVENT_IMPACTS = {"breaking", "informational"}


class ManifestValidationError(ValueError):
    """Raised when the checked-in package-surface manifest is malformed."""


class TrainLockError(ValueError):
    """Raised when the checked-in train lock is malformed."""


class NuGetIndexError(RuntimeError):
    """Raised when a NuGet flat-container response cannot be trusted."""


@dataclass(frozen=True)
class VersionResolution:
    """Result of resolving one release train against one package index."""

    value: str | None
    state: str
    reason: str


def _require_mapping(value: object, location: str) -> dict:
    if not isinstance(value, dict):
        raise ManifestValidationError(f"{location} must be an object")
    return value


def _require_nonempty_string(value: object, location: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ManifestValidationError(f"{location} must be a non-empty string")
    return value


def _validate_package_id(value: object, location: str) -> str:
    package = _require_nonempty_string(value, location)
    if not _PACKAGE_RE.fullmatch(package):
        raise ManifestValidationError(
            f"{location} must contain only letters, digits, '.', '_', or '-'"
        )
    return package


def _validate_version(value: object, location: str) -> str:
    version = _require_nonempty_string(value, location)
    if not _VERSION_RE.fullmatch(version):
        raise ManifestValidationError(f"{location} is not a supported SemVer: {version!r}")
    return version


def _validate_package_refs(value: object, location: str) -> list[dict]:
    if not isinstance(value, list) or not value:
        raise ManifestValidationError(f"{location} must be a non-empty array")

    refs: list[dict] = []
    seen: set[str] = set()
    for index, raw_ref in enumerate(value):
        ref = _require_mapping(raw_ref, f"{location}[{index}]")
        package = _validate_package_id(
            ref.get("package"), f"{location}[{index}].package"
        )
        package_key = package.casefold()
        if package_key in seen:
            raise ManifestValidationError(
                f"{location} repeats package {package!r}"
            )
        seen.add(package_key)

        if "version" in ref:
            _validate_version(ref.get("version"), f"{location}[{index}].version")
        refs.append(ref)
    return refs


def validate_manifest(manifest: object) -> dict:
    """Validate and return a package lifecycle manifest.

    The function does not mutate or normalize the input. Keeping checked-in
    list order intact makes the emitted plan byte-for-byte deterministic.
    """

    root = _require_mapping(manifest, "manifest")
    if root.get("schema_version") != SCHEMA_VERSION:
        raise ManifestValidationError(
            f"manifest.schema_version must be {SCHEMA_VERSION}"
        )

    surfaces = root.get("surfaces")
    if not isinstance(surfaces, list) or not surfaces:
        raise ManifestValidationError("manifest.surfaces must be a non-empty array")

    slugs: set[str] = set()
    packages: set[str] = set()
    scopes: set[str] = set()
    orders: list[int] = []

    for index, raw_surface in enumerate(surfaces):
        location = f"manifest.surfaces[{index}]"
        surface = _require_mapping(raw_surface, location)

        order = surface.get("order")
        if not isinstance(order, int) or isinstance(order, bool):
            raise ManifestValidationError(f"{location}.order must be an integer")
        orders.append(order)

        slug = _require_nonempty_string(surface.get("slug"), f"{location}.slug")
        if not _SLUG_RE.fullmatch(slug):
            raise ManifestValidationError(
                f"{location}.slug must be lowercase kebab-case"
            )
        if slug in slugs:
            raise ManifestValidationError(f"duplicate surface slug: {slug!r}")
        slugs.add(slug)

        package = _validate_package_id(surface.get("package"), f"{location}.package")
        package_key = package.casefold()
        if package_key in packages:
            raise ManifestValidationError(f"duplicate surface package: {package!r}")
        packages.add(package_key)

        scope = surface.get("id_scope")
        if not isinstance(scope, str):
            raise ManifestValidationError(f"{location}.id_scope must be a string")
        if slug == "core":
            if scope != "":
                raise ManifestValidationError(
                    "the core surface must use an empty id_scope for legacy ID compatibility"
                )
        elif not _ID_SCOPE_RE.fullmatch(scope):
            raise ManifestValidationError(
                f"{location}.id_scope must be a non-empty uppercase ID segment"
            )
        if scope:
            if scope in scopes:
                raise ManifestValidationError(f"duplicate non-empty id_scope: {scope!r}")
            scopes.add(scope)

    expected_orders = list(range(len(surfaces)))
    if orders != expected_orders:
        raise ManifestValidationError(
            "surface order must be contiguous, unique, zero-based, and match manifest "
            f"list order (expected {expected_orders}, got {orders})"
        )

    events = root.get("lifecycle_events")
    if not isinstance(events, list):
        raise ManifestValidationError("manifest.lifecycle_events must be an array")

    event_ids: set[str] = set()
    for index, raw_event in enumerate(events):
        location = f"manifest.lifecycle_events[{index}]"
        event = _require_mapping(raw_event, location)

        event_id = _require_nonempty_string(event.get("id"), f"{location}.id")
        if not _EVENT_ID_RE.fullmatch(event_id):
            raise ManifestValidationError(
                f"{location}.id must be lowercase and contain only letters, digits, '.', or '-'"
            )
        if event_id in event_ids:
            raise ManifestValidationError(f"duplicate lifecycle event id: {event_id!r}")
        event_ids.add(event_id)

        _validate_version(event.get("release_version"), f"{location}.release_version")

        kind = event.get("kind")
        if kind not in _EVENT_KINDS:
            raise ManifestValidationError(
                f"{location}.kind must be one of {sorted(_EVENT_KINDS)}"
            )
        impact = event.get("impact")
        if impact not in _EVENT_IMPACTS:
            raise ManifestValidationError(
                f"{location}.impact must be one of {sorted(_EVENT_IMPACTS)}"
            )

        raw_related = event.get("related_surfaces")
        if not isinstance(raw_related, list) or not raw_related:
            raise ManifestValidationError(
                f"{location}.related_surfaces must be a non-empty array"
            )
        related = [
            _require_nonempty_string(
                slug, f"{location}.related_surfaces[{related_index}]"
            )
            for related_index, slug in enumerate(raw_related)
        ]
        if len(related) != len(set(related)):
            raise ManifestValidationError(
                f"{location}.related_surfaces contains duplicates"
            )
        unknown = [slug for slug in related if slug not in slugs]
        if unknown:
            raise ManifestValidationError(
                f"{location}.related_surfaces references unknown slug(s): {unknown}"
            )

        source_refs = _validate_package_refs(
            event.get("source_packages"), f"{location}.source_packages"
        )
        target_refs = _validate_package_refs(
            event.get("target_packages"), f"{location}.target_packages"
        )
        _require_nonempty_string(event.get("reason"), f"{location}.reason")

        if kind == "package_split" and impact != "breaking":
            raise ManifestValidationError(
                f"{location}: package_split events must have breaking impact"
            )

        if kind == "repository_externalization":
            if impact != "informational":
                raise ManifestValidationError(
                    f"{location}: repository_externalization must be informational"
                )
            source_repo = _require_nonempty_string(
                event.get("source_repository"), f"{location}.source_repository"
            )
            target_repo = _require_nonempty_string(
                event.get("target_repository"), f"{location}.target_repository"
            )
            if not _REPOSITORY_RE.fullmatch(source_repo):
                raise ManifestValidationError(
                    f"{location}.source_repository must be an owner/repository name"
                )
            if not _REPOSITORY_RE.fullmatch(target_repo):
                raise ManifestValidationError(
                    f"{location}.target_repository must be an owner/repository name"
                )
            if event.get("same_package_ids") is not True:
                raise ManifestValidationError(
                    f"{location}.same_package_ids must be true"
                )
            source_ids = {ref["package"].casefold() for ref in source_refs}
            target_ids = {ref["package"].casefold() for ref in target_refs}
            if source_ids != target_ids:
                raise ManifestValidationError(
                    f"{location}: same_package_ids=true requires identical source/target package IDs"
                )

    return root


def load_manifest(path: Path) -> dict:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ManifestValidationError(f"{path} is not valid JSON: {exc}") from exc
    return validate_manifest(raw)


def resolve_train_version(
    versions: Iterable[str], release_version: str
) -> VersionResolution:
    """Resolve one exact package version for a MAF release train.

    An exact version is always preferred. Prefix fallback is permitted only for
    a stable three-part release train; an explicitly requested prerelease must
    itself exist and is never widened to another prerelease build.
    """

    if not _VERSION_RE.fullmatch(release_version):
        raise ValueError(f"unsupported release version: {release_version!r}")

    unique_versions = sorted(set(versions), key=str.casefold)
    invalid = [version for version in unique_versions if not _VERSION_RE.fullmatch(version)]
    if invalid:
        raise ValueError(f"package index contains invalid version(s): {invalid}")

    if release_version in unique_versions:
        return VersionResolution(
            release_version,
            "resolved",
            f"exact package version {release_version} exists",
        )

    semver_core = release_version.split("+", 1)[0]
    if "-" in semver_core:
        return VersionResolution(
            None,
            "missing",
            f"exact prerelease package version {release_version} does not exist",
        )

    aligned = [version for version in unique_versions if version.startswith(release_version + "-")]
    if len(aligned) == 1:
        return VersionResolution(
            aligned[0],
            "resolved",
            f"single aligned prerelease {aligned[0]} selected for train {release_version}",
        )
    if not aligned:
        return VersionResolution(
            None,
            "missing",
            f"no exact or aligned package version exists for train {release_version}",
        )
    return VersionResolution(
        None,
        "ambiguous",
        f"multiple aligned package versions exist for train {release_version}: {aligned}",
    )


def fetch_nuget_versions(
    package: str,
    *,
    opener: Callable = urlopen,
    timeout: int = 30,
) -> list[str]:
    """Fetch and validate a package's NuGet flat-container version index."""

    if not _PACKAGE_RE.fullmatch(package):
        raise ValueError(f"invalid NuGet package id: {package!r}")
    url = NUGET_INDEX.format(package=quote(package.casefold(), safe="._-"))
    request = Request(url, headers={"User-Agent": "maf-doctor-release-watcher/1"})
    try:
        with opener(request, timeout=timeout) as response:
            raw = response.read(MAX_NUGET_INDEX_BYTES + 1)
    except Exception as exc:
        raise NuGetIndexError(f"NuGet request failed for {package}: {_safe_error(exc)}") from exc

    if len(raw) > MAX_NUGET_INDEX_BYTES:
        raise NuGetIndexError(
            f"NuGet index for {package} exceeds {MAX_NUGET_INDEX_BYTES} bytes"
        )

    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise NuGetIndexError(f"NuGet returned invalid JSON for {package}: {_safe_error(exc)}") from exc

    versions = data.get("versions") if isinstance(data, dict) else None
    if not isinstance(versions, list) or not all(isinstance(v, str) for v in versions):
        raise NuGetIndexError(
            f"NuGet index for {package} does not contain a string versions array"
        )
    invalid = [version for version in versions if not _VERSION_RE.fullmatch(version)]
    if invalid:
        raise NuGetIndexError(
            f"NuGet index for {package} contains invalid version value(s): {invalid[:3]}"
        )
    return versions


def _safe_error(exc: BaseException) -> str:
    """Bound untrusted network/parser exception text before writing plan evidence."""

    text = " ".join(str(exc).split()) or type(exc).__name__
    return text[:300]


def _network_failure_resolution(package: str, exc: BaseException) -> VersionResolution:
    return VersionResolution(
        None,
        "network_error",
        f"NuGet index lookup failed for {package}: {_safe_error(exc)}",
    )


def _events_for_release(manifest: dict, new_version: str) -> list[dict]:
    return [
        copy.deepcopy(event)
        for event in manifest["lifecycle_events"]
        if event["release_version"] == new_version
    ]


def _release_train_key(version: str) -> tuple[int, int, int]:
    """Return the numeric three-part train key from a validated SemVer."""

    core = version.split("-", 1)[0].split("+", 1)[0]
    return tuple(int(part) for part in core.split("."))


def _informational_externalization(
    events: list[dict], surface_slug: str
) -> dict | None:
    matches = [
        event
        for event in events
        if event["kind"] == "repository_externalization"
        and event["impact"] == "informational"
        and surface_slug in event["related_surfaces"]
    ]
    return matches[0] if matches else None


def _pinned_source_resolution(
    event: dict,
    package: str,
    fallback: VersionResolution,
    available: list[str] | None,
) -> VersionResolution:
    """Use the exact source version an externalization event records.

    Upstream may republish another prerelease for the source train after the
    move (Microsoft re-released ``1.16.0-preview.*`` from the new repository),
    which makes train alignment ambiguous. The manifest's pinned
    ``source_packages[].version`` is explicit evidence, not a guess, so it wins
    when it exists in the package index.
    """

    if not available:
        return fallback
    for ref in event.get("source_packages", []):
        pinned = ref.get("version")
        if ref["package"].casefold() != package.casefold() or not pinned:
            continue
        if pinned in available:
            return VersionResolution(
                pinned,
                "resolved",
                f"source version {pinned} pinned by lifecycle event {event['id']}",
            )
    return fallback


def load_train_lock(path: Path | None) -> dict:
    """Load and validate the train lock; a missing file is an empty lock.

    The lock records the exact package versions the watcher resolved when it
    processed each MAF train. Upstream can republish another prerelease for an
    already-processed train later (Microsoft re-released 1.16.0-preview.* from
    a new repository), which turns train alignment ambiguous after the fact.
    Recorded versions are evidence of what was actually diffed, so they win
    for the *old* side of the next train.
    """

    if path is None or not path.exists():
        return {"schema_version": SCHEMA_VERSION, "trains": {}}
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise TrainLockError(f"{path} is not valid JSON: {exc}") from exc
    if not isinstance(raw, dict) or raw.get("schema_version") != SCHEMA_VERSION:
        raise TrainLockError(f"train lock schema_version must be {SCHEMA_VERSION}")
    trains = raw.get("trains")
    if not isinstance(trains, dict):
        raise TrainLockError("train lock trains must be an object")
    for train, packages in trains.items():
        if not isinstance(train, str) or not _VERSION_RE.fullmatch(train) or "-" in train:
            raise TrainLockError(f"train lock key {train!r} is not a stable X.Y.Z train")
        if not isinstance(packages, dict) or not packages:
            raise TrainLockError(f"train lock entry {train} must be a non-empty object")
        for package, version in packages.items():
            if not isinstance(package, str) or not _PACKAGE_RE.fullmatch(package):
                raise TrainLockError(f"train lock {train}: invalid package id {package!r}")
            if not isinstance(version, str) or not _VERSION_RE.fullmatch(version):
                raise TrainLockError(
                    f"train lock {train}/{package}: invalid version {version!r}"
                )
    return raw


def _locked_resolution(
    lock: dict | None,
    train: str,
    package: str,
    available: list[str] | None,
    fallback: VersionResolution,
) -> VersionResolution:
    if not lock or available is None:
        return fallback
    entry = lock.get("trains", {}).get(train, {})
    pinned = next(
        (version for name, version in entry.items() if name.casefold() == package.casefold()),
        None,
    )
    if pinned is None or pinned not in available:
        return fallback
    return VersionResolution(
        pinned, "resolved", f"version {pinned} recorded in the train lock for MAF {train}"
    )


def _introduced_after(available: list[str] | None, old_version: str) -> bool:
    """True when every published version belongs to a train newer than ``old_version``."""

    if not available:
        return False
    old_key = _release_train_key(old_version)
    return all(_release_train_key(version) > old_key for version in available)


def _prior_externalizations(manifest: dict, new_version: str) -> dict[str, dict]:
    """Map each surface slug to the earliest externalization before ``new_version``.

    An externalization is a lasting lifecycle state, not a one-release event:
    once a surface leaves the MAF release train, later trains must not expect
    a train-aligned package for it. Only events from strictly earlier trains
    qualify; the transition train itself keeps the stricter
    ``_informational_externalization`` rule (old side resolved, new missing).
    """

    new_key = _release_train_key(new_version)
    prior: dict[str, dict] = {}
    for event in manifest["lifecycle_events"]:
        if event["kind"] != "repository_externalization":
            continue
        if event["impact"] != "informational":
            continue
        if _release_train_key(event["release_version"]) >= new_key:
            continue
        for slug in event["related_surfaces"]:
            current = prior.get(slug)
            if current is None or _release_train_key(
                event["release_version"]
            ) < _release_train_key(current["release_version"]):
                prior[slug] = event
    return prior


def build_plan(
    manifest: dict,
    old_version: str,
    new_version: str,
    fetch_versions: Callable[[str], Iterable[str]] | None = None,
    train_lock: dict | None = None,
    lock_new_side: bool = False,
) -> dict:
    """Build a deterministic plan; ``fetch_versions`` is injectable for tests.

    ``train_lock`` pins the old side to the versions recorded when that train
    was processed. ``lock_new_side`` also pins the new side and is only meant
    for replaying already-processed trains (the replay harness).
    """

    validate_manifest(manifest)
    _validate_version(old_version, "old_version")
    _validate_version(new_version, "new_version")
    if _release_train_key(old_version) >= _release_train_key(new_version):
        raise ValueError(
            "old_version must be from a numerically earlier release train than "
            "new_version"
        )

    fetch = fetch_versions or fetch_nuget_versions
    selected_events = _events_for_release(manifest, new_version)
    prior_externalizations = _prior_externalizations(manifest, new_version)
    planned_surfaces: list[dict] = []

    for surface in manifest["surfaces"]:
        package = surface["package"]
        available: list[str] | None = None
        try:
            available = list(fetch(package))
            old_resolution = resolve_train_version(available, old_version)
            new_resolution = resolve_train_version(available, new_version)
        except Exception as exc:
            old_resolution = _network_failure_resolution(package, exc)
            new_resolution = old_resolution
        old_resolution = _locked_resolution(
            train_lock, old_version, package, available, old_resolution
        )
        if lock_new_side:
            new_resolution = _locked_resolution(
                train_lock, new_version, package, available, new_resolution
            )

        externalization = _informational_externalization(
            selected_events, surface["slug"]
        )
        if externalization is not None and old_resolution.state != "resolved":
            old_resolution = _pinned_source_resolution(
                externalization, package, old_resolution, available
            )
        prior_externalization = prior_externalizations.get(surface["slug"])
        if old_resolution.state == "resolved" and new_resolution.state == "resolved":
            status = "diffable"
            reason = (
                f"Resolved {package} from {old_resolution.value} to "
                f"{new_resolution.value}."
            )
        elif (
            old_resolution.state == "missing"
            and new_resolution.state == "resolved"
            and _introduced_after(available, old_version)
        ):
            # The package first shipped after the old train (no version at or
            # before it exists on NuGet), so there is nothing to diff and nothing
            # an existing consumer could have depended on. A package that merely
            # skipped a train still has older versions and stays unverifiable.
            status = "informational"
            reason = (
                f"{package} first shipped after MAF {old_version} (new in "
                f"{new_resolution.value}); no previous train to diff."
            )
        elif (
            old_resolution.state == "missing"
            and new_resolution.state == "missing"
            and _introduced_after(available, new_version)
        ):
            # Not published yet at either train: every NuGet version belongs to a
            # later train, so the surface did not exist for this release.
            status = "informational"
            reason = f"{package} was not yet published as of MAF {new_version}."
        elif prior_externalization is not None:
            # The surface left the MAF train in an earlier release. Missing,
            # ambiguous, or unreachable train-aligned evidence is expected and
            # must never block a later train; it is visible, not gating.
            status = "informational"
            unresolved = "; ".join(
                dict.fromkeys(
                    resolution.reason
                    for resolution in (old_resolution, new_resolution)
                    if resolution.state != "resolved"
                )
            )
            reason = (
                f"Externalized since MAF {prior_externalization['release_version']} "
                f"to {prior_externalization['target_repository']} "
                f"(lifecycle event {prior_externalization['id']}); not part of the "
                f"MAF {new_version} release train, so train-aligned evidence is not "
                f"required ({unresolved})."
            )
        elif (
            externalization is not None
            and old_resolution.state == "resolved"
            and new_resolution.state == "missing"
        ):
            status = "informational"
            reason = externalization["reason"]
        else:
            status = "unverifiable"
            unresolved_reasons = [
                resolution.reason
                for resolution in (old_resolution, new_resolution)
                if resolution.state != "resolved"
            ]
            reason = "; ".join(dict.fromkeys(unresolved_reasons))

        slug = surface["slug"]
        planned_surfaces.append(
            {
                "order": surface["order"],
                "slug": slug,
                "package": package,
                "id_scope": surface["id_scope"],
                "status": status,
                "old_package_version": old_resolution.value,
                "new_package_version": new_resolution.value,
                "diff_artifact": f"diff-{slug}.txt",
                "stderr_artifact": f"diff-{slug}.stderr.txt",
                "reason": reason,
            }
        )

    return {
        "schema_version": SCHEMA_VERSION,
        "release": {
            "old_version": old_version,
            "new_version": new_version,
        },
        "surfaces": planned_surfaces,
        "lifecycle_events": selected_events,
    }


def render_plan(plan: dict) -> str:
    """Return stable, human-readable JSON with a terminating newline."""

    return json.dumps(plan, indent=2, ensure_ascii=False) + "\n"


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument(
        "--train-lock",
        type=Path,
        default=DEFAULT_TRAIN_LOCK,
        help="recorded per-train package versions; pins the old side when present",
    )
    parser.add_argument("--old-version", required=True)
    parser.add_argument("--new-version", required=True)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print the complete plan without creating or changing the output file",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        manifest = load_manifest(args.manifest)
        train_lock = load_train_lock(args.train_lock)
        plan = build_plan(
            manifest, args.old_version, args.new_version, train_lock=train_lock
        )
    except (ManifestValidationError, ValueError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    rendered = render_plan(plan)
    if args.dry_run:
        sys.stdout.write(rendered)
        return 0

    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    except OSError as exc:
        print(f"error: could not write {args.output}: {exc}", file=sys.stderr)
        return 1

    print(f"Wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
