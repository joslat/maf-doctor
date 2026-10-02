---
name: maf-release-watcher
description: "Detects new MAF releases and keeps the registry, matrix and guides current. Pipeline: (1) the watcher diffs every tracked package surface and opens a scaffold PR on a per-version branch, (2) the maf-registry-fill agentic workflow fills the TODOs inside Actions, (3) required PR gates decide; releases without breaking changes auto-merge, breaking ones wait for review. This skill documents the loop end to end."
---

# maf-release-watcher

## Purpose

When Microsoft ships a new MAF version, this pipeline keeps the toolkit's three artefacts current with zero manual baseline-update work:

- **`.maf-version`** — the tracked-current pointer
- **`docs/compatibility-matrix.md`** — dependency-version table (new row per release)
- **`guides/maf-X.Y.Z-migration-guide.md`** — per-version delta with banner pointing back to the chain
- **`guides/maf-current-migration-guide.md`** — auto-regenerated cumulative reference
- **`.github/skills/maf-obsolete-api-registry/registry.yaml`** — append-only breaking-change registry

Three of those (matrix row contents, per-version guide sections, registry-entry TODO fields) need *judgement* the deterministic pipeline can't automate: release notes have to be interpreted and before/after C# examples written. That work runs in the **`maf-registry-fill`** agentic workflow (GitHub Agentic Workflows, Copilot engine), which fills the scaffold PR in place.

---

## When this skill is "invoked"

It isn't, in the Copilot-Chat sense. The work runs in GitHub Actions VMs, not in a Copilot conversation. This skill is the **reference doc** explaining what those workflows do, so a maintainer reading the repo can understand the whole loop without piecing it together from two `.yml` files plus three Python scripts.

Triggers for the pipeline itself:

- **Weekly cron**: `0 6 * * 4` (Thursday 06:00 UTC) — defined in `.github/workflows/maf-release-watcher.yml`
- **Manual dispatch**: `gh workflow run maf-release-watcher.yml -f maf_version=X.Y.Z`

---

## Architecture — the three stages

```
TRIGGER (cron or gh workflow run)
   │
   ▼
┌────────────────────────────────────────────────────────────────────┐
│  STAGE 1 — Deterministic data extraction                           │
│  .github/workflows/maf-release-watcher.yml                         │
│  Runs on a fresh Ubuntu VM provisioned by GitHub Actions           │
│  NO LLM, NO AGENT — just shell + Python + dotnet CLI               │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  1.1  Select oldest untracked MAF stable from NuGet                │
│       (clean no-op while any watcher scaffold PR is in flight)     │
│  1.2  Resolve + diff the targeted release-critical package surfaces│
│  1.3  Fetch GitHub release notes from microsoft/agent-framework    │
│  1.4  Run `python3 .github/scripts/update_compat_matrix.py`        │
│          → inserts a new row at the top of compatibility-matrix.md │
│  1.5  Run `python3 .github/scripts/gen_guide_section.py`           │
│          → writes guides/maf-X.Y.Z-migration-guide.md              │
│          → ALSO regenerates guides/maf-current-migration-guide.md  │
│  1.6  Run `registry-extract --diff-file` for validated surfaces   │
│          → scoped drafts, no second network diff, then de-duplicate │
│  1.7  Update .maf-version                                          │
│  1.8  Upload plan/diff+extraction ledgers/diff-*.txt/notes         │
│          as workflow artefacts for reviewer audit                  │
│  1.9  git checkout -b release-watcher/maf-X.Y.Z                    │
│       git commit + git push (per-version branch, NOT main)         │
│  1.10 gh pr create --base main --head release-watcher/maf-X.Y.Z    │
│       (never commits to main; one watcher PR open at a time; an    │
│        open one warns after 7 days and fails the watcher at 14)    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
                                  │ pull_request: opened
                                  ▼
┌────────────────────────────────────────────────────────────────────┐
│  STAGE 2 — Agentic fill                                            │
│  .github/workflows/maf-registry-fill.md (compiled .lock.yml)       │
│  Copilot engine inside the workflow; THIS is the LLM part          │
├────────────────────────────────────────────────────────────────────┤
│  2.1  Follows .github/scripts/ai_fill_issue_prompt.md.tpl          │
│  2.2  Fills registry TODOs, guide sections, matrix row,            │
│       CompatibilityTool.cs; replaces each REVIEW sentinel with a   │
│       NEW same-package entry                                       │
│  2.3  Runs the release checklist (checks 1-11, incl. the CI        │
│       obligations gate) and iterates until it passes               │
│  2.4  Pushes to the PR branch via safe outputs (data files only)   │
│       and comments a summary for the reviewer                      │
└────────────────────────────────────────────────────────────────────┘
                                  │ pull_request: synchronize
                                  ▼
┌────────────────────────────────────────────────────────────────────┐
│  STAGE 3 — Gates and merge                                         │
├────────────────────────────────────────────────────────────────────┤
│  3.1  maf-ai-fill-verify (required): obligations contract, train   │
│       lock immutability, sentinel rule, verify-registry, cross-    │
│       file consistency, autonomy envelope (data paths only)        │
│  3.2  build-test + ci-invariants (required)                        │
│  3.3  Additive release: auto-merge. Breaking: maintainer review.   │
│  3.4  maf-doctor release: push a vX.Y.Z tag → release.yml          │
└────────────────────────────────────────────────────────────────────┘
```

---

## The Python helper scripts — what, where, why

The Python scripts in `.github/scripts/` are **CI-runner code** invoked by the Stage-1 workflow via `run: python3 .github/scripts/<file>.py`. They are NOT skills, NOT MCP tools, NOT Copilot-invoked. They run on the Ubuntu runner's pre-installed Python 3 during workflow execution.

They exist because the data transformation is **deterministic** (parse JSON, insert a row in a Markdown table, write a Markdown stub from a template) — work that doesn't need an LLM and shouldn't pay LLM-token costs.

### `update_compat_matrix.py`

- **Inputs (via env vars)**: `OLD_VERSION`, `NEW_VERSION`
- **Reads**: `docs/compatibility-matrix.md`
- **Writes**: same file, with a new row inserted at the top of the data section
- **Idempotency**: if a row for `NEW_VERSION` already exists, the script no-ops (`exit 0`).
- **Position-agnostic**: inserts before the FIRST `**X.Y.Z**` row it finds, so the matrix stays ordered newest-first regardless of which version was previously latest.

### `gen_guide_section.py`

- **Inputs (via env vars)**: `OLD_VERSION`, `NEW_VERSION`
- **Reads**: `maf-package-plan.json`, `maf-diff-results.json`, every planned `diff-<surface>.txt`, and `release-notes.txt`
- **Writes**:
  - `guides/maf-<NEW>-migration-guide.md` — per-version delta file
  - `guides/maf-current-migration-guide.md` — auto-regenerated cumulative concatenation of all per-version files (TOC at top, ascending version order)
- **Idempotency on the per-version file**: re-runs preserve everything under the `## Human additions` heading; only the `AUTO-GENERATED START / END` block is overwritten.
- **Banner**: every auto-generated per-version file now opens with a callout that says it's a **delta only** and points readers at the chain or at the cumulative file. This is what makes the per-version files honest about their scope.
- **Per-package evidence**: every validated surface preserves all breaking and potentially-breaking rows plus a bounded additive sample; untrusted diagnostics retain an 80-line cap. A long Core report cannot crowd Harness/Hosting/later evidence out of the guide.
- **Cumulative-only repair**: `python3 .github/scripts/gen_guide_section.py --cumulative-only` rebuilds the cumulative guide solely from checked-in per-version guides; it needs no ephemeral release artifacts.

---

## Files modified by a successful run

After Stage 1 (the scaffold):

| File | Modification |
|---|---|
| `.maf-version` | New version |
| `docs/compatibility-matrix.md` | New row at top of data table (`unknown` cells for some columns) |
| `guides/maf-X.Y.Z-migration-guide.md` | New per-version file with banner + AUTO-GENERATED stub + Human-additions marker |
| `guides/maf-current-migration-guide.md` | Regenerated cumulative file |
| `.github/skills/maf-obsolete-api-registry/registry.yaml` | New entries appended (with TODO placeholders for `fix_description`, `example_before`, `example_after`, `guide_section`) |

After Stage 2 (the agentic fill, pushed to the same PR):

| File | Modification |
|---|---|
| `registry.yaml` | TODO placeholders filled in for that version's entries |
| `compatibility-matrix.md` and `CompatibilityTool.cs` | `unknown` cells filled where the release notes or packages reveal them |
| Per-version guide | `Breaking Changes`, `New Patterns`, `Obsolete APIs`, `Known Misalignments` sections filled; terminal-escape artefacts cleaned |

---

## Human review checklist (breaking releases, after the fill comment)

- [ ] **Registry entries**: spot-check the entries the agent flags in its comment (it says which `cs_warning` values are inferred) against the upstream `dotnet-X.Y.Z` source. Compile if you can.
- [ ] **Compat matrix**: if cells are still `unknown`, you'll need to look up versions from the MAF csproj on NuGet — the agent leaves them when release notes are ambiguous.
- [ ] **Migration guide "Breaking Changes"**: confirm one bullet per registry entry; no breaking change in the diff is missed.
- [ ] **Migration guide "New Patterns"**: confirm each PR # mentioned corresponds to a real MAF PR (cross-reference at `https://github.com/microsoft/agent-framework/pull/<N>`).
- [ ] **`guide_section`**: for entries on a new MAF surface, value should be `N/A` (not `TBD`, not `TODO`). `TBD` is rejected by `verify-registry`.
- [ ] **`## Human additions` heading**: should be untouched.

---

## Limitations the pipeline can't solve

- **Obsolete-by-attribute APIs not in the diff**: the pinned `dotnet-inspect` surfaces `[Obsolete]` at the type/member level. But the COMPILER is still ground-truth for transitive obsoletions, overload-resolution surprises, and project-local `[Obsolete]` decorations. Run `MafRunCs0618Hunt` against a real project pinned to the new version to catch these.
- **Behavioural changes the diff can't see**: a method whose signature is identical but whose runtime semantics changed (e.g. "now returns ValueTask<T> instead of starving silently") is invisible to `dotnet-inspect`. The agent records `[BREAKING]` release-note items with no API diff as behavioral reviews in the guide; it catches some of these but not all.
- **Whether the new version is actually *good***: the pipeline tells you what changed, not whether you should upgrade. That call is human.

---

## Related artefacts

- **Workflows**: `.github/workflows/maf-release-watcher.yml`, `.github/workflows/maf-registry-fill.md`, `.github/workflows/maf-ai-fill-verify.yml`, `.github/workflows/maf-freshness.yml`, `.github/workflows/maf-fill-repair.yml`
- **Runbook**: `docs/runbooks/self-update.md` (one section per watcher failure class)
- **Python helpers**: `.github/scripts/gen_guide_section.py`, `.github/scripts/update_compat_matrix.py`
- **CLI used by Stage 1**: `maf-doctor registry-extract` (from the published NuGet tool)
- **MCP tool for multi-version paths**: `MafMigrationPath(currentVer, targetVer)` — returns the ordered chain of per-version guide sections to read
- **Resource**: `maf://compatibility` — exposes the matrix to the LLM at chat time
