"""Trust-boundary contract and behavior tests for the AI-fill verifier."""
import os
import shlex
import shutil
import subprocess
from pathlib import Path

import yaml


WORKFLOW = (
    Path(__file__).resolve().parents[2] / "workflows" / "maf-ai-fill-verify.yml"
)


def _bash() -> str:
    """bash for running workflow steps; on Windows, skip System32's WSL launcher (Git Bash works)."""
    for directory in os.environ.get("PATH", "").split(os.pathsep):
        candidate = shutil.which("bash", path=directory)
        if candidate and "system32" not in candidate.lower():
            return candidate
    return "bash"


def _steps() -> list[dict]:
    document = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
    return document["jobs"]["verify"]["steps"]


def _resolver_command() -> list[str]:
    resolver = next(step for step in _steps() if step.get("id") == "trusted_base")
    assignment = next(
        line.strip()
        for line in resolver["run"].splitlines()
        if line.strip().startswith("BASE_SHA=$(")
    )
    assert assignment.endswith(")")
    return shlex.split(assignment[len("BASE_SHA=$("):-1])


def _git(cwd: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", *args],
        cwd=cwd,
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def test_pr_data_and_trusted_verifier_use_separate_checkouts():
    checkouts = [
        step
        for step in _steps()
        if str(step.get("uses", "")).startswith("actions/checkout@")
    ]
    assert len(checkouts) == 2
    assert checkouts[0]["with"]["ref"] == "${{ github.event.pull_request.head.sha }}"
    assert checkouts[0]["with"]["path"] == "pr"
    assert checkouts[1]["with"]["ref"] == "${{ github.base_ref }}"
    assert checkouts[1]["with"]["path"] == "trusted"
    assert all(step["with"]["persist-credentials"] is False for step in checkouts)


def test_current_trusted_base_sha_drives_scope_and_delta_verification():
    steps = _steps()
    resolver = next(step for step in steps if step.get("id") == "trusted_base")
    scope = next(step for step in steps if step.get("id") == "scope")
    delta = next(step for step in steps if step.get("id") == "delta")
    resolved_sha = "${{ steps.trusted_base.outputs.sha }}"

    assert resolver.get("continue-on-error") is not True
    assert resolver["shell"] == "bash"
    assert resolver["env"]["BASE_REF"] == "${{ github.base_ref }}"
    assert "set -euo pipefail" in resolver["run"]
    assert '[ -z "$BASE_REF" ]' in resolver["run"]
    assert 'git -C trusted rev-parse --verify "HEAD^{commit}"' in resolver["run"]
    assert "^([0-9a-f]{40}|[0-9a-f]{64})$" in resolver["run"]
    assert 'git -C trusted cat-file -e "${BASE_SHA}^{commit}"' in resolver["run"]
    assert 'echo "sha=$BASE_SHA" >> "$GITHUB_OUTPUT"' in resolver["run"]

    assert scope["env"]["BASE_SHA"] == resolved_sha
    assert delta["env"]["BASE_SHA"] == resolved_sha
    assert 'git -C pr fetch --no-tags origin "$BASE_SHA"' in scope["run"]
    assert 'git -C pr diff --name-only "${BASE_SHA}...${HEAD_SHA}"' in scope["run"]
    assert '--base-sha "$BASE_SHA"' in delta["run"]


def test_stale_event_base_sha_cannot_select_or_verify_trusted_code():
    workflow = WORKFLOW.read_text(encoding="utf-8")

    assert "github.event.pull_request.base.sha" not in workflow
    # Scope decision, obligations verifier, the Z-02 autonomy-envelope check,
    # and the Q-02 signature and compiler oracles (merge-base of the PR for the
    # old train).
    assert workflow.count("${{ steps.trusted_base.outputs.sha }}") == 5


def test_resolver_command_uses_checked_out_tip_not_an_older_sha(tmp_path):
    trusted = tmp_path / "trusted"
    _git(tmp_path, "init", str(trusted))
    _git(tmp_path, "-C", "trusted", "config", "user.name", "MAF CI Test")
    _git(tmp_path, "-C", "trusted", "config", "user.email", "ci@example.invalid")

    marker = trusted / "marker.txt"
    marker.write_text("stale\n", encoding="utf-8")
    _git(tmp_path, "-C", "trusted", "add", "marker.txt")
    _git(tmp_path, "-C", "trusted", "commit", "-m", "stale base")
    stale_event_sha = _git(tmp_path, "-C", "trusted", "rev-parse", "HEAD")

    marker.write_text("current\n", encoding="utf-8")
    _git(tmp_path, "-C", "trusted", "commit", "-am", "current base")
    current_tip = _git(tmp_path, "-C", "trusted", "rev-parse", "HEAD")

    resolved = subprocess.run(
        _resolver_command(),
        cwd=tmp_path,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    assert stale_event_sha != current_tip
    assert resolved == current_tip


def test_resolver_command_fails_when_trusted_checkout_is_missing(tmp_path):
    result = subprocess.run(
        _resolver_command(),
        cwd=tmp_path,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode != 0


def test_scope_discovery_and_verifiers_fail_closed_from_trusted_base():
    steps = _steps()
    scope = next(step for step in steps if step.get("id") == "scope")["run"]
    assert "git -C pr diff --name-only" in scope
    assert "|| true" not in scope

    registry = next(step for step in steps if step.get("id") == "verify")
    assert "trusted/src/maf-autopilot" in registry["run"]
    assert "/pr/.github/skills/maf-obsolete-api-registry/registry.yaml" in registry["env"]["MAF_REGISTRY_PATH"]

    crossfile = next(step for step in steps if step.get("id") == "crossfile")["run"]
    assert "../trusted/.github/scripts/verify_crossfile_consistency.py" in crossfile

    delta = next(step for step in steps if step.get("id") == "delta")["run"]
    assert "trusted/.github/scripts/verify_ai_fill_delta.py" in delta
    assert "--repo pr" in delta
    assert "--head-root pr" in delta
    assert '--base-sha "$BASE_SHA"' in delta
    assert '--head-sha "$HEAD_SHA"' in delta


def test_first_verifier_bootstrap_cannot_approve_a_release_scaffold():
    delta = next(step for step in _steps() if step.get("id") == "delta")["run"]
    assert 'git -C pr ls-tree "$BASE_SHA" -- .github/scripts/verify_ai_fill_delta.py' in delta
    assert '[ "$TRUSTED_TREE_EXIT" -ne 0 ]' in delta
    assert '[ "$TRUSTED_MODE" != "100644" ]' in delta
    assert '[ -L "$TRUSTED_DELTA" ]' in delta
    assert delta.index('[ "$TRUSTED_MODE" != "100644" ]') < delta.index('python3 "$TRUSTED_DELTA"')
    assert 'git -C pr show "${BASE_SHA}:.maf-version"' in delta
    assert 'git -C pr show "${HEAD_SHA}:.maf-version"' in delta
    assert 'git -C pr cat-file -e "${HEAD_SHA}:.github/scripts/verify_ai_fill_delta.py"' in delta
    assert 'git -C pr diff --name-status "$BASE_SHA" "$HEAD_SHA"' in delta
    assert "$'A\\t.github/scripts/verify_ai_fill_delta.py'" in delta
    assert 'git -C pr ls-tree "$HEAD_SHA" -- .github/scripts/verify_ai_fill_delta.py' in delta
    assert '[ "$VERIFIER_TREE_EXIT" -ne 0 ]' in delta
    assert '[ "$VERIFIER_MODE" != "100644" ]' in delta
    assert "git -C pr ls-tree -r --name-only" in delta
    assert '.github/maf-scaffold-obligations/' in delta
    assert '[ "$BASE_VERSION" != "$HEAD_VERSION" ]' in delta
    assert '[ -n "$BASE_CONTRACT_FILES" ] || [ -n "$HEAD_CONTRACT_FILES" ]' in delta
    assert '[ "$BASE_CONTRACT_LIST_EXIT" -ne 0 ]' in delta
    assert '[ "$HEAD_CONTRACT_LIST_EXIT" -ne 0 ]' in delta
    assert '[ "$HEAD_VERIFIER_EXIT" -ne 0 ]' in delta
    assert "A release scaffold may not use the bootstrap path." in delta
    assert "EXIT=1" in delta


def test_obligations_are_branch_name_independent_and_recovered_from_history():
    steps = _steps()
    delta = next(step for step in steps if step.get("id") == "delta")
    assert "startsWith(github.base_ref" not in str(delta.get("if", ""))
    assert delta["if"] == "steps.scope.outputs.in_scope == 'true'"
    assert "--base-ref" in delta["run"]
    assert "--head-ref" in delta["run"]
    workflow = WORKFLOW.read_text(encoding="utf-8")
    assert ".github/maf-scaffold-obligations/maf-" in workflow


def _envelope_repo(tmp_path: Path, scaffold_files: dict[str, str]) -> tuple[Path, str, str]:
    """A repo where main advanced (non-allowlisted files) after the scaffold branch was cut."""
    repo = tmp_path / "pr"
    _git(tmp_path, "init", "-b", "main", str(repo))
    _git(repo, "config", "user.name", "MAF CI Test")
    _git(repo, "config", "user.email", "ci@example.invalid")
    (repo / ".maf-version").write_text("1.21.0\n", encoding="utf-8")
    _git(repo, "add", ".")
    _git(repo, "commit", "-m", "base")
    _git(repo, "checkout", "-b", "release-watcher/maf-1.22.0")
    for rel, content in scaffold_files.items():
        path = repo / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    _git(repo, "add", ".")
    _git(repo, "commit", "-m", "scaffold")
    head = _git(repo, "rev-parse", "HEAD")
    _git(repo, "checkout", "main")
    workflow_dir = repo / ".github" / "workflows"
    workflow_dir.mkdir(parents=True)
    (workflow_dir / "unrelated.yml").write_text("name: x\n", encoding="utf-8")
    (repo / "CHANGELOG.md").write_text("main moved on\n", encoding="utf-8")
    _git(repo, "add", ".")
    _git(repo, "commit", "-m", "main advances after the branch was cut")
    base = _git(repo, "rev-parse", "HEAD")
    return repo, base, head


def _run_envelope(tmp_path: Path, base: str, head: str) -> subprocess.CompletedProcess:
    step = next(step for step in _steps() if step.get("id") == "envelope")
    import os
    env = {**os.environ, "BASE_SHA": base, "HEAD_SHA": head, "HEAD_REF": "release-watcher/maf-1.22.0"}
    return subprocess.run([_bash(), "-c", step["run"]], cwd=tmp_path, env=env, capture_output=True, text=True)


def test_envelope_ignores_changes_main_gained_after_the_branch_was_cut(tmp_path):
    # Regression: PR #196 failed because a two-dot diff against the current
    # base tip listed main's newer workflow/script changes as PR changes.
    _, base, head = _envelope_repo(tmp_path, {
        ".maf-version": "1.22.0\n",
        ".github/skills/maf-obsolete-api-registry/registry.yaml": "entries: []\n",
        "guides/maf-current-migration-guide.md": "# guide\n",
        "guides/maf-1.22.0-migration-guide.md": "# guide\n",
        ".github/maf-scaffold-obligations/maf-1.22.0.json": "{}\n",
    })
    result = _run_envelope(tmp_path, base, head)
    assert result.returncode == 0, result.stdout + result.stderr
    assert "Autonomy envelope OK" in result.stdout


def test_envelope_rejects_a_scaffold_branch_that_edits_a_workflow(tmp_path):
    _, base, head = _envelope_repo(tmp_path, {
        ".maf-version": "1.22.0\n",
        ".github/workflows/maf-ai-fill-verify.yml": "name: weakened\n",
    })
    result = _run_envelope(tmp_path, base, head)
    assert result.returncode == 1
    assert ".github/workflows/maf-ai-fill-verify.yml" in result.stdout
