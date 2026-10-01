"""Adversarial tests for immutable watcher-scaffold obligations."""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest
import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from verify_ai_fill_delta import (  # noqa: E402
    build_obligations,
    recover_canonical_obligations,
    serialize_obligations,
    verify_obligations,
)


def _write(root: Path, relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _registry() -> dict:
    return {
        "schema_version": 1,
        "target_maf_version": "1.14.0",
        "last_updated": "2026-08-17",
        "entries": [
            {
                "id": "MAF113-HISTORY-001",
                "package": "Microsoft.Agents.AI",
                "version_introduced": "1.13.0",
                "fix_description": "historical",
            },
            {
                "id": "MAF114-CORE-001",
                "package": "Microsoft.Agents.AI",
                "version_introduced": "1.14.0",
                "fix_description": "TODO",
            },
            {
                "id": "MAF114-REVIEW-001",
                "package": "Microsoft.Agents.AI.Harness",
                "version_introduced": "1.14.0",
                "fix_description": "TODO",
            },
        ],
    }


TARGET_GUIDE = """# MAF 1.14.0 Migration Guide

<!-- AUTO-GENERATED START — anything between AUTO-GENERATED START and AUTO-GENERATED END is overwritten on re-run -->

## Versions

- Migrating from: `1.13.0`
- Migrating to: `1.14.0`

## Package API Evidence

### Microsoft.Agents.AI

```text
- [Removed] Agent.RunAsync()
```

## Release Notes Extract

Official evidence.

## Breaking Changes (requires human verification)

TODO fill this review section.

<!-- AUTO-GENERATED END -->

## Human additions

Preserved suffix.
"""


TOOL = '''internal static class CompatibilityTool
{
    internal static readonly object Matrix = new()
        {
            ["1.14.0"] = """
                ## MAF 1.14.0 Compatibility
                target TODO
                """,

            ["1.13.0"] = """
                ## MAF 1.13.0 Compatibility
                historical
                """,
        };
}
'''


def _scaffold(root: Path) -> tuple[dict, bytes]:
    _write(root, ".maf-version", "1.14.0\n")
    _write(
        root,
        ".github/skills/maf-obsolete-api-registry/registry.yaml",
        yaml.safe_dump(_registry(), sort_keys=False),
    )
    _write(root, "guides/maf-1.13.0-migration-guide.md", "historical guide\n")
    _write(root, "guides/maf-1.14.0-migration-guide.md", TARGET_GUIDE)
    _write(
        root,
        "docs/compatibility-matrix.md",
        "matrix prefix\n| **1.14.0** | target TODO |\n| **1.13.0** | historical |\ntracking 1.14.0\n",
    )
    _write(root, "src/maf-autopilot/Tools/CompatibilityTool.cs", TOOL)
    doc = build_obligations(
        root,
        old_version="1.13.0",
        target_version="1.14.0",
        base_commit="a" * 40,
        target_branch="release-watcher/maf-1.14.0",
    )
    raw = serialize_obligations(doc)
    path = root / ".github/maf-scaffold-obligations/maf-1.14.0.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(raw)
    return doc, raw


def _fill_registry(root: Path) -> None:
    path = root / ".github/skills/maf-obsolete-api-registry/registry.yaml"
    doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    doc["entries"] = [
        entry for entry in doc["entries"] if entry["id"] != "MAF114-REVIEW-001"
    ]
    doc["entries"].append(
        {
            "id": "MAF114-HARNESS-001",
            "package": "Microsoft.Agents.AI.Harness",
            "version_introduced": "1.14.0",
            "fix_description": "Concrete replacement",
        }
    )
    path.write_text(yaml.safe_dump(doc, sort_keys=False), encoding="utf-8")


def _filled(root: Path) -> tuple[dict, bytes]:
    doc, raw = _scaffold(root)
    _fill_registry(root)
    return doc, raw


def test_allows_only_target_fill_regions_and_review_replacement(tmp_path: Path):
    doc, raw = _filled(tmp_path)
    guide = tmp_path / "guides/maf-1.14.0-migration-guide.md"
    guide.write_text(guide.read_text().replace("TODO fill this review section.", "Concrete migration."))
    matrix = tmp_path / "docs/compatibility-matrix.md"
    matrix.write_text(matrix.read_text().replace("target TODO", "target filled"))
    tool = tmp_path / "src/maf-autopilot/Tools/CompatibilityTool.cs"
    tool.write_text(tool.read_text().replace("target TODO", "target filled"))
    assert verify_obligations(doc, tmp_path, raw) == []


def test_rejects_deleted_or_reversioned_generated_id(tmp_path: Path):
    doc, raw = _filled(tmp_path)
    path = tmp_path / ".github/skills/maf-obsolete-api-registry/registry.yaml"
    registry = yaml.safe_load(path.read_text())
    next(e for e in registry["entries"] if e["id"] == "MAF114-CORE-001")["version_introduced"] = "1.15.0"
    path.write_text(yaml.safe_dump(registry, sort_keys=False))
    assert any("required target registry id" in e for e in verify_obligations(doc, tmp_path, raw))


def test_rejects_review_retention_or_wrong_package_replacement(tmp_path: Path):
    doc, raw = _scaffold(tmp_path)
    errors = verify_obligations(doc, tmp_path, raw)
    assert any("must be replaced" in error for error in errors)
    assert any("same-package" in error for error in errors)


def test_additive_scaffold_can_record_zero_generated_registry_entries(tmp_path: Path):
    _scaffold(tmp_path)
    registry_path = tmp_path / ".github/skills/maf-obsolete-api-registry/registry.yaml"
    registry = yaml.safe_load(registry_path.read_text())
    registry["entries"] = [
        entry for entry in registry["entries"] if entry["version_introduced"] != "1.14.0"
    ]
    registry_path.write_text(yaml.safe_dump(registry, sort_keys=False))
    doc = build_obligations(
        tmp_path,
        old_version="1.13.0",
        target_version="1.14.0",
        base_commit="a" * 40,
        target_branch="release-watcher/maf-1.14.0",
    )
    raw = serialize_obligations(doc)
    contract = tmp_path / ".github/maf-scaffold-obligations/maf-1.14.0.json"
    contract.write_bytes(raw)
    assert doc["registry"]["generated_target_entries"] == []
    assert verify_obligations(doc, tmp_path, raw) == []


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("target_maf_version", "1.13.0", "target_maf_version must match"),
        ("last_updated", "2026-02-31", "valid ISO calendar date"),
        ("last_updated", "2026-8-17", "valid ISO calendar date"),
    ],
)
def test_scaffold_rejects_incorrect_registry_metadata(
    tmp_path: Path,
    field: str,
    value: str,
    message: str,
):
    _scaffold(tmp_path)
    registry_path = tmp_path / ".github/skills/maf-obsolete-api-registry/registry.yaml"
    registry = yaml.safe_load(registry_path.read_text(encoding="utf-8"))
    registry[field] = value
    registry_path.write_text(yaml.safe_dump(registry, sort_keys=False), encoding="utf-8")

    with pytest.raises(ValueError, match=message):
        build_obligations(
            tmp_path,
            old_version="1.13.0",
            target_version="1.14.0",
            base_commit="a" * 40,
            target_branch="release-watcher/maf-1.14.0",
        )


def test_scaffold_ignores_guide_headings_inside_fenced_package_evidence(tmp_path: Path):
    fenced = """<<<BEGIN_USER_DATA_0123456789abcdef0123456789abcdef_UPSTREAM-MAF-DIFF-CORE>>>
## Package API Evidence
## Release Notes Extract
## Breaking Changes
<<<END_USER_DATA_0123456789abcdef0123456789abcdef_UPSTREAM-MAF-DIFF-CORE>>>"""
    _write(
        tmp_path,
        "guides/maf-1.14.0-migration-guide.md",
        TARGET_GUIDE.replace("```text\n- [Removed] Agent.RunAsync()\n```", fenced),
    )
    _write(tmp_path, ".maf-version", "1.14.0\n")
    _write(
        tmp_path,
        ".github/skills/maf-obsolete-api-registry/registry.yaml",
        yaml.safe_dump(_registry(), sort_keys=False),
    )
    _write(tmp_path, "guides/maf-1.13.0-migration-guide.md", "historical guide\n")
    _write(
        tmp_path,
        "docs/compatibility-matrix.md",
        "matrix prefix\n| **1.14.0** | target TODO |\n| **1.13.0** | historical |\ntracking 1.14.0\n",
    )
    _write(tmp_path, "src/maf-autopilot/Tools/CompatibilityTool.cs", TOOL)

    document = build_obligations(
        tmp_path,
        old_version="1.13.0",
        target_version="1.14.0",
        base_commit="a" * 40,
        target_branch="release-watcher/maf-1.14.0",
    )
    assert document["target_guide"]["evidence_sha256"]


def test_scaffold_rejects_unterminated_upstream_data_fence(tmp_path: Path):
    _write(
        tmp_path,
        "guides/maf-1.14.0-migration-guide.md",
        TARGET_GUIDE.replace(
            "```text\n- [Removed] Agent.RunAsync()\n```",
            "<<<BEGIN_USER_DATA_0123456789abcdef0123456789abcdef_UPSTREAM-MAF-DIFF-CORE>>>\n## Breaking Changes",
        ),
    )
    _write(tmp_path, ".maf-version", "1.14.0\n")
    _write(
        tmp_path,
        ".github/skills/maf-obsolete-api-registry/registry.yaml",
        yaml.safe_dump(_registry(), sort_keys=False),
    )
    _write(tmp_path, "guides/maf-1.13.0-migration-guide.md", "historical guide\n")
    _write(
        tmp_path,
        "docs/compatibility-matrix.md",
        "matrix prefix\n| **1.14.0** | target TODO |\n| **1.13.0** | historical |\ntracking 1.14.0\n",
    )
    _write(tmp_path, "src/maf-autopilot/Tools/CompatibilityTool.cs", TOOL)

    with pytest.raises(ValueError, match="unterminated upstream-data fence"):
        build_obligations(
            tmp_path,
            old_version="1.13.0",
            target_version="1.14.0",
            base_commit="a" * 40,
            target_branch="release-watcher/maf-1.14.0",
        )


@pytest.mark.parametrize(
    ("relative", "old", "new", "message"),
    [
        (
            ".github/skills/maf-obsolete-api-registry/registry.yaml",
            "fix_description: historical",
            "fix_description: rewritten",
            "historical registry",
        ),
        ("guides/maf-1.13.0-migration-guide.md", "historical", "rewritten", "historical guide"),
        ("guides/maf-1.14.0-migration-guide.md", "Official evidence.", "erased evidence.", "target-guide"),
        ("docs/compatibility-matrix.md", "historical |", "rewritten |", "matrix suffix"),
        ("src/maf-autopilot/Tools/CompatibilityTool.cs", "historical", "rewritten", "CompatibilityTool suffix"),
        (".maf-version", "1.14.0", "1.13.0", ".maf-version"),
    ],
)
def test_rejects_protected_history_evidence_and_version(
    tmp_path: Path, relative: str, old: str, new: str, message: str
):
    doc, raw = _filled(tmp_path)
    path = tmp_path / relative
    path.write_text(path.read_text().replace(old, new))
    assert any(message in error for error in verify_obligations(doc, tmp_path, raw))


def test_rejects_modified_obligations_copy(tmp_path: Path):
    doc, raw = _filled(tmp_path)
    path = tmp_path / ".github/maf-scaffold-obligations/maf-1.14.0.json"
    path.write_text(path.read_text().replace("maf-release-watcher", "attacker"))
    assert any("differs" in error for error in verify_obligations(doc, tmp_path, raw))


def _git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args], capture_output=True, text=True, check=True
    )
    return result.stdout.strip()


def _commit(repo: Path, message: str) -> str:
    _git(repo, "add", "-A")
    _git(repo, "commit", "-m", message)
    return _git(repo, "rev-parse", "HEAD")


def _history_repo(tmp_path: Path, base_version: str = "1.13.0") -> tuple[Path, str]:
    repo = tmp_path / "repo"
    repo.mkdir()
    _git(repo, "init")
    _git(repo, "config", "user.email", "tests@example.invalid")
    _git(repo, "config", "user.name", "Tests")
    _write(repo, ".maf-version", f"{base_version}\n")
    return repo, _commit(repo, "base")


def _add_contract(repo: Path, base: str, target: str, branch: str) -> tuple[str, bytes, Path]:
    _write(repo, ".maf-version", f"{target}\n")
    document = {
        "schema_version": 1,
        "scaffold": {
            "source": "maf-release-watcher",
            "base_commit": base,
            "target_branch": branch,
            "old_version": "1.13.0",
            "target_version": target,
        },
    }
    raw = serialize_obligations(document)
    path = repo / f".github/maf-scaffold-obligations/maf-{target}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(raw)
    return _commit(repo, "scaffold"), raw, path


def test_history_recovers_first_main_bound_scaffold_contract(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    head, raw, _ = _add_contract(repo, base, "1.14.0", "dry-run/custom")
    assert recover_canonical_obligations(
        repo, base_sha=base, head_sha=head, base_ref="main", head_ref="dry-run/custom"
    ) == raw


def test_history_rejects_modified_contract(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _, _, path = _add_contract(repo, base, "1.14.0", "release-watcher/maf-1.14.0")
    path.write_text(path.read_text().replace("maf-release-watcher", "attacker"))
    head = _commit(repo, "tamper")
    with pytest.raises(ValueError, match="changed at commit"):
        recover_canonical_obligations(
            repo,
            base_sha=base,
            head_sha=head,
            base_ref="main",
            head_ref="release-watcher/maf-1.14.0",
        )


def test_history_rejects_deleted_then_readded_contract(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _, raw, path = _add_contract(repo, base, "1.14.0", "release-watcher/maf-1.14.0")
    path.unlink()
    _commit(repo, "delete contract")
    path.write_bytes(raw)
    head = _commit(repo, "readd contract")
    with pytest.raises(ValueError, match="missing at commit"):
        recover_canonical_obligations(
            repo,
            base_sha=base,
            head_sha=head,
            base_ref="main",
            head_ref="release-watcher/maf-1.14.0",
        )


def test_version_reversion_cannot_erase_contract_gate(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _add_contract(repo, base, "1.14.0", "release-watcher/maf-1.14.0")
    _write(repo, ".maf-version", "1.13.0\n")
    head = _commit(repo, "revert version")
    with pytest.raises(ValueError, match="HEAD .maf-version"):
        recover_canonical_obligations(
            repo,
            base_sha=base,
            head_sha=head,
            base_ref="main",
            head_ref="release-watcher/maf-1.14.0",
        )


def test_version_bump_without_contract_fails_closed(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _write(repo, ".maf-version", "1.14.0\n")
    head = _commit(repo, "uncontracted bump")
    with pytest.raises(ValueError, match="no obligations"):
        recover_canonical_obligations(
            repo, base_sha=base, head_sha=head, base_ref="main", head_ref="feature"
        )


def test_ordinary_infrastructure_pr_without_contract_is_not_applicable(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _write(repo, "README.md", "ordinary change\n")
    head = _commit(repo, "ordinary")
    assert recover_canonical_obligations(
        repo, base_sha=base, head_sha=head, base_ref="main", head_ref="feature"
    ) is None


def _stale_branch(tmp_path: Path, branch_change: tuple[str, str]) -> tuple[Path, str, str]:
    """A PR branch cut at 1.13.0, after which main merged the 1.14.0 release."""
    repo, _ = _history_repo(tmp_path)
    _git(repo, "checkout", "-b", "feature")
    _write(repo, *branch_change)
    head = _commit(repo, "pr change")
    _git(repo, "checkout", "-")
    _write(repo, ".maf-version", "1.14.0\n")
    main_tip = _commit(repo, "release 1.14.0 merged")
    _git(repo, "checkout", "feature")
    return repo, main_tip, head


def test_stale_pr_cut_before_a_release_merged_is_not_a_version_bump(tmp_path: Path):
    # Regression (#201): main moved 1.21 -> 1.22 after a Dependabot PR was cut.
    # The PR never touched .maf-version, so comparing it with the current main
    # tip reported a "watcher-managed version bump" and blocked the merge.
    repo, main_tip, head = _stale_branch(tmp_path, ("Directory.Packages.props", "<Project />\n"))
    assert recover_canonical_obligations(
        repo, base_sha=main_tip, head_sha=head, base_ref="main", head_ref="feature"
    ) is None


def test_stale_pr_that_bumps_the_version_itself_still_fails_closed(tmp_path: Path):
    repo, main_tip, head = _stale_branch(tmp_path, (".maf-version", "1.15.0\n"))
    with pytest.raises(ValueError, match="no obligations"):
        recover_canonical_obligations(
            repo, base_sha=main_tip, head_sha=head, base_ref="main", head_ref="feature"
        )


def test_prior_release_contract_on_main_does_not_block_next_release(tmp_path: Path):
    repo, original = _history_repo(tmp_path)
    _, old_raw, old_path = _add_contract(repo, original, "1.14.0", "release-watcher/maf-1.14.0")
    # Model the filled 1.14 contract having merged to main, then create 1.15.
    base = _git(repo, "rev-parse", "HEAD")
    old_path.write_bytes(old_raw)
    head, new_raw, _ = _add_contract(repo, base, "1.15.0", "release-watcher/maf-1.15.0")
    assert recover_canonical_obligations(
        repo,
        base_sha=base,
        head_sha=head,
        base_ref="main",
        head_ref="release-watcher/maf-1.15.0",
    ) == new_raw


def _add_contract_with_lock(repo: Path, base: str, target: str, branch: str) -> tuple[str, bytes]:
    _write(repo, ".github/maf-train-lock.json", '{"schema_version": 1, "trains": {"%s": {"A": "%s"}}}\n' % (target, target))
    head, raw, _ = _add_contract(repo, base, target, branch)
    return head, raw


def test_history_accepts_fill_that_leaves_train_lock_untouched(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _, raw = _add_contract_with_lock(repo, base, "1.14.0", "release-watcher/maf-1.14.0")
    _write(repo, "guides/maf-1.14.0-migration-guide.md", "filled\n")
    head = _commit(repo, "fill")
    assert recover_canonical_obligations(
        repo, base_sha=base, head_sha=head, base_ref="main", head_ref="release-watcher/maf-1.14.0"
    ) == raw


def test_history_rejects_fill_that_changes_train_lock(tmp_path: Path):
    repo, base = _history_repo(tmp_path)
    _add_contract_with_lock(repo, base, "1.14.0", "release-watcher/maf-1.14.0")
    _write(repo, ".github/maf-train-lock.json", '{"schema_version": 1, "trains": {"1.14.0": {"A": "9.9.9"}}}\n')
    head = _commit(repo, "tamper lock")
    with pytest.raises(ValueError, match="train lock changed"):
        recover_canonical_obligations(
            repo, base_sha=base, head_sha=head, base_ref="main", head_ref="release-watcher/maf-1.14.0"
        )


def _replace_review_with(root: Path, package: str) -> None:
    path = root / ".github/skills/maf-obsolete-api-registry/registry.yaml"
    doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    doc["entries"] = [entry for entry in doc["entries"] if entry["id"] != "MAF114-REVIEW-001"]
    doc["entries"].append(
        {
            "id": "MAF114-EXTRA-001",
            "package": package,
            "version_introduced": "1.14.0",
            "fix_description": "Concrete replacement",
        }
    )
    path.write_text(yaml.safe_dump(doc, sort_keys=False), encoding="utf-8")


def test_review_sentinel_can_be_resolved_in_an_untracked_maf_package(tmp_path: Path):
    # MAF 1.19: the break lived in Microsoft.Agents.AI.Mcp, which the watcher
    # did not diff, so the sentinel's package was only a default guess.
    doc, raw = _scaffold(tmp_path)
    _replace_review_with(tmp_path, "Microsoft.Agents.AI.SomeUntrackedSurface")
    errors = verify_obligations(doc, tmp_path, raw)
    assert not any("REVIEW sentinel" in error for error in errors)


def test_review_sentinel_cannot_be_resolved_in_another_tracked_package(tmp_path: Path):
    doc, raw = _scaffold(tmp_path)
    _replace_review_with(tmp_path, "Microsoft.Agents.AI.Workflows")
    errors = verify_obligations(doc, tmp_path, raw)
    assert any("same-package" in error for error in errors)


def test_review_sentinel_cannot_be_resolved_outside_maf(tmp_path: Path):
    doc, raw = _scaffold(tmp_path)
    _replace_review_with(tmp_path, "Contoso.Unrelated")
    errors = verify_obligations(doc, tmp_path, raw)
    assert any("same-package" in error for error in errors)


def test_tracked_set_is_the_one_in_force_for_the_train(tmp_path: Path):
    # An Mcp entry may resolve a sentinel when Mcp was untracked for the train,
    # even after the package becomes tracked on main (MAF 1.19 -> 1.20).
    doc, raw = _scaffold(tmp_path)
    _replace_review_with(tmp_path, "Microsoft.Agents.AI.Mcp")
    before = verify_obligations(doc, tmp_path, raw, tracked_packages={"microsoft.agents.ai.harness"})
    after = verify_obligations(
        doc, tmp_path, raw, tracked_packages={"microsoft.agents.ai.harness", "microsoft.agents.ai.mcp"}
    )
    assert not any("REVIEW sentinel" in error for error in before)
    assert any("same-package" in error for error in after)


def test_tracked_packages_at_reads_manifest_from_base_commit(tmp_path: Path):
    from verify_ai_fill_delta import tracked_packages_at

    repo, base = _history_repo(tmp_path)
    # No manifest at the base commit: fall back to the trusted checked-in copy.
    assert "microsoft.agents.ai.workflows" in tracked_packages_at(repo, base)
    _write(
        repo,
        ".github/maf-package-surfaces.json",
        '{"schema_version": 1, "surfaces": [{"package": "Microsoft.Agents.AI"}]}',
    )
    commit = _commit(repo, "manifest")
    assert tracked_packages_at(repo, commit) == {"microsoft.agents.ai"}
