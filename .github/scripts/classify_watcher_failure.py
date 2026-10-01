#!/usr/bin/env python3
"""Classify a failed self-update run and render a diagnosing issue body.

The 2026-08-20 → 10-01 stall was reported seven times as "Failed again" with no
cause, while the reason sat deep in a job log. This helper reads the failed
jobs' log text, maps it to a known failure class (each with a runbook anchor),
extracts the decisive evidence lines, and renders a status body that says what
failed, since when, how many runs in a row, why, and what fixes it.

Inputs are this workflow's own logs, which can quote upstream text (package
names, release-note fragments). Evidence is length-capped, stripped of control
characters, and placed in a code block whose fence is longer than any backtick
run inside it, so it cannot break out into the issue's markdown.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

RUNBOOK = "docs/runbooks/self-update.md"
MAX_LOG_BYTES = 4 * 1024 * 1024
MAX_EVIDENCE_LINES = 12
MAX_LINE_CHARS = 300

_PREFIX_RE = re.compile(r"^(?:[^\t]*\t){0,2}(?:﻿)?\d{4}-\d{2}-\d{2}T[0-9:.]+Z ?")
_CONTROL_RE = re.compile(r"\x1b\[[0-9;]*[A-Za-z]|[\x00-\x08\x0b-\x1f\x7f]")


@dataclass(frozen=True)
class FailureClass:
    key: str
    title: str
    fix: str
    patterns: tuple[str, ...]


CLASSES: tuple[FailureClass, ...] = (
    FailureClass(
        "evidence-gap",
        "Release evidence is incomplete (package surface could not be verified)",
        "A planned package surface has no exact or aligned version for the train, or its "
        "alignment is ambiguous. Check whether upstream moved, split, or republished the "
        "package; model a move as lifecycle state in `.github/maf-package-surfaces.json` "
        "and pin republished trains in `.github/maf-train-lock.json`.",
        ("Release evidence is incomplete",),
    ),
    FailureClass(
        "credential",
        "Watcher credential is missing, expired, or revoked",
        "Rotate the watcher token (target state: GitHub App, ROADMAP Z-01) and rerun.",
        ("COPILOT_ASSIGN_PAT is missing", "COPILOT_ASSIGN_PAT is expired", "Bad credentials"),
    ),
    FailureClass(
        "tool-version",
        "dotnet-inspect version mismatch",
        "Install or pin the expected dotnet-inspect version in the watcher.",
        ("Expected dotnet-inspect",),
    ),
    FailureClass(
        "nuget-unreachable",
        "NuGet was unreachable, so release state is unknown",
        "Usually transient; rerun. If it persists, check nuget.org status.",
        ("NuGet API unreachable", "NuGet request failed", "Could not resolve host"),
    ),
    FailureClass(
        "sequencing",
        "Release requested out of order or branch collision",
        "Process releases oldest-first; rerun without `maf_version`, or clean up the stale branch.",
        ("Process releases sequentially", "already exists; refusing", "appeared after selection"),
    ),
    FailureClass(
        "registry-extraction",
        "Registry extraction or de-duplication failed",
        "Inspect the extraction ledger artifact on the run.",
        ("Registry de-duplication failed", "Cannot prove per-package registry extraction"),
    ),
)

UNKNOWN = FailureClass(
    "unknown",
    "Unclassified failure",
    "Read the evidence below and the run log; add a signature for this class to "
    "`classify_watcher_failure.py` and the runbook once understood.",
    (),
)


def clean_lines(log_text: str) -> list[str]:
    lines = []
    for raw in log_text.splitlines():
        line = _CONTROL_RE.sub("", _PREFIX_RE.sub("", raw)).rstrip()
        if line:
            lines.append(line[:MAX_LINE_CHARS])
    return lines


def classify(log_text: str) -> tuple[FailureClass, list[str]]:
    """Return the failure class and the decisive evidence lines."""

    lines = clean_lines(log_text)
    for failure in CLASSES:
        for index, line in enumerate(lines):
            if any(pattern in line for pattern in failure.patterns):
                evidence = [line.replace("##[error]", "").replace("::error::", "").strip()]
                if failure.key == "evidence-gap":
                    for follow in lines[index + 1 : index + 1 + MAX_EVIDENCE_LINES]:
                        if re.match(r"^\s*- [a-z0-9-]+: ", follow):
                            evidence.append(follow.strip())
                return failure, evidence[: MAX_EVIDENCE_LINES + 1]
    errors = [
        line.replace("##[error]", "").strip()
        for line in lines
        if "##[error]" in line or "::error::" in line or "Error:" in line
    ]
    return UNKNOWN, (errors or lines)[-MAX_EVIDENCE_LINES:]


def code_block(lines: list[str]) -> str:
    text = "\n".join(lines) if lines else "(no log lines captured)"
    longest = max((len(m) for m in re.findall(r"`+", text)), default=0)
    fence = "`" * max(3, longest + 1)
    return f"{fence}text\n{text}\n{fence}"


def render_body(
    failure: FailureClass,
    evidence: list[str],
    *,
    workflow: str,
    run_url: str,
    repo_url: str,
    streak: int,
    first_failure: str,
) -> str:
    runbook = f"{repo_url}/blob/main/{RUNBOOK}#{failure.key}"
    return "\n".join(
        [
            f"**{workflow} is failing.** This issue is rewritten on every failed run and "
            "should be closed once a run succeeds.",
            "",
            "| | |",
            "|---|---|",
            f"| Failure class | `{failure.key}`: {failure.title} |",
            f"| Failing since | {first_failure} |",
            f"| Consecutive failed runs | **{streak}** |",
            f"| Latest failed run | {run_url} |",
            f"| Runbook | {runbook} |",
            "",
            "**What fixes it:** " + failure.fix,
            "",
            "**Evidence** (from the failed job log):",
            "",
            code_block(evidence),
        ]
    ) + "\n"


def streak_from_runs(runs: list[dict], branch: str = "main") -> tuple[int, str | None]:
    """Count this run plus consecutive earlier failures; return the earliest date.

    ``runs`` is newest-first ``gh run list`` JSON. The current run is still in
    progress, so it is skipped and counted as one failure. Runs on other
    branches (dry runs) are ignored; ``gh run list --branch`` is not used
    because it silently drops scheduled runs.
    """

    streak, first = 1, None
    for run in runs:
        if run.get("headBranch", branch) != branch:
            continue
        if run.get("status") != "completed":
            continue
        if run.get("conclusion") != "failure":
            break
        streak += 1
        first = run.get("createdAt")
    return streak, first


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--runs", type=Path, help="gh run list JSON (newest first)")
    parser.add_argument("--workflow", required=True)
    parser.add_argument("--run-url", required=True)
    parser.add_argument("--repo-url", required=True)
    parser.add_argument("--now", required=True, help="ISO timestamp of this run")
    parser.add_argument("--branch", default="main", help="branch whose runs form the streak")
    parser.add_argument("--body-out", type=Path, required=True)
    parser.add_argument("--summary-out", type=Path, required=True)
    args = parser.parse_args(argv)

    try:
        raw = args.log.read_bytes()[-MAX_LOG_BYTES:] if args.log.exists() else b""
        runs = json.loads(args.runs.read_text(encoding="utf-8")) if args.runs else []
    except (OSError, json.JSONDecodeError) as exc:
        print(f"warning: {exc}; classifying with partial input", file=sys.stderr)
        raw, runs = b"", []

    failure, evidence = classify(raw.decode("utf-8", errors="replace"))
    streak, first = streak_from_runs(runs if isinstance(runs, list) else [], args.branch)
    first_failure = (first or args.now)[:10]
    body = render_body(
        failure,
        evidence,
        workflow=args.workflow,
        run_url=args.run_url,
        repo_url=args.repo_url,
        streak=streak,
        first_failure=first_failure,
    )
    args.body_out.write_text(body, encoding="utf-8")
    args.summary_out.write_text(
        json.dumps({"class": failure.key, "title": failure.title, "streak": streak,
                    "first_failure": first_failure}) + "\n",
        encoding="utf-8",
    )
    print(f"Classified as {failure.key} (streak {streak}, since {first_failure})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
