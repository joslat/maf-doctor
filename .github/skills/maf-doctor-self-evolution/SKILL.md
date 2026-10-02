---
name: maf-doctor-self-evolution
description: "Maintainer runbook for keeping maf-autopilot current with new MAF releases. Use when a new MAF version has shipped and .maf-version is stale. Covers the full loop: pre-flight check → trigger watcher → monitor PR → fill TODOs → review → merge. Distinct from maf-release-watcher (which describes what the automation does internally); this skill tells YOU what to do as the operator."
---

# maf-doctor-self-evolution

## Purpose

When Microsoft ships a new MAF release, maf-autopilot must evolve to track it:
`.maf-version`, the compatibility matrix, per-version migration guides, and the
obsolete-API registry all need updating. Most of the work is automated — your job
as maintainer is to **trigger the right workflow, review the PR, and merge it**.

This skill is the step-by-step operator runbook. For a description of what the
pipeline does internally, see the `maf-release-watcher` skill.

---

## Pre-flight — is an update actually needed?

Run these checks before touching anything:

```powershell
# 1. What version is the toolkit currently tracking?
Get-Content .maf-version   # should be the last known-good MAF version

# 2. What is the latest STABLE MAF version on NuGet?
$raw = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/microsoft.agents.ai/index.json"
$raw.versions | Where-Object { $_ -notmatch '-(alpha|beta|rc|preview)' } | Select-Object -Last 3

# 3. Is there already an open PR or issue for the new version?
gh issue list --label maf-release --repo joslat/maf-doctor
gh pr list --base main --repo joslat/maf-doctor
```

If `.maf-version` **matches** the latest stable on NuGet → nothing to do.
If `.maf-version` is **behind** → proceed.

---

## Step 1 — Trigger the release watcher

```bash
gh workflow run maf-release-watcher.yml \
  --repo joslat/maf-doctor \
  -f maf_version=X.Y.Z
```

Replace `X.Y.Z` with the new MAF version (e.g. `1.13.0`).

Leave `maf_version` blank to have the watcher auto-detect the latest NuGet stable:

```bash
gh workflow run maf-release-watcher.yml --repo joslat/maf-doctor
```

> **Cron note**: the watcher also runs every Thursday at 06:00 UTC
> (`0 6 * * 4`). If a release landed mid-week you can trigger manually rather
> than waiting.

---

## Step 2 — Watch the run

```bash
# List the most recent watcher runs
gh run list --workflow maf-release-watcher.yml \
  --repo joslat/maf-doctor --limit 5

# Tail the live log of the most recent run
gh run watch --repo joslat/maf-doctor
```

A successful run will:

1. Run `dotnet-inspect diff` for `Microsoft.Agents.AI` and
   `Microsoft.Agents.AI.Workflows` — diffs are uploaded as artifacts.
2. Fetch release notes from `microsoft/agent-framework`.
3. Classify the release as **additive** or **breaking**.
4. Write/update: `.maf-version`, `docs/compatibility-matrix.md`,
   `src/maf-autopilot/Tools/CompatibilityTool.cs`,
   `guides/maf-X.Y.Z-migration-guide.md`, `guides/maf-current-migration-guide.md`,
   `.github/skills/maf-obsolete-api-registry/registry.yaml`.
5. Push to branch `release-watcher/maf-X.Y.Z` and open a PR to `main`.

**If the run fails**: a GitHub issue labelled `maf-release` is opened automatically
with a link to the failed run. Fix the root cause (usually a network hiccup, a
missing secret, or a new tag format), then re-run.

---

## Step 3 — Understand the PR classification

Open the PR the watcher created:

```bash
gh pr list --base main --repo joslat/maf-doctor
```

The PR title and body state the verdict:

| Verdict | What it means | PR CI state on arrival |
|---------|--------------|------------------------|
| **additive** | No `.NET [BREAKING]` notes, no removed members. All stubs auto-filled. | ✅ Green (should be) |
| **breaking** | Removed/changed APIs. Registry has `TODO` draft entries. | 🔴 Red by design — `verify-registry` gate holds it red until TODOs are filled |

---

## Step 4 — Fill the TODOs (breaking releases only)

The `keep-breaking-red` gate in CI rejects any registry entry that still has a
`TODO`/`TBD`/`XXX` placeholder in a required field.

Opening the scaffold PR starts `maf-registry-fill`, an agentic workflow that fills
`fix_description`, `example_before`, `example_after` and `guide_section` on the PR
branch, runs the release checklist, and pushes the fill. If the required `verify`
check then fails, `maf-fill-repair` re-runs the agent once. Act only when the PR
is labelled `needs-human`:

### Option A — Assign GitHub Copilot to the PR

```bash
gh pr edit release-watcher/maf-X.Y.Z \
  --add-assignee Copilot \
  --repo joslat/maf-doctor
```

Copilot reads the PR diff, fills the entries, and pushes commits to the branch.

### Option B — Fill manually

Check out the scaffold branch, fill the TODOs following
`.github/scripts/ai_fill_issue_prompt.md.tpl`, run its verification checklist,
and push to the same branch.

---

## Step 5 — Review the PR

Before merging, verify:

- [ ] **`.maf-version`** contains exactly `X.Y.Z`.
- [ ] **`docs/compatibility-matrix.md`** has a new row for `X.Y.Z` at the top of
  the data table. No `unknown` cells remain unless the release notes genuinely
  don't mention the dependency version (leave a `<!-- TODO: -->` comment in that case).
- [ ] **`guides/maf-X.Y.Z-migration-guide.md`** exists and the `Breaking Changes`
  section matches the registry entries for this version.
- [ ] **Registry entries**: spot-check 1–2 `example_before` / `example_after` snippets
  against the real MAF surface. Compile locally against `X.Y.Z` if you can.
- [ ] **`guide_section`** fields: for APIs with no parallel in a prior guide, value
  must be `N/A` (not `TBD`, not `TODO`).
- [ ] CI is green (both `build-test` and `ci-invariants` pass).

---

## Step 6 — Merge

```bash
gh pr merge release-watcher/maf-X.Y.Z \
  --merge --repo joslat/maf-doctor
```

Use `--merge` (not squash) to preserve the watcher's commit chain.

After merge, verify:

```bash
# Confirm main is updated
git fetch origin main
git show origin/main:.maf-version   # should read X.Y.Z

# Confirm the compatibility-matrix drift test passes
dotnet test src/maf-autopilot.Tests/ \
  --filter "FullyQualifiedName~CompatibilityMatrix"
```

---

## Step 7 — Post-merge housekeeping

- Close any stale `maf-release` issues that were tracking the old version:
  ```bash
  gh issue list --label maf-release --state open --repo joslat/maf-doctor
  ```
- If a `🔴 Self-update workflow failed` issue is open from a prior failed run,
  close it now that the update is complete.
- Run `maf-migration-retrospective` if the watcher surfaced any unexpected
  breaking patterns — add them to the registry so the next cycle starts smarter.

---

## Troubleshooting quick-reference

| Symptom | Root cause | Fix |
|---------|-----------|-----|
| Watcher run exits in ≤ 15 s with "No update" | `.maf-version` already equals NuGet latest | Nothing — toolkit is current |
| `verify-registry` CI gate is red after fill | A registry entry still has `TODO`/`TBD`/`XXX` | Fix the placeholder, push to the branch |
| `push_target` rejected ("must not be a protected branch") | `push_target=main` was passed | Omit `push_target` — the watcher manages its own branch |
| Copilot PR sits unapproved for > 24 h | Copilot's required-status-check approval is pending | Approve from the PR's "Copilot code review" section, or fill manually |
| `dnx is not found` error | Stale `dnx` invocation in a fork / old cached step | Ensure workflow uses `dotnet tool install --global dotnet-inspect --version 0.9.1` |
| Release notes fetched empty | Tag format changed (e.g. `dotnet-X.Y.Z` vs `vX.Y.Z`) | Check `gh release list --repo microsoft/agent-framework` for the real tag |
| `Watcher credential is missing` / `Bot App token could not be minted` | The bot GitHub App is not set up, or its key or installation is wrong | Set `MAF_BOT_CLIENT_ID` (variable) and `MAF_BOT_PRIVATE_KEY` (secret), and install the App on the repository |

---

## Secrets required

| Name | Kind | Purpose |
|------|------|---------|
| `MAF_BOT_CLIENT_ID` | Variable | Client ID of the bot GitHub App. Each run mints a short-lived token to push branches and tags and open PRs (pushes made with `GITHUB_TOKEN` do not trigger CI). |
| `MAF_BOT_PRIVATE_KEY` | Secret | The bot App's private key. App permissions: Contents, Issues and Pull requests read and write; Administration and Metadata read-only; no Workflows. |
| `COPILOT_ASSIGN_PAT` | Secret | Fallback while the App is not set up; delete it once the App runs. |

---

## Related artefacts

- **Skill** `maf-release-watcher` — internal pipeline anatomy (what each stage does)
- **Workflow** `.github/workflows/maf-release-watcher.yml` — Stage 1 (detect + scaffold)
- **Workflow** `.github/workflows/maf-registry-fill.md` — Stage 2 (agentic fill on the scaffold PR)
- **Workflow** `.github/workflows/maf-ai-fill-verify.yml` — PR gate (rejects unfilled drafts)
- **Script** `.github/scripts/gen_guide_section.py` — generates the per-version guide stub
- **Script** `.github/scripts/update_compat_matrix.py` — inserts the matrix row
- **File** `.maf-version` — single source of truth for the tracked MAF version
