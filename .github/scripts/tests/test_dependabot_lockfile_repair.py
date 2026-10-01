"""Trust-envelope tests for the Dependabot lockfile repair workflow.

The repair job runs on workflow_run with the push PAT in scope, so its
envelope must only admit Dependabot-authored branches that change version
lines in Directory.Packages.props and packages.lock.json files.
"""
from __future__ import annotations

import os
import subprocess
from pathlib import Path

import yaml

WORKFLOW = Path(__file__).resolve().parents[2] / "workflows" / "dependabot-lockfile-repair.yml"

PROPS = """<Project>
  <ItemGroup>
    <PackageVersion Include="YamlDotNet" Version="18.1.0" />
  </ItemGroup>
</Project>
"""


def _git(cwd: Path, *args: str, env: dict | None = None) -> str:
    return subprocess.run(
        ["git", *args], cwd=cwd, check=True, capture_output=True, text=True,
        env={**os.environ, **(env or {})},
    ).stdout.strip()


def _job() -> dict:
    return yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))["jobs"]["repair"]


def _branch(tmp_path: Path, changes: dict[str, str], author: str = "dependabot[bot]") -> tuple[Path, str]:
    origin = tmp_path / "origin.git"
    _git(tmp_path, "init", "--bare", "-b", "main", str(origin))
    repo = tmp_path / "repo"
    _git(tmp_path, "clone", "-q", str(origin), str(repo))
    _git(repo, "config", "user.name", "Maintainer")
    _git(repo, "config", "user.email", "m@example.invalid")
    _git(repo, "checkout", "-q", "-b", "main")
    (repo / "Directory.Packages.props").write_text(PROPS, encoding="utf-8")
    (repo / "src" / "app").mkdir(parents=True)
    (repo / "src" / "app" / "packages.lock.json").write_text("{}\n", encoding="utf-8")
    (repo / "src" / "app" / "app.csproj").write_text("<Project />\n", encoding="utf-8")
    _git(repo, "add", ".")
    _git(repo, "commit", "-q", "-m", "base")
    _git(repo, "push", "-q", "origin", "main")
    _git(repo, "checkout", "-q", "-b", "dependabot/nuget/yamldotnet")
    for rel, content in changes.items():
        (repo / rel).write_text(content, encoding="utf-8")
    _git(repo, "add", ".")
    ident = {"GIT_AUTHOR_NAME": author, "GIT_AUTHOR_EMAIL": "bot@example.invalid"}
    _git(repo, "commit", "-q", "-m", "bump", env=ident)
    return repo, _git(repo, "rev-parse", "HEAD")


def _envelope(repo: Path, head: str, tmp_path: Path) -> str:
    step = next(s for s in _job()["steps"] if s.get("id") == "envelope")
    out = tmp_path / "out.txt"
    out.write_text("", encoding="utf-8")
    env = {**os.environ, "HEAD_SHA": head, "GITHUB_OUTPUT": str(out)}
    subprocess.run(["bash", "-c", step["run"]], cwd=repo, env=env, check=True, capture_output=True, text=True)
    return out.read_text(encoding="utf-8")


def test_job_only_runs_for_failed_same_repo_dependabot_nuget_branches():
    condition = " ".join(_job()["if"].split())
    assert "github.event.workflow_run.conclusion == 'failure'" in condition
    assert "head_repository.full_name == github.repository" in condition
    assert "startsWith(github.event.workflow_run.head_branch, 'dependabot/nuget/')" in condition
    checkout = _job()["steps"][0]
    assert checkout["with"]["persist-credentials"] is False


def test_dependabot_version_bump_is_admitted(tmp_path):
    repo, head = _branch(tmp_path, {
        "Directory.Packages.props": PROPS.replace("18.1.0", "18.2.0"),
        "src/app/packages.lock.json": '{"version": 2}\n',
    })
    assert "ok=true" in _envelope(repo, head, tmp_path)


def test_project_file_change_is_refused(tmp_path):
    repo, head = _branch(tmp_path, {"src/app/app.csproj": "<Project><Import Project=\"evil.targets\" /></Project>\n"})
    assert "ok=false" in _envelope(repo, head, tmp_path)


def test_props_change_beyond_version_lines_is_refused(tmp_path):
    evil = PROPS.replace("<ItemGroup>", "<Import Project=\"evil.props\" />\n  <ItemGroup>")
    repo, head = _branch(tmp_path, {"Directory.Packages.props": evil})
    assert "ok=false" in _envelope(repo, head, tmp_path)


def test_non_dependabot_commit_is_refused(tmp_path):
    repo, head = _branch(tmp_path, {"Directory.Packages.props": PROPS.replace("18.1.0", "18.2.0")}, author="someone")
    assert "ok=false" in _envelope(repo, head, tmp_path)
