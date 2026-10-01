# maf-doctor — Setup Guide

> Last updated: 2026-05-13 — Phase S landed: multi-target nupkg + central package management. The toolkit installs on **.NET 8, .NET 9, OR .NET 10** runtimes (NuGet picks the matching TFM at install time).

This guide explains what you need to do — as a human — to use and maintain `maf-doctor`. It covers three usage modes, repository setup, the CI workflow, and answers the "Copilot Coding Agent vs. shell scripts" architecture question.

---

## Current state

MAF Doctor is published on NuGet (`maf-doctor`, `maf-doctor.Analyzers`) and keeps
itself current: the release watcher, the agentic fill, the PR gates, the freshness
SLO and Dependabot lock-file repair run in this repository's Actions (see
[`TROUBLESHOOTING.md`](../TROUBLESHOOTING.md#self-update-workflows-github-actions)).
To run that automation in a fork you need the two secrets below, plus repository
auto-merge and the required checks (`verify`, `Build + test (net8/9/10)` and the six
`ci-invariants` jobs) on `main`.
Releases are cut automatically when a MAF train merges if the repository variable
`AUTO_RELEASE` is `true` (`gh variable set AUTO_RELEASE --body true`); otherwise push a
`vX.Y.Z` tag yourself.

---

## Three Usage Modes

### Mode A — VS Code Copilot Chat (no repo needed)

Use the agents, skills, and guide directly in VS Code without running the MCP server.

**Prerequisites:** VS Code + GitHub Copilot (any plan)

**Steps:**
1. Clone or fork this repo (or copy `.github/` into your project)
2. Open the folder in VS Code
3. In Copilot Chat, agent mode is automatically aware of `.github/agents/`, `.github/skills/`, and `.github/instructions/`
4. Use `@maf-migration` to start a migration, `@maf-auditor` to audit a codebase

**What you get:**
- All 11 skills loaded on demand
- Always-on constraint rules from `maf-constraints.instructions.md`
- The full 21-section migration guide navigated by `maf-migration-guide` skill
- No MCP server needed

---

### Mode B — MCP Server (local, development build)

Run the MCP server locally to expose `maf://` resources and `/maf-*` prompts in Copilot Chat.

**Prerequisites:** any of .NET 8 SDK / .NET 9 SDK / .NET 10 SDK (the solution multi-targets all three; `global.json`'s `rollForward: latestMajor` will pick whichever is installed) — plus VS Code + GitHub Copilot

**Steps:**
1. Clone this repo
2. Verify `.vscode/mcp.json` already points to the local build:
   ```json
   "maf-doctor": {
     "type": "stdio",
     "command": "dotnet",
     "args": ["run", "--project", "src/maf-autopilot/maf-autopilot.csproj", "--"]
   }
   ```
3. Open VS Code — the MCP server starts automatically when Copilot needs it
4. In Copilot Chat, reference `maf://constraints` or use `/maf-audit` slash command

**What you get additionally over Mode A:**
- `maf://constraints`, `maf://registry`, `maf://guide`, `maf://skills?name=*` as referenceable resources
- `/maf-audit`, `/maf-migrate`, `/maf-cs0618-hunt` as slash commands
- `MafApiSafety`, `MafRegistryLookup`, `MafRegistryList` tools

---

### Mode C — MCP Server (global tool, any project)

Install `maf-doctor` as a global .NET tool so any project can run `maf-doctor init` and use the MCP server without cloning this repo.

**Prerequisites:** any of .NET 8 / .NET 9 / .NET 10 SDK. Install the [latest published release](https://www.nuget.org/packages/maf-doctor) from NuGet — the toolkit keeps its own MAF knowledge current (see the README's "It updates itself").

**Steps:**
```bash
# Install once, globally
dotnet tool install -g maf-doctor

# In any MAF project folder:
maf-doctor init
```

`maf-doctor init` will:
- Wire the MCP server for **VS Code** (`.vscode/mcp.json`) and **Claude Code** (`.mcp.json`) — idempotent, safe to re-run
- Write overwrite-on-reinit steering **sidecars** (never merged into your own instruction files): `.github/instructions/maf-doctor.instructions.md` (Copilot), `.claude/maf-doctor.md` + a one-line `@import` in `CLAUDE.md` (Claude Code), and an `AGENTS.md` managed block — read natively by GitHub Copilot's coding agent and by Cursor, so those two already get baseline steering from this file alone
- With `--with-cursor`: also write `.cursor/rules/maf-doctor.mdc`, a dedicated Cursor rule reinforcing the `AGENTS.md` block (not the only path to Cursor steering)

See [init-reference.md](./init-reference.md) for exactly what's written and why.

**Then open VS Code** — the MCP server starts automatically.

---

## Creating the GitHub Repository

### Repository details

| Field | Value |
|-------|-------|
| **Name** | `maf-doctor` |
| **Owner** | your GitHub username or org (e.g. `joslat`) |
| **Visibility** | **Private** (start here — you can make it public later) |
| **Description** | AI-powered toolkit for Microsoft Agent Framework (MAF) migrations — agents, skills, MCP server, and self-updating guide |
| **Tagline** (for topics) | `maf` `dotnet` `migration` `copilot` `mcp-server` `ai-agents` |

### Steps

**Option 1 — GitHub CLI (fastest):**
```bash
cd C:\git\joslat\maf-doctor

# Create private repo on GitHub and push
gh repo create joslat/maf-doctor \
  --private \
  --description "AI-powered toolkit for Microsoft Agent Framework (MAF) migrations — agents, skills, MCP server, and self-updating guide" \
  --source . \
  --remote origin \
  --push
```

**Option 2 — GitHub web UI + manual push:**
1. Go to [github.com/new](https://github.com/new)
2. Repository name: `maf-doctor`
3. Description: `AI-powered toolkit for Microsoft Agent Framework (MAF) migrations — agents, skills, MCP server, and self-updating guide`
4. Visibility: **Private**
5. Leave "Initialize this repository" unchecked (you already have local files)
6. Click **Create repository**
7. In your local terminal:
   ```bash
   git remote add origin https://github.com/joslat/maf-doctor.git
   git branch -M main
   git push -u origin main
   ```

### After the repo is created

- **Add topics** (GitHub repo page → gear icon next to About): `maf`, `dotnet`, `migration`, `copilot`, `mcp-server`, `ai-agents`
- **Enable Actions:** Settings → Actions → General → Allow all actions → Save
- **Verify the workflow is visible:** Actions tab → you should see "MAF Release Watcher" listed (no runs yet — that's expected)
- **Add `NUGET_API_KEY` secret** when ready to publish (see [Publishing to NuGet.org](#publishing-to-nugetorg-step-25))

> **When to go public:** Once `maf-doctor init` is end-to-end testable (after #25 NuGet publish), flip visibility to public under Settings → Danger Zone → Change repository visibility.

---

## Setting Up the GitHub Repository

To use the release watcher workflow and eventually publish the NuGet package, you need a GitHub repository.

### Option A — Use this repo directly

If you are **joslat** (the repo owner): the repository is `github.com/joslat/maf-doctor`. The workflow is already present. Skip to [adding secrets](#adding-github-secrets).

### Option B — Fork for your own use

1. Fork `joslat/maf-doctor` on GitHub
2. In your fork: **Settings → Actions → General → Allow all actions** (ensure Actions are enabled)
3. Change the `.maf-version` file if you want to track a different baseline version

### Option C — New repo (bring your own guide)

1. `gh repo create your-org/maf-doctor --private` (or public)
2. Push this codebase: `git remote add origin <url> && git push -u origin main`
3. Enable Actions (Settings → Actions → General)

---

### Adding GitHub Secrets

The repository automation uses two repository-level Actions secrets:

| Secret | Where to get it | Required for |
|--------|-----------------|-------------|
| `NUGET_API_KEY` | [nuget.org → API Keys](https://www.nuget.org/account/apikeys) | The separate `release.yml` NuGet publish workflow |
| `COPILOT_ASSIGN_PAT` | GitHub fine-grained PAT, scoped to this repository | Pushing watcher scaffold branches, the `maf-registry-fill` agent's push to the PR branch, and Dependabot lock-file repairs (pushes made with `GITHUB_TOKEN` would not trigger CI) |

The semantic-review workflow does not need another stored secret. It uses the
run's short-lived `GITHUB_TOKEN` with only `copilot-requests: write`; for this
personally owned repository, GitHub bills those requests to the repository
owner's Copilot seat. Copilot tools are not enabled in that workflow.
The workflow passes an empty `model` so Copilot CLI chooses the seat's supported
default rather than a pinned model that may not be available to every account
(`actions/ai-inference` v3 would otherwise default to gpt-4.1).

**How to add:**
1. GitHub repo → **Settings → Secrets and variables → Actions → New repository secret**
2. Add each secret by its exact name.
3. Scope `NUGET_API_KEY` to package push. Scope `COPILOT_ASSIGN_PAT` to Metadata R plus Issues, Contents, and Pull requests R/W on this repository only.

The watcher uses `GITHUB_TOKEN` for its read-only checkout, validates
`COPILOT_ASSIGN_PAT` before doing expensive analysis, and reserves that token
for the final branch push/PR operation. It deliberately does not fall back to
`GITHUB_TOKEN` for the push: GitHub suppresses downstream `pull_request` events
for changes made by that token, which would create a scaffold PR with no CI.

> The `GITHUB_TOKEN` secret is automatically provided by GitHub Actions — no setup needed.

---

### Enabling the Workflow

The file `.github/workflows/maf-release-watcher.yml` is already in the repo. GitHub Actions will pick it up automatically once the repo is on GitHub.

**What the workflow does:**

1. **Runs weekly** (Thursday 06:00 UTC) — or manually via **Actions → MAF Release Watcher → Run workflow**.
2. **`check-for-new-maf-release`** — reads the NuGet stable-version index and selects the oldest release newer than `.maf-version`. If any watcher scaffold PR is already open, it exits cleanly so updates stay sequential.
3. **`analyze-and-update`** — runs adjacent-version `dotnet-inspect` diffs, updates the matrix/code matrix, writes a per-version guide, appends registry drafts, and opens `release-watcher/maf-X.Y.Z` as a PR. It never pushes the scaffold to `main`.
4. **Opening the scaffold PR triggers `maf-registry-fill`**, an agentic workflow that fills the TODOs on the PR branch and runs the release checklist plus the obligations gate. `maf-ai-fill-verify` (required) then decides; releases without breaking changes auto-merge, breaking ones wait for review. `maf-ai-fill-todos.yml` remains a manual fallback.
5. NuGet publication is separate: tags or a manual dispatch run `release.yml`; the watcher does not publish MAF Doctor.

**Manual trigger with version override:**
- Go to Actions → MAF Release Watcher → Run workflow
- Optionally specify a version (e.g., `1.14.0`) to process a specific release.
- Leave `push_target` blank for the normal per-version branch; set it only for an explicit throwaway-branch dry run. `main` and `master` are rejected.

---

## Publishing to NuGet.org (Step #25)

This is the next required user action. Once done, anyone can `dotnet tool install -g maf-doctor --prerelease`.

### Prerequisites
- NuGet.org account (free at [nuget.org](https://www.nuget.org/users/account/LogOn))
- `NUGET_API_KEY` secret added to GitHub repo (see above)

### Steps

**Option 1 — Via GitHub Actions (recommended for ongoing releases):**
1. Add `NUGET_API_KEY` to GitHub secrets
2. Push a reviewed semver tag (`vX.Y.Z` or `X.Y.Z`) to run `release.yml`, or go to Actions → Release maf-doctor → Run workflow
3. Optionally supply the package version on manual dispatch; otherwise the workflow uses the project version
4. Review the test, package, provenance-attestation, NuGet-push, and GitHub-release steps

**Option 2 — Manual first publish (bootstraps NuGet listing):**
```powershell
cd src/maf-autopilot
dotnet pack -c Release -o ./nupkg
dotnet nuget push ./nupkg/maf-doctor.*.nupkg --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json
```

### After publishing
- The package appears on NuGet.org within ~15 minutes
- Users can then `dotnet tool install -g maf-doctor --prerelease` and `maf-doctor init`
- The separate `release.yml` workflow handles future tagged or manually dispatched releases

---

## Filling Registry Drafts in CI

The watcher deterministically extracts and appends candidate registry entries. Breaking releases remain red until their semantic fields and guide sections are filled. A maintainer can fill them directly on the scaffold branch or dispatch `maf-ai-fill-todos.yml`, which opens a tightly-scoped issue and assigns the selected Coding Agent to create a fill PR targeting that scaffold branch.

The AI-fill handoff is deliberately manual: the watcher does not start a paid/non-deterministic agent run automatically, and no AI-inferred migration reaches `main` without the registry and cross-file verification gates.

---

## Architecture: Copilot Coding Agent vs. Shell Scripts

The watcher (`analyze-and-update`) uses deterministic shell, Python, the locally built MAF Doctor CLI, and `dotnet-inspect`. Semantic TODO filling is a separate, manual Coding-Agent issue/PR handoff. These are complementary stages, not alternative implementations of the same job.

Here is an honest comparison:

### Current approach — Shell Scripts

```yaml
- name: Generate draft guide section
  run: |
    printf '\n\n---\n\n' >> guides/maf-1.3.0-migration-guide.md
    cat >> guides/maf-1.3.0-migration-guide.md << EOF
    ## Section AUTO — ...
    EOF
```

**Pros:**
- No Copilot Enterprise required — works with any GitHub Actions plan
- Fast (seconds, not minutes)
- Deterministic — same input always produces same output
- No token cost
- Draft sections are clearly templated — reviewers know exactly what is auto-generated vs. human-written

**Cons:**
- `registry.yaml` entries cannot be auto-written without LLM reasoning — pure scripts don't know what `AddFanInBarrierEdge` changing signatures means semantically
- Guide section quality is template-based — placeholder text, not analysis
- Human must still write the real content (but the PR creates a structured checkpoint)

---

### Semantic stage — Coding Agent

The `maf-ai-fill-todos.yml` workflow renders a fenced, version-specific issue prompt, creates the issue, and assigns Copilot, Claude, or Codex through GitHub's Coding Agent integration. The agent must base its work on the watcher scaffold branch and open its fill PR back to that branch.

**Pros:**
- Can write **meaningful** `registry.yaml` entries (semantic understanding of what changed)
- Can write **meaningful** guide section content (not just placeholders)
- Can reason about breaking changes in natural language

**Cons:**
- Requires **GitHub Copilot Enterprise** or Teams with Coding Agent enabled — not available on free/Pro plans
- Slower — agent may take 5–30 minutes
- Non-deterministic — output quality varies; human review is even more critical
- Token costs apply
- Agent may hallucinate API names — the PR review step is not optional

---

### Recommendation: Hybrid approach (the current design)

**Use shell scripts for everything that is deterministic:**
- NuGet version check (pure HTTP)
- `dotnet-inspect diff` execution (CLI invocation)
- Compatibility matrix row insertion (regex replacement)
- `.maf-version` update (string write)
- PR creation with templated checklist

**Use Copilot Coding Agent ONLY for `registry.yaml` semantic update (step #30):**
- The diff is already available as an artifact (`diff-core.txt`)
- The agent prompt can be tightly constrained: "read this diff, find CS0618-risk patterns, add entries matching this YAML schema"
- The PR review checklist already asks reviewers to verify registry entries — the agent output gets the same human check

**Why this is better than all-scripts OR all-agent:**

| Task | Scripts | Agent | Best choice |
|------|---------|-------|-------------|
| Version check | ✅ Perfect | Overkill | Scripts |
| Diff generation | ✅ Perfect | Can't (CLI) | Scripts |
| Compat matrix row | ✅ Good enough | Unnecessary | Scripts |
| Guide section template | ✅ Placeholder is fine | Better prose | Agent (optional) |
| `registry.yaml` entries | ❌ Cannot do semantically | ✅ Ideal fit | **Agent (recommended)** |
| PR creation | ✅ Perfect | Not needed | Scripts |

**Bottom line:** deterministic extraction creates an auditable scaffold; optional agent reasoning fills only the semantic gaps; mechanical and semantic PR checks guard the result.

---

## Prerequisite Chain Summary

```
You want: dotnet tool install -g maf-doctor --prerelease
  └─ Requires: #25 NuGet publish
       └─ Requires: NuGet.org account + API key
            └─ Action: Add NUGET_API_KEY to GitHub repo secrets

You want: registry.yaml semantic TODO filling via Coding Agent
  └─ Requires: manually dispatch maf-ai-fill-todos.yml for the scaffold branch
       └─ Requires: a supported GitHub Coding Agent enabled for the repo
            └─ Requires: COPILOT_ASSIGN_PAT for automatic issue assignment

You want: maf_open_feedback_issue tool
  └─ Requires: #31 tool implementation
       └─ Prerequisite: #25 NuGet publish (so binary users can trigger it)
```

**No prerequisites** (works right now):
- Mode A (VS Code Copilot Chat agents/skills)
- Mode B (local MCP server via `dotnet run`)
- The `maf-release-watcher.yml` workflow itself (just needs the repo on GitHub)

---

## Quick Reference: What Runs Where

| Component | Runs where | Needs |
|-----------|-----------|-------|
| Agents / skills / instructions | VS Code Copilot Chat (host-side) | VS Code + Copilot |
| MCP server (local dev) | Your machine | .NET 8 / 9 / 10 SDK (any one) |
| MCP server (global tool) | Your machine | .NET 8 / 9 / 10 runtime (any one) + `dotnet tool install -g maf-doctor --prerelease` |
| Release watcher workflow | GitHub Actions | GitHub repo |
| NuGet publish | GitHub Actions | `NUGET_API_KEY` secret |
| Registry scaffold extraction | GitHub Actions | GitHub repo; no LLM |
| Registry semantic fill | GitHub Coding Agent + Actions | Enabled Coding Agent + `COPILOT_ASSIGN_PAT` for assignment |
