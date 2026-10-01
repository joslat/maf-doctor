# AGENTS.md — maf-autopilot (MAF Doctor)

This is **MAF Doctor** (NuGet package: `maf-autopilot`) — an MCP server + skills
plugin for Microsoft Agent Framework (MAF) lifecycle maintenance: audit, fix,
migrate, and keep registry fresh.

## When working on this codebase

| Area | Location |
|---|---|
| Production code | `src/maf-autopilot/` (MCP tools, prompts, resources) |
| Roslyn analyzers | `src/maf-autopilot.Analyzers/` (MAF001/002/003) |
| Tests | `src/maf-autopilot.Tests/`, `src/maf-autopilot.Analyzers.Tests/` |
| Skills (procedural) | `.github/skills/` (load into GitHub Copilot Coding Agent) |
| Specialist agents | `.github/agents/` (`.agent.md` files for Copilot Coding Agent) |
| CI / maintenance workflows | `.github/workflows/` (12 YAML workflows + 1 agentic workflow, `maf-registry-fill.md` → compiled `.lock.yml` via `gh aw compile`) |
| Self-update runbook | `docs/runbooks/self-update.md` (one section per watcher failure class) |
| Obsolete-API registry | `.github/skills/maf-obsolete-api-registry/registry.yaml` |
| MAF migration guide | embedded in NuGet (`guide.md`) |
| Multi-version samples | `samples/maf-1.0-sample/`, `maf-1.2-sample/`, `maf-1.3-sample/` |
| Workshop | `samples/workshop.md` |

## Adding features

- **New MCP tool** → see `CONTRIBUTING.md` §"Adding an MCP server tool". Pattern: `src/maf-autopilot/Tools/<Name>Tool.cs` with `[McpServerToolType]` + `[McpServerTool]` + `[Description]`.
- **New registry entry** → edit `.github/skills/maf-obsolete-api-registry/registry.yaml`. Include `applies_to_codebases` markers (`pre-X.Y.Z` / `X.Y.Z+` / exact `X.Y.Z`).
- **New skill** → `.github/skills/<name>/SKILL.md` + register in `MafResources.AllowedSkillNames` in `src/maf-autopilot/Resources/MafResources.cs`.
- **New specialist agent** → `.github/agents/<name>.agent.md`.

## Coding standards

- **Tool descriptions**: verb-first opening, 80-char strong, when-to-use clarity, disambiguated from neighbors, machine-routable returns.
- **All writes**: support `--dry-run`.
- **All outputs**: support structured (JSON/SARIF) where CI integration matters.
- **Registry entries**: always include `applies_to_codebases` markers.

## Companion docs

- `CLAUDE.md` — Claude Code-specific working notes (this is where Claude reads when invoked from `claude-code`)
- `CONTRIBUTING.md` — contributor playbook (setup, conventions, PR norms, security rubric for new MCP tools)
- `SECURITY.md` — vulnerability-disclosure policy + scope (GitHub Security tab landing)
- `docs/security.md` — user-facing "how MAF Doctor is hardened" overview + Cisco mcp-scanner results + canonical-source references
- `docs/security/threat-model.md` — full attack-surface map, closure list (§4), and documented residual gaps (§5)
- `CHANGELOG.md` — release history

## The self-update loop (unique to this project)

When Microsoft ships a new MAF version, the `maf-release-watcher` workflow detects
it, resolves every package surface (`.github/maf-package-surfaces.json`, pinned by
`.github/maf-train-lock.json`), diffs it with dotnet-inspect, drafts registry entries
with TODOs, and opens a scaffold PR on `release-watcher/maf-X.Y.Z`. The agentic
workflow `maf-registry-fill` (GitHub Agentic Workflows; source `.md`, compiled
`.lock.yml`) then fills the TODOs inside Actions, runs the release verification
checklist, and pushes the fill to the PR branch through safe outputs restricted to
the registry, guides, compatibility matrix and `CompatibilityTool.cs`. The PR gates
(`maf-ai-fill-verify`, crossfile consistency, `verify-registry`) decide. Watcher
failures are classified into a single rolling issue that links
`docs/runbooks/self-update.md`.

Edit an agentic workflow by changing its `.md` and running `gh aw compile <name>`;
never hand-edit a `.lock.yml`.
