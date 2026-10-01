"""Static contract tests for the Copilot semantic-review workflow."""
from pathlib import Path

import yaml


WORKFLOW = (
    Path(__file__).resolve().parents[2]
    / "workflows"
    / "maf-ai-semantic-review.yml"
)


def test_copilot_inference_uses_the_seat_supported_default_model():
    """Do not reintroduce an account-dependent Copilot CLI model pin."""
    document = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
    steps = document["jobs"]["semantic-review"]["steps"]
    inference = next(
        step
        for step in steps
        if str(step.get("uses", "")).startswith("actions/ai-inference@")
    )

    assert inference["with"]["provider"] == "copilot"
    # ai-inference v3 defaults an omitted `model` to gpt-4.1; only an explicit
    # empty string lets the Copilot CLI pick a model this seat can use.
    assert inference["with"]["model"] == ""


def test_privileged_review_scripts_are_checked_out_from_trusted_base():
    """A PR must not replace scripts later run with pull-request write access."""
    document = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
    steps = document["jobs"]["semantic-review"]["steps"]
    checkout = next(
        step
        for step in steps
        if str(step.get("uses", "")).startswith("actions/checkout@")
    )

    assert checkout["with"]["ref"] == "${{ steps.pr.outputs.base }}"
    assert checkout["with"]["persist-credentials"] is False
