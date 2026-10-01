"""Guard conditions for the bounded fill-repair loop (ROADMAP A-01)."""
from pathlib import Path

import yaml

WORKFLOW = Path(__file__).resolve().parents[2] / "workflows" / "maf-fill-repair.yml"
RUNBOOK = Path(__file__).resolve().parents[3] / "docs" / "runbooks" / "self-update.md"


def _doc() -> dict:
    return yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))


def test_triggers_only_on_the_verify_workflow():
    trigger = _doc()[True]["workflow_run"]  # PyYAML parses the `on:` key as True
    assert trigger["workflows"] == ["MAF AI-Fill PR Verify"]
    assert trigger["types"] == ["completed"]


def test_acts_only_on_a_failed_verify_of_the_agents_own_fill():
    condition = " ".join(_doc()["jobs"]["repair"]["if"].split())
    assert "workflow_run.conclusion == 'failure'" in condition
    assert "head_repository.full_name == github.repository" in condition
    assert "startsWith(github.event.workflow_run.head_branch, 'release-watcher/maf-')" in condition
    # The scaffold commit fails verify by design; only the agent's fill is repaired.
    assert "startsWith(github.event.workflow_run.head_commit.message, 'chore: AI-filled TODOs')" in condition


def test_repairs_are_bounded_and_escalate_to_a_human():
    step = _doc()["jobs"]["repair"]["steps"][0]
    assert step["env"]["MAX_REPAIRS"] == "${{ vars.MAF_FILL_MAX_REPAIRS || '1' }}"
    assert '[ "$REPAIRS" -lt "$MAX_REPAIRS" ]' in step["run"]
    assert "needs-human" in step["run"]
    assert '[ "$CURRENT" != "$HEAD_SHA" ]' in step["run"]  # stale heads are ignored


def test_escalation_links_an_existing_runbook_section():
    assert "## fill-repair-exhausted" in RUNBOOK.read_text(encoding="utf-8")
