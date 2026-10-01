# Changelog

All notable changes to **maf-autopilot (MAF Doctor)** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added

- **MCP spec 2026-07-28 (stateless) is served.** With the 2.2.0 SDK, hosts can skip the
  `initialize` handshake: `server/discover`, `tools/list` and `tools/call` work with
  per-request `_meta`. The classic handshake still negotiates up to 2025-11-25.
- **An MCP round trip runs on every PR** (`build-test`): every tool, prompt, resource
  and template is listed, every resource read, the argument-free prompts rendered, a
  real tool call made, and the stateless 2026-07-28 flow exercised.
- **Automatic releases.** When a watcher PR merges (`.maf-version` changes on `main`),
  `auto-release` tags the next minor version, so each MAF train ships as a maf-doctor
  release with no human step. Switched by the `AUTO_RELEASE` repository variable.
- **Release canary.** Before anything is pushed to NuGet, `release.yml` installs the
  packed tool from the local `.nupkg`, checks `--version`, runs an MCP stdio smoke test
  (handshake, capability lists, a real tool call) and `verify-registry` against the
  registry embedded in the package.
- **New MAF packages can no longer go unnoticed.** The daily `maf-freshness` job lists
  every verified `Microsoft.Agents.AI*` package on nuget.org and keeps a rolling issue
  open for any that is neither a watcher surface, a lifecycle-event package, nor
  ignored with a reason in `.github/maf-package-discovery.json`. 24 of 35 packages had
  gone undiffed for months.
- **Freshness badge.** The README shows the `maf-freshness` status; the run now turns red
  while the SLO is breached or a package is untracked.
- `CLAUDE.md` with the repository's working notes (it was referenced but missing).

### Changed

- **MCP C# SDK 2.2.0** (`ModelContextProtocol`, was 1.4.1). A drop-in for MAF Doctor:
  same protocol version, tools, prompts, resources and templates (verified over stdio).
- `xunit.runner.visualstudio` 4.0.0 (test-only).

### Fixed

- **The fill agent was told to follow MAF 1.3.0 rules.** `.github/copilot-instructions.md`
  was a stale `init` output ("NEVER introduce patterns not present in MAF 1.3.0") that
  Copilot loads as repository instructions, including in the agentic fill. It now
  describes this repository and points to the version-scoped constraints.
- Docs describe the current self-update loop (agentic fill, required gates, freshness,
  lock-file repair) instead of the retired Copilot-coding-agent issue flow:
  `TROUBLESHOOTING.md`, `docs/setup.md`, the `maf-release-watcher` skill, the watcher's
  commit message and notices.

## [1.16.0] - 2026-10-01

**MAF 1.18 → 1.23, all 35 Agent Framework packages, and a release pipeline that
fills, verifies and largely heals itself.** The watcher was stuck from July to
October; it is unstuck, fails loudly with a diagnosis, and an in-workflow agent
fills each release. The tool now says UNKNOWN instead of SAFE past its coverage.

### Microsoft Agent Framework lifecycle intelligence

- Added ordered migration intelligence for MAF **1.18 → 1.19 → 1.20 → 1.21 → 1.22
  → 1.23**: per-release guides, compatibility-matrix rows and registry entries
  (1.22: session stores promoted to `Microsoft.Agents.AI.AgentSessionStore`,
  key-based `Get`/`SaveSessionAsync`, removed `DeleteSessionAsync`, sticky Foundry
  user identity; 1.23: async Declarative `GetChatOptionsAsync`/`EvalAsync`,
  Foundry `Azure.AI.Extensions.OpenAI` parameter types).
- The watcher now diffs all **35** `Microsoft.Agents.AI*` packages (was 11).

### Fixed

- **Per-release steering covers MAF 1.18–1.23.** The `maf-constraints` resource's
  version checkpoint table stopped at 1.17.
- **The Copilot semantic review stopped working after `actions/ai-inference` v3.**
  v3 defaults `model` to gpt-4.1 when the input is omitted, which re-created the
  "model unavailable to this seat" failure. The workflow now passes an empty
  `model` so the Copilot CLI picks one.
- **`verify` blocked PRs cut before a MAF release merged.** The AI-fill verifier
  compared `.maf-version` with the current `main` tip and reported a "version bump
  without obligations" on PRs that never touched it (Dependabot #201 after MAF 1.22).
  The verifier now compares from the merge-base, and the scope step uses the PR's own
  three-dot diff, so stale branches are no longer pulled into the full verification.
- **The replay suite failed once the release backlog was cleared.** It required a
  release newer than `.maf-version` in its snapshot, which blocked the MAF 1.23.0 PR.
  Being caught up now passes; `maf-freshness` watches upstream lag.
- **`maf_generate_regression_plan` listed almost no changes.** Each step looked up
  entries applicable to a codebase already *on* the target version. Migration entries
  are marked `pre-<version>`, so every entry from registry-extract was dropped, and
  steps from 1.4 on showed "no registry changes". Steps are now evaluated from their
  source version.
- **Registry hygiene.** All 162 entries now carry `applies_to_codebases`, and
  `verify-registry` rejects a missing or malformed marker (a missing one silently meant
  "applies to every version"). The two 1.5.0 `TodoProvider` entries, unfilled since
  May, now document `GetAllTodosAsync`/`GetRemainingTodosAsync` (CS1061). The 1.11.0
  `SearchFilesAsync` entries now say CS1503 rather than claiming a positional token
  silently binds to `recursive`. MAF161-EXTENSIO-001 is marked `pre-1.6.1`
  (was `1.6.1+`, which fired only on already-migrated code).
- **Release watcher stalled since 2026-08-20 (7 failed runs).** The MAF 1.17
  Durable Task / Azure Functions repository externalization was modelled as a
  one-release exception, so every later train failed the complete-evidence gate.
  Externalization is now lasting lifecycle state: later trains plan the moved
  surfaces as informational (or diff them if upstream realigns), while every
  other surface stays fail-closed. MAF 1.18 → 1.23 now plan cleanly.
- **Upstream prerelease republishes no longer make a processed train ambiguous.**
  A new train lock (`.github/maf-train-lock.json`, written by the watcher via
  `update_train_lock.py`) pins the next train's old side to exactly what was
  diffed; the 1.17 transition uses the event's pinned source version.

### Added

- **Dependabot NuGet PRs repair their own lock files.** Dependabot regenerates
  `packages.lock.json` for one target framework only, so every NuGet bump failed
  locked restore (NU1004). `dependabot-lockfile-repair` regenerates the lock files
  for net8.0/net9.0/net10.0 and pushes them back. It acts only on same-repo
  `dependabot/nuget/*` branches that Dependabot alone wrote and that change only
  `PackageVersion` lines and lock files. gh-aw's runtime action is excluded from
  Dependabot; upgrade it with `gh aw upgrade`.
- **`MafDoctorStatus` reports knowledge freshness.** Besides tool and init
  freshness, it now shows which MAF version the installed registry covers, how many
  stable MAF releases on nuget.org are newer (cached 24 h, 3 s timeout, honors
  `MAF_DOCTOR_UPDATE_CHECK`), and the workspace's coverage horizon.
- **Additive MAF releases merge themselves.** The watcher enables auto-merge on
  additive scaffold PRs. They land once every required check passes (build-test,
  ci-invariants, `verify`). Breaking releases keep human review.
- **Autonomy envelope in the required `verify` check.** A `release-watcher/maf-*` PR
  that touches anything outside the scaffold/fill data paths (registry, guides,
  compatibility matrix and its code mirror, `.maf-version`, train lock, obligations)
  fails `verify` and can never auto-merge.
- **Freshness SLO (`maf-freshness`, daily).** Compares the MAF release covered on
  `main` and in the latest release tag with nuget.org. More than one release or ten
  days behind keeps a rolling "MAF knowledge is stale" issue open, which closes
  itself once fresh again.
- **Stalled watcher PRs fail.** An open scaffold PR warns after 7 days and fails the
  watcher after 14, with a new `stalled-pr` failure class and runbook section.
- **Coverage horizon (honest verdicts past the registry).** `doctor` (markdown, plan
  and JSON) and `MafApiSafety` now compare the project's `Microsoft.Agents.AI*`
  package versions (including Central Package Management pins) with the embedded
  registry's MAF version. Past it, `doctor` shows a banner and an additive
  `coverage_gap` JSON field, and `MafApiSafety` (with the new optional `repoPath`)
  answers **UNKNOWN** instead of SAFE. Without `repoPath`, SAFE now states where
  coverage ends.
- **Every train-aligned MAF package is diffed (11 → 35 surfaces).** The watcher now
  covers `Microsoft.Agents.AI.Abstractions`, A2A, OpenAI, Anthropic, Foundry,
  Declarative, Workflows.Declarative, LocalCodeAct, Purview and the other
  `Microsoft.Agents.AI*` packages that ship with each MAF release. The planner
  treats a package with no version at or before the old train as new in this train,
  and one with no version at or before the new train as not yet published. Both
  are informational and evidence-based; a package that merely skipped a train still
  fails closed.
- **`Microsoft.Agents.AI.Mcp` is now a tracked package surface.** MAF 1.19's only
  breaking change (`ListAgentToolsWithTaskSupportAsync` → `ListAgentToolsWithTasksAsync`,
  `McpTaskOptions.DefaultTimeToLive` removed, MCP SDK floor 1.2.0 → 2.1.0) lived in
  this alpha package, which the watcher did not diff. From 1.20 on it is diffed like
  every other surface.
- **REVIEW sentinels can be resolved in an untracked MAF package.** A sentinel means
  "classified breaking, but no tracked surface produced diff evidence", so its
  package is only a default guess. The fill gate now accepts concrete entries for an
  untracked `Microsoft.Agents.AI*` package; entries in another *tracked* package (or
  outside MAF) still don't count.
- **Watcher replay harness** (`test_watcher_replay.py`): replays every adjacent
  MAF train 1.9 → 1.23 against a recorded nuget.org snapshot, so a manifest,
  lock, or planner change that would strand a train fails CI.
- **Diagnosing failure issues**: a failed watcher run now classifies its own
  failure (evidence gap, credential, tool version, NuGet unreachable, sequencing,
  registry extraction), rewrites the tracking issue with cause, streak, first
  failure, evidence and runbook link, and escalates (`p0` + owner mention) from
  the second consecutive failure.
- **Self-update runbook** (`docs/runbooks/self-update.md`).
- **`maf-registry-fill` agentic workflow** (GitHub Agentic Workflows, Copilot
  engine): fills a scaffold PR inside Actions, runs the verification checklist,
  and pushes only to an allowlisted set of files through validated safe outputs.
  Triggered when a scaffold PR opens (or is labelled `ai-fill`), or by dispatch.

## [1.15.0] - 2026-08-17

**Sequential MAF release intelligence, deterministic package evidence, and a
repaired CI/self-update path.** MAF Doctor now tracks each Microsoft Agent
Framework release in order instead of collapsing a backlog into the newest tag.

### Microsoft Agent Framework lifecycle intelligence

- Added reviewed, ordered migration intelligence for MAF **1.14 → 1.15 → 1.16
  → 1.17**, including exact package maturity/version resolution, public API and
  dependency-floor evidence, behavioral compatibility checkpoints, registry
  recipes, and regenerated cumulative guidance.
- Captured the 1.14 agent/session, approval, Harness, AG-UI, GitHub Copilot, and
  Shell breaks; the 1.15 preview Hosting session-store contract; the 1.16
  VectorData 10.7 and runtime-lifecycle changes; and the 1.17 declarative
  failure/redaction behavior plus Durable Task/Azure Functions externalization.

### Fixed

- **Installed migration knowledge is cumulative and ordered.** `maf://guide`
  now embeds the checked-in 1.3→1.17 cumulative guide instead of the stale
  1.3-only document. `MafMigrationPath` accepts both legacy and current metadata
  shapes, excludes the already-applied source step, and returns each intervening
  release in order.
- **Release artifacts are verified before publication.** The release workflow
  now validates both NuGet packages and both symbol packages, including package
  identity, version, TFMs, tool settings, analyzer layout, README, icon, and
  symbol payloads. Symbol artifacts are attested and attached to the GitHub
  release, and tag-triggered GHCR publishing now rejects commits outside `main`.
- **Semantic review restored after GitHub Models retirement.** The workflow now
  uses GitHub Copilot CLI with the short-lived `GITHUB_TOKEN`, an exact-pinned
  CLI, no enabled Copilot tools, and the seat's supported default model rather
  than an account-dependent hard-coded model.
- **Watcher credentials fail early and clearly.** Read-only checkout now uses
  `GITHUB_TOKEN`; the maintainer token is validated before analysis and reserved
  for the CI-triggering push. Failure notification now identifies the repository
  explicitly instead of requiring a nonexistent checkout.
- **Watcher diff evidence is complete again.** The watcher now pins
  `dotnet-inspect` 0.9.1, the first release built after the upstream Unix
  seekable-stdout fix, verifies the installed version, and rejects any raw diff
  whose package/version header or summary counts do not reconcile or whose body
  contains unrecognized formatter output. This closes
  the 0.7.8 Linux Native-AOT failure that exited 0 but left a 256-byte fragment.
- **Interactive package diffs enforce the same inspector version.** The shared
  process runner now verifies exact `dotnet-inspect` 0.9.1 before invoking it
  (SemVer build metadata is accepted), fails closed on missing, mismatched, or
  unverifiable executables, and reaches the exact-version `dnx` fallback only
  after the existing explicit `MAF_DOCTOR_ALLOW_TOOL_DOWNLOAD` opt-in.
- **Registry-aware compiler diagnostics no longer overcount generic errors.**
  `MafRunCs0618Hunt` still retains every legacy CS0618/CS0246 finding, while
  newer registry diagnostic codes such as CS1503 now require a matching
  same-code registry type, member, or signature symbol before being reported as
  a MAF migration finding.
- **Watcher package coverage is release-aware and fail-closed.** A checked-in
  manifest now plans ten targeted release-critical surfaces (including base
  Hosting, Harness, GitHub Copilot, and Tools Shell) with independently resolved
  package versions, bounded
  per-surface diffs, lifecycle events, and a deterministic result ledger. Every
  expected report must validate; breaking and potentially-breaking summary rows,
  missing release notes, an empty later-package report, a package split, or
  unverifiable evidence remain review-required. Registry
  extraction consumes those same files with scoped IDs instead of repeating the
  network diff, records a strict per-surface extraction ledger, and cannot let a
  successful Core draft hide a failed or structurally uncovered later package.
  Generated guides retain every breaking/potentially-breaking row while bounding
  additive samples independently for each surface.
- **Cumulative migration-guide repair no longer needs ephemeral artifacts.**
  `gen_guide_section.py --cumulative-only` rebuilds the current cumulative guide
  solely from checked-in per-version guides, making the AI-fill completion path
  deterministic after human edits.
- **Release backlogs no longer skip intermediate stable MAF versions.** The watcher selects the oldest untracked stable release and permits only one scaffold PR in flight, preserving per-version compatibility rows and migration guides when Microsoft ships more than once between weekly runs.
- **Cost audit false positives removed.** CLI command entry points and the built MCP/host `RunAsync()` loop are no longer mistaken for uncapped agent inference calls.
- **MCP scanner cache made runner-writable.** pipx now installs and caches under the workspace instead of `/opt`, eliminating tar permission failures and perpetual cache misses on hosted runners.
- **CLI dispatch test made deterministic on Windows.** The valid-path smoke test now scans its own empty temporary directory instead of the entire shared temp root, avoiding parallel net8/9/10 timeouts on busy developer machines.

### Documentation

- Reconciled the watcher schedule, PR-only update flow, manual AI-fill handoff, workflow count, and cross-guide section rules with the live implementation.

## [1.14.0] - 2026-07-25

**The Fable-5 residual-review remediation — 113 of 115 findings across the autofix engine, CLI, scan core, reporting surfaces, and the entire CI / release / AI-automation pipeline — shipped as nine independently-reviewed phases (PRs #154–163), followed by a security-documentation refresh (#164) and a report-readability fix from user feedback (#165).** CI green across net8/9/10 and Linux.

> 🙏 Thanks to **Jesse Liberty** for the feedback that prompted the report-readability improvement — scored reports are now much easier to scan.

### Security
- **Fence-label injection closed (SEC-01).** The LLM data-fence *label* is now sanitized to `[A-Za-z0-9._-]` (capped at 64 chars) in both the C# (`LlmFencing`) and Python (`llm_fencing.py`) fencers, so an attacker-influenced registry id can no longer inject text outside the data fence a downstream model reads.
- **Subprocess secrets no longer inherited (SEC-09).** Every `dotnet` / `dotnet-inspect` / `git` child is spawned with credential-like environment variables (`*TOKEN*`, `*SECRET*`, `*KEY*`, `*PASSWORD*`, `…PAT`, `AWS_*`, `AZURE_*`, `GITHUB_*`, `GH_*`) stripped — a build/inspect run on an untrusted repo can't read the MCP host's secrets.
- **Hostile `.git/config` neutralized (SEC-22).** Every `git` invocation forces `core.fsmonitor=false`, `core.hooksPath=`, `core.pager=cat`, `protocol.ext.allow=never`, and `GIT_CONFIG_NOSYSTEM`, closing the command-execution vectors a malicious repo's git config could otherwise trigger.
- **Verifiable release provenance (SEC-13).** `release.yml` now produces a signed build-provenance attestation over the published `.nupkg`s and refuses to publish from a tag that isn't an ancestor of the reviewed `main` branch.
- **NuGet vulnerability audit has teeth (SEC-15).** `NuGetAuditMode=all` plus `NU1903`/`NU1904` as errors — a HIGH/CRITICAL transitive advisory now fails restore in every CI lane, release included.
- **Fork-safe PR automation (SEC-11).** Jobs that build PR-supplied (possibly fork) code no longer hold `pull-requests: write`; they upload an artifact and a single trusted `workflow_run` companion posts the sticky comment from the base-repo context.
- **Fail-closed release classification (WM-11).** The release-watcher captures the upstream `dotnet-inspect` diff as stdout only, empties it on any non-zero exit, and requires a `**Summary:**` marker — a garbled or truncated diff now classifies as *breaking*, never silently additive.
- **Non-vacuous scanner gate (SEC-12 / WM-13).** The mcp-scanner check rejects a "0 tools scanned" handshake and no longer lets scanner stderr merge into the results file where a benign line could forge a clean verdict.
- **Widened CI templating-injection gate (WM-14).** The `ci-invariants` guard now also blocks fork-controlled event free-text (PR / issue / comment / review titles + bodies) and single-line `run:` forms from being interpolated into shell.
- **Container capability boundary (SEC-10).** SDK-only tools detect a missing binary in a minimal container and return an explicit "use the global tool" message instead of crashing opaquely.

### Fixed
- **Autofix engine hardening (Phase 1).** Source-corruption, pipeline, encoding, preview, and file-lock fixes across the Roslyn rewriters — rewriter output is now compile-verified.
- **CLI & MCP lifecycle (Phase 2).** Strict argument handling, honest exit codes, and correct stdout/stderr stream separation.
- **Scan-core correctness (Phase 3).** Parse-once + parallel scanning, honest scan caps, and the safe-child-process routing noted above.
- **Reporting accuracy (Phases 4–6).** A single-source health grade, an additive-diff contract, full SARIF fidelity (help URIs + partial fingerprints), prose-to-data conversions, and tool-description / prompt / doc accuracy fixes — the numbers a report shows now reconcile across the markdown, `--all`, `--plan`, `--json`, and SARIF surfaces.
- **PR-audit & drafted-issue quality (Phase 5).** More accurate PR-audit output and neutralization of untrusted fork text in generated issues.

### Changed
- **The scored report is now scannable (#165).** The default `maf-doctor doctor` report spaces each finding out and separates findings with a horizontal rule; the `--all` grouped view does the same between rule-groups. Machine surfaces (`--json`, `--plan`, `--plan-json`, SARIF) are byte-for-byte unchanged. _(Suggested by Jesse Liberty.)_

### Documentation
- **Security docs federated + refreshed (#164).** The four security documents were de-duplicated and given one job each — a navigation map, a single canonical scan-result home, and accuracy fixes reflecting the hardening above (env-scrubbing and provenance gaps closed, tool count corrected to 28, new attack classes documented).

## [1.13.0] - 2026-07-20

**2026-07-19 security assessment — all 30 findings closed, verified across four independent adversarial review rounds.** A full-repo security review (path-traversal/symlink defenses, subprocess-spawn safety, CI/CD supply-chain hardening, MCP tool-annotation accuracy) turned up 30 findings (F-01–F-30). All are addressed; residual, deliberately-deferred items are disclosed honestly in `docs/security/threat-model.md` §5 rather than hidden. **CI green across net8/9/10 and Linux.**

### Security
- **Symlink/path-escape defenses.** New shared primitives — `PathGuard.ValidateContainment` (rejects a symlink/reparse point on the leaf or any parent segment, not just the leaf) and `SafeWorkspaceWriter` (stages writes through an unpredictable temp name opened with `FileMode.CreateNew`, closing predictable-tmp-path symlink races) — now back every write site: `AutoFixTool`, `InitCommand` (including its backup path), the agent/executor scaffolders (CLI and MCP, both now all-or-nothing — a symlinked target refuses the whole scaffold instead of partially writing it), the registry-override loader, and the update-check cache.
- **MCP-only workspace allowlist.** `MAF_DOCTOR_WORKSPACE_ROOTS` scopes which absolute paths an MCP-connected agent can point tools at; filesystem roots and the user's home directory are always rejected regardless of configuration. `init` sets this to the initialized repo on first write. CLI usage (a human typing a path at their own shell) is unaffected — this boundary applies to MCP-driven tool calls only.
- **Bounded, deadlock-safe subprocess handling.** All `dotnet build` / `dotnet-inspect diff` / `git` invocations now go through one shared `ProcessRunner.Run` — concurrent stdout/stderr draining against a shared budget (closing a stdout/stderr pipe-deadlock class of bug and a "buffer the whole output before truncating" memory issue), argv-only (`ArgumentList`, never the unsafe `Arguments` string property), one real `Process.Start` call site enforced by CI. The `dnx dotnet-inspect` auto-download fallback is now gated behind `MAF_DOCTOR_ALLOW_TOOL_DOWNLOAD` (default off).
- **`autofix-all` / `MafAutoFixAll` now preview by default.** Both the CLI (`autofix-all` requires `--apply` to write) and the MCP tool (`dryRun` defaults to `true`) default to preview-only — previously both wrote by default, which a client that misread an MCP confirmation hint (or a human running the wrong command) could apply without review.
- **CI/CD supply-chain hardening.** Every privileged workflow now builds `maf-doctor` from the trusted checkout instead of a floating `--prerelease` NuGet install; restores in `--locked-mode` against a real `packages.lock.json` (all 4 projects); pins every third-party GitHub Action to an exact commit SHA (Dependabot keeps them current) and every Python tool dependency to an exact version; the release-watcher's checkout no longer persists its maintainer PAT into `.git/config` for the whole job (`persist-credentials: false`, explicit auth immediately before the one `git push`); the `mcp-scanner` PR gate is a hard-fail (not advisory) requiring an exact clean-scan shape; PR-audit verifies a PR's number against the GitHub API before posting a comment rather than trusting an artifact's own claim; base container images are digest-pinned.
- **Bounded reads everywhere a file gets fully materialized.** `MafExplainFinding`, the `.csproj`/`Directory.Packages.props` scans in `DraftIssueTool`/`SemanticKernelDetectorTool`, and PR-audit's per-file/aggregate-byte budgets all cap how much of a file is read into memory, with an explicit "scan incomplete" signal (not a silently-clean result) when a cap is hit.
- **CLI exit codes now reflect actual success/failure.** `doctor`, `badge`, `autofix-all`, and `migrate-scan` previously exited 0 even when they printed an error — any CI/script checking `$?` after these commands would have treated a failure as success. An unrecognized subcommand (e.g. a typo) previously fell through to MCP-server startup instead of erroring, which looked like a hang with stdin left open.
- **Tool-annotation accuracy.** Five `[McpServerTool]` `ReadOnly`/`Destructive`/`OpenWorld` drift cases were found and fixed (a wrong annotation is a privilege-escalation bug for MCP-spec-compliant clients that gate auto-invocation on it) and are now locked by a reflection test.
- **Documentation accuracy as a first-class deliverable.** `docs/security.md` and `docs/security/threat-model.md` were rewritten to cite real file:line locations and verified counts rather than round, stale numbers, and to distinguish deterministic guarantees ("Are we vulnerable? No.") from probabilistic mitigations ("Mitigated, not eliminated") for LLM-facing surfaces like prompt fencing.

See `docs/security/threat-model.md` for the full finding-by-finding closure record and the honestly-disclosed residual gaps (no sandbox around `dotnet build`, no full hash-pinning of Python dependencies, no SBOM/build-provenance attestation, and a local-dev-only test-reporting limitation that doesn't affect CI).

## [1.12.0] - 2026-07-19

**A second, more thorough steering-completeness pass** (following v1.11.0's dead-reference fix): audited the full live tool/prompt catalogue against what the steering docs actually tell the assistant to do, and closed the real gaps found.

### Added
- **Self-update awareness in steering.** Nothing in the four steering docs (or the `@maf` agent) ever mentioned `MafDoctorStatus` — the tool that reports whether the installed package or a workspace's `init` is stale. Added: check occasionally, surface the `dotnet tool update -g maf-doctor` + `maf-doctor init` flow when needed, confirming with the user before the global tool update (repo-scoped `init` is always safe to just run).
- **`maf://constraints` awareness in steering.** Every MCP prompt (`maf-audit`, `maf-migrate`, `maf-remediate`, `maf-migrate-from`, `maf-review`, `maf-debug`) reads `maf://constraints` — the non-negotiable hard rules — as its first step. The baseline steering never told the assistant to do the same for anything answered *outside* a prompt. Added as its own item, early in the list.
- **"Scan incomplete" awareness.** v1.10.0 added `scan_truncated` / `files_scanned` reporting when a repo scan hits a size cap, but steering never told the assistant to check for or surface it — a capped scan could silently present an incomplete grade as definitive. Added to the `MafDoctor`-first item.
- **`maf-onboarding` now referenced in steering**, not just in `maf-help`'s routing table — closes the gap where the prompt existed (added earlier in this same review cycle) but nothing in the baseline guidance pointed to it.
- **`MafAuditPullRequest` now referenced in steering** — the PR-scoped scanner was previously only mentioned inside the `maf-migrate` prompt's own text, not in the baseline guidance, despite being broadly useful for any MAF-touching PR.
- **Discovery fallback in steering.** Nothing in the four steering docs told the assistant what to do if a request didn't match any of the listed scenarios. Added a closing pointer to `MafTour()` (full capability catalogue) and the `maf-help` prompt (guided triage) — previously only reachable via the `@maf` agent (Mode 2 only), leaving Claude Code / Cursor / plain-`init` Copilot users with no discovery mechanism at all.

### Notes
- Deliberately NOT added to steering (to avoid turning it into a full tool index — that's what `MafTour` / `maf-help` are for): `MafScanAntiPatterns`, `MafValidateFanOut`, `MafScoreMigrationRisk`, `MafGenerateRegressionPlan`, `MafExplain`, and similar — already reachable via `maf-review` / `maf-debug` / the `@maf` agent's own decision tree, without duplicating that routing at the steering-baseline level.

## [1.11.0] - 2026-07-19

**Steering-wiring review: dead agent references fixed, a real onboarding-prompt gap closed, and a sweep of stale docs.** A review of how `init` steers GitHub Copilot, Claude Code, and Cursor found that all four shipped steering files (and two of the MCP prompts) told the assistant to hand off to `@maf-*` specialist-agent personas that `init`'s default install path never actually installs — dead references for anyone who just ran `maf-doctor init` rather than manually copying this repo's `.github/`.

### Added
- **`maf-onboarding` MCP prompt.** The `maf-help` triage prompt routed "I just joined the team" to `@maf-onboarding` — an agent persona with no MCP-prompt equivalent, unlike every other route. Added a real, always-available prompt mirroring the agent's guided first-day tour (codebase summary, agent topology via `MafSimulateWorkflow`, hot-path files, current grade + top fixes via `MafDoctor`). (Prompts 10 → 11.)

### Fixed
- **Dead `@maf-*` agent references in all four steering files and two MCP prompts.** `init` never copies `.github/agents/*.agent.md` into a consumer repo (only manually copying this repo's whole `.github/` in — "Mode 2" — does, and only for GitHub Copilot); there is no Claude Code subagent or Cursor custom-mode equivalent anywhere. So `.github/instructions/maf-doctor.instructions.md`, `.claude/maf-doctor.md`, the `AGENTS.md` block, `.cursor/rules/maf-doctor.mdc`, and the `maf-debug` / `maf-help` prompts were all routing to personas that don't exist for the common case — while the identical workflows were already sitting there, wired up by every `init` run, as MCP prompts (`maf-review`, `maf-audit`, `maf-migrate`, `maf-debug`, `maf-migrate-from`). Steering now leads with the prompts and mentions the `@`-mention personas only as a Mode-2, Copilot-only enhancement.
- **Sidecar files carried no "don't hand-edit me" warning.** `.github/instructions/maf-doctor.instructions.md`, `.claude/maf-doctor.md`, and `.cursor/rules/maf-doctor.mdc` are fully overwritten on every re-`init`, but the only comment that said so lived in the *source* snippet's header — which is stripped before the file is written to a consumer's repo. A team that hand-edited one of these lost the edit silently on the next `init`. Each now carries an accurate, undeleted note pointing at where durable customizations actually belong.
- **Stale header comments in the four `docs/steering/*.md` source snippets** — left over from a prior refactor (see the 1.x "no longer merges into your own files" entry below), they described appending into the user's own `copilot-instructions.md` / `CLAUDE.md` with merge semantics; the shipped design has been overwrite-on-reinit sidecars for a while. Rewritten to match, and to name Copilot's/Cursor's native `AGENTS.md` support.
- **Stale `docs/steering/README.md`** — said "these three files" (there are four) and claimed `cursor-rules.md` lands at `./.cursorrules`; the real destination is `.cursor/rules/maf-doctor.mdc`. Also now documents that GitHub Copilot's coding agent and Cursor both read `AGENTS.md` natively, so `--with-cursor` is a reinforcing layer, not the only path to Cursor steering.
- **`--with-cursor`'s console output named the wrong file** (`.cursorrules` instead of the actual `.cursor/rules/maf-doctor.mdc`).
- **Stale tool / prompt / skill counts drifted independently across docs** — README.md, NUGET_README.md, `docs/architecture.md`, `docs/init-reference.md`, `docs/quickstart.md`, and `docs/usage.md` variously still said 27 tools, 10 prompts, or 14 skills; the live counts (drift-tested in `TourToolTests`) are 28 tools, 11 prompts (with `maf-onboarding` above), 15 skills. Reconciled everywhere.
- **No drift guard on the three duplicated steering bodies.** `copilot-instructions.md`, `claude-instructions.md`, and `agents.md` intentionally share identical numbered guidance (different H2 titles/framing per tool) with no shared source and, previously, no test — a future edit to one could silently diverge from the other two. Added `SteeringBodies_CopilotClaudeAgents_ShareIdenticalActionableGuidance` to pin it.

## [1.10.0] - 2026-07-17

**MCP server memory leak fixed, repo-scan hardening, and our first outside contribution.** The MCP server no longer leaks memory while idle — a stray config-reload file watcher was recursively monitoring the MCP host's entire working directory, typically the user's home folder. Also closes a related class of bugs found while reviewing that fix: `repoPath` must now be a genuinely absolute path, every repo scan is bounded, and the `MAF PR Audit` CI check no longer fails on outside contributions. **CI green across net8/9/10.**

### Fixed
- **MCP server memory leak while idle.** `Host.CreateApplicationBuilder(args)` registers a config-reload file watcher on the process's content root by default; since MCP hosts (Claude Code, VS Code, Cursor) spawn `maf-doctor` with `cwd` set to the user's **home directory**, that watcher ended up recursively monitoring the entire home directory tree via FSEvents (macOS) — every filesystem event anywhere under `~` triggered stat/allocate work, growing to multiple GB within minutes under normal machine activity, multiplied per concurrent MCP session. `maf-doctor` reads no host/appsettings configuration, so it now uses `Host.CreateEmptyApplicationBuilder(...)`, which never creates the watcher at all — a fix that also heads off the Linux inotify-watch-limit and Windows `FileSystemWatcher` buffer-overflow variants of the same underlying issue.
- **`repoPath` now requires a genuinely absolute path.** Every tool description says "absolute path," but nothing enforced it — a relative value from an MCP caller resolved against the wrong working directory (the bug above, in a different guise), and `MafAutoFix` / `MafAutoFixAll` write by default. `PathGuard.ValidateRepoPath` now rejects anything that isn't `Path.IsPathFullyQualified` (not just `IsPathRooted`, which wrongly accepts Windows drive-relative paths like `C:tmp`). CLI usage (`maf-doctor doctor .`, etc.) is unaffected.
- **Repo scans are now bounded.** `SourceFileWalker` (shared by every scanning tool) caps both the number of files walked (50,000) and the size of any single file read (10 MB), so an accidentally-huge scan target can no longer turn one call into an unbounded-time, unbounded-memory operation. `doctor` surfaces a "Scan incomplete" note (markdown / plan) and additive `scan_truncated` / `files_scanned` fields (`--json` / `--plan --json`; schema stays v1 — see `docs/output-schemas.md`) whenever a scan is capped.
- **`MAF PR Audit` CI check no longer fails on outside contributions.** GitHub force-downgrades `GITHUB_TOKEN` to read-only for `pull_request` runs from a forked repository, regardless of declared workflow permissions — which is exactly what was failing this check on every external PR. The audit workflow no longer tries to post its own comment; a new companion workflow (triggered by `workflow_run`, running with a normal write-scoped token that never touches fork-supplied code) posts it instead.

### Acknowledgments
- **Alexander Nachtmann ([@ANcpLua](https://github.com/ANcpLua))** — maf-doctor's first outside contributor. Diagnosed the MCP server memory leak with a precise root-cause writeup (down to the exact `sample` stack trace) and shipped a minimal, well-verified fix. Software engineering student at FH Technikum Wien — thank you for the sharp catch!

## [1.9.0] - 2026-07-09

**MCP update/init freshness** — the MCP server now gives users a non-blocking startup advisory when a newer `maf-doctor` package is available, and separately detects when the installed tool is current but a workspace only needs `maf-doctor init` refreshed. Adds the **`MafDoctorStatus`** MCP tool for on-demand status, keeps init repair idempotent, and documents the exact update flow. **28 tools. CI green across net8/9/10.**

### Added
- **Startup update advisory.** On MCP startup, `maf-doctor` performs a short, cached NuGet check and logs the exact commands when action is needed:
  - `dotnet tool update --global maf-doctor`
  - `maf-doctor init`
- **Workspace init freshness detection.** `maf-doctor init` now stamps managed MCP entries with `MAF_DOCTOR_INIT_VERSION`, allowing the MCP startup/status check to distinguish "package update needed" from "package is current, but this repo needs init refreshed." Startup and status checks walk upward to the initialized workspace root so nested-directory launches report the right repo state.
- **`MafDoctorStatus` MCP tool.** Users can ask for the same package/init freshness report on demand from an MCP client, including whether to update the global tool, re-run init, or do nothing.

### Changed
- **Managed MCP entry repair.** Re-running `maf-doctor init` repairs stale managed entries, migrates legacy `maf-autopilot` entries to `maf-doctor`, preserves unrelated MCP servers, and refreshes the steering sidecars without touching hand-authored content.
- **Docs updated for the new behavior.** README, quickstart, usage, and init reference now highlight the startup advisory, `MafDoctorStatus`, and the safe re-run flow after updating.

### Fixed
- **Startup/read hardening.** Advisory and init-status reads are bounded and failure-tolerant: marker files are capped, update-cache reads reject oversized files, duplicate `User-Agent` headers are avoided, and prerelease/stable version comparisons no longer suppress valid update notices.

## [1.8.0] - 2026-07-09

**MAF 1.13.0 coverage** — a FileStore API rename across `AgentFileStore` / `FileSystemAgentFileStore` / `InMemoryAgentFileStore` — plus a new **`maf-doctor-self-evolution` maintainer runbook** skill and a verification-logic fix that prevents partially-filled scaffold entries from blocking the build. **15 skills. All tests green across net8/9/10.**

### Added
- **MAF 1.13.0 compatibility coverage** across the matrix, code matrix (`CompatibilityTool`), obsolete-API registry, and migration guides. 1.13.0 is a **.NET breaking** release — a FileStore API rename:
  - `AgentFileStore` / `FileSystemAgentFileStore` / `InMemoryAgentFileStore`: `WriteFileAsync` → `WriteAsync`, `ReadFileAsync` → `ReadAsync`, `DeleteFileAsync` → `DeleteAsync`, `ListFilesAsync` + `ListDirectoriesAsync` → `ListChildrenAsync` (members collapsed; use `FileListEntry.Type` to distinguish), `SearchFilesAsync` → `SearchAsync` (`MAF1130-FILESTORE-001` through `005`).
  - `FileListEntry.FileName` → `FileListEntry.Name` (`MAF1130-FILELIST-001`).
  - `FileAccessProvider` tool-name constants renamed: `SaveFileToolName` → `WriteToolName`, `ListFilesToolName` / `ListSubdirectoriesToolName` → `LsToolName`, `SearchFilesToolName` → `GrepToolName` (`MAF1130-FILEACCESS-001`).
  - 8 consolidated registry entries (the 24-member diff reduces to 8 patterns across 3 concrete types).
- **`maf-doctor-self-evolution` skill** (15th skill, served at `maf://skills?name=maf-doctor-self-evolution`). A maintainer action runbook: pre-flight checks, exact `gh workflow run` trigger command, PR review checklist, merge steps, post-merge housekeeping, and a troubleshooting table of known failure modes. Distinct from `maf-release-watcher` (which documents the pipeline internals); this skill is the human-facing "what do I do right now" guide.

### Fixed
- **`IsIncompleteDraft` verification gate** (`VerifyRegistryCommand`). The previous `IsRawDraft` logic only skipped entries where *all* fields were `TODO`. A partially-filled scaffold entry (non-`TODO` `obsolete_signature` + `replacement_signature` but `guide_section: TODO`) was no longer considered a raw draft, causing `LiveRegistry_PassesVerification` to fail on every breaking scaffold PR. The new `IsIncompleteDraft` skips any entry where *any* core field is still a placeholder — the monotonic fill model: checks only activate once every required field is filled.
- **Placeholder grammar single source of truth.** `placeholder-fixtures.json` is now the authoritative fixture consumed by both the C# `LooksLikePlaceholder` test and the Python `test_placeholder_fixtures.py` CI check, eliminating the historical divergence where em-dash TODO variants passed C# but failed Python.
- **Release-watcher scaffold now opens a PR (never pushes to main).** Following the MAF 1.11.0 incident (unfilled scaffold pushed to main turned main red and blocked all PRs), the watcher now pushes to a per-version branch (`release-watcher/maf-X.Y.Z`) and opens a PR. Main only receives the filled, build-green result on merge.

## [1.7.0] - 2026-07-02

**MAF 1.12.0 coverage** — a breaking skills-source refactor — and the first real-world exercise of the green-on-arrival watcher (its classifier was fooled by a truncated diff, but the keep-breaking-red gate + independent `registry-extract` correctly held the release red for review). **994 .NET tests (979 + 15 analyzer) green across net8/9/10, + 87 release-automation tests.**

### Added
- **MAF 1.12.0 compatibility coverage** across the matrix, code matrix (`CompatibilityTool`), obsolete-API registry, and migration guides. 1.12.0 is a **.NET breaking** release — a skills-source API refactor around a new `AgentSkillsSourceContext`:
  - `AgentSkillsSource` / `AgentFileSkillsSource` `GetSkillsAsync` gained a required `AgentSkillsSourceContext` first parameter (`MAF112-AGENT-001` / `MAF112-AGENT-002`);
  - `AgentSkillsProviderBuilder.UseFilter`'s predicate changed to `Func<AgentSkill, AgentSkillsSourceContext, bool>` (`MAF112-BUILDER-001`);
  - `AgentSkillsProviderOptions.DisableCaching` was **removed** — caching moved to the new `CachingAgentSkillsSourceOptions` type (`MAF112-OPTIONS-001`).

### Notes
- **Green-on-arrival, real-world validation.** The watcher's classifier mislabeled 1.12.0 as "additive" (its `dotnet-inspect` diff capture was truncated and the upstream release notes weren't published yet), but the independent `registry-extract` + the keep-breaking-red gate caught the true breaking set and held the scaffold PR **red** — the safety net worked as designed. The scaffold was filled by hand as a breaking release. The root-cause diff-capture truncation is tracked as a separate follow-up.

## [1.6.0] - 2026-06-29

**MAF 1.11.0 + 1.11.1 compatibility coverage** + a **green-on-arrival release-watcher** that auto-fills additive MAF releases (and holds breaking ones red for review), on the latest **ModelContextProtocol 1.4.0** runtime. The compatibility matrix, obsolete-API registry, and migration guides now span MAF up through 1.11.1. **994 .NET tests (979 + 15 analyzer) green across net8/9/10, + 87 release-automation tests.**

### Added
- **MAF 1.11.0 + 1.11.1 coverage** across every compatibility surface the tool serves: new `docs/compatibility-matrix.md` rows + `CompatibilityTool.Matrix` entries (`MafCompatibility`), obsolete-API registry entries for the 1.11.1 removals — `AgentSkillsProviderBuilder.UseScriptApproval()` and `AgentSkillsProviderOptions.ScriptApproval`, removed when AgentSkillsProvider tools became approval-by-default (MAF #6729) — plus the 1.11.0 `SearchFilesAsync` `recursive`-parameter break, and per-version migration guides (`guides/maf-1.11.0-…` / `maf-1.11.1-…`).
- **Release-watcher "green-on-arrival"** (repo automation). A fail-closed additive-vs-breaking classifier: an **additive** MAF release now produces a fully auto-filled, GREEN scaffold PR (compatibility-matrix doc + `CompatibilityTool.cs` code matrix + migration guide), while a **breaking** release keeps draft registry TODOs that a keep-breaking-red gate (+ a sentinel guard coupling the verdict to the gate) hold RED until a human writes the migration. Hardened across four adversarial-review passes (21 → 3 → 4 → 1 verified findings) + GitHub Copilot review; merge stays human-gated.

### Changed
- **ModelContextProtocol 1.3.0 → 1.4.0** and **Microsoft.Extensions.Hosting 10.0.8 → 10.0.9** — the shipped tool runs on the newer MCP runtime. CI dependency bumps: `actions/checkout` 6→7, `actions/setup-dotnet` 5.2→5.3, `softprops/action-gh-release` 3.0.0→3.0.1, `docker/metadata-action` + `docker/setup-buildx-action` minors.

### Fixed
- **SK→MAF migration review (Tier 1):** doc-accuracy pass + new drift tests coupling the SKILL strategy-tag table to the detector and the TourTool resource catalogue to every `[McpServerResource]` + the skills allowlist; and `migrate-scan --source` no longer consumes a following `--flag` as its value (+regression test).
- **Release-watcher reliability:** unblocked `main` after the MAF 1.11.0 auto-update pushed an unfilled scaffold straight to `main`; the watcher now opens a per-version PR and never pushes a scaffold to a protected branch.

### Acknowledgments
- The **Semantic Kernel → MAF migration** assistant (shipped in 1.5.0) was **suggested by Shawn Henry** — Principal Group Product Manager for Microsoft Agent Framework & Microsoft Foundry at Microsoft.

## [1.5.0] - 2026-06-25

**Migrate FROM Semantic Kernel TO MAF** — a cross-framework migration assistant (distinct from the existing MAF *version* upgrade path): a Roslyn detector (`migrate-scan` CLI + `MafDetectSourceFramework` MCP tool) that inventories SK usage and tags every construct 🌉 bridge / 🔁 rewrite / 🏗 re-architect with an EASY/MEDIUM/HARD verdict, an orchestration prompt (`maf-migrate-from`) + specialist agent (`@maf-cross-migration`) that scaffold a new MAF project beside the original and port it construct-by-construct, the mapping guide resource + `maf-from-semantic-kernel` skill, and a `samples/sk-sample/` fixture. Hardened by a 24-issue multi-agent review, a 7-principle architecture/quality assessment, and an independent adversarial verification pass — every fix verified, no regressions. **967 tests green across net8/9/10.**

### Added
- **Cross-framework migration: Semantic Kernel → MAF (Phase 1 + the orchestration prompt).** A new assistant for porting an existing Semantic Kernel codebase *to* Microsoft Agent Framework (vs. the existing MAF *version* upgrade path):
  - **`MafDetectSourceFramework`** tool + **`maf-doctor migrate-scan`** CLI — inventories SK usage (csproj package refs + Roslyn construct scan) and tags each construct with its migration **strategy**: 🌉 *bridgeable* (reuse via the `KernelFunction.AsAIFunction()` / `IChatCompletionService.AsChatClient()` interop shims), 🔁 *rewrite* (mechanical SK→MAF API swap), or 🏗 *re-architect* (group chat / planners / prompt templates / vector stores) — plus an EASY/MEDIUM/HARD verdict. `--json` for automation.
  - **`maf-migrate-from`** prompt — the conductor: detect → summarize intent → write `migration-plan.md` → **scaffold a new MAF project beside the original** (non-destructive, side-by-side) → port construct-by-construct with `dotnet build` gates → validate with `MafDoctor`. `@maf` routes "migrate my Semantic Kernel app to MAF" here.
  - **`maf://migrate-from?source=semantic-kernel`** resource — the construct-by-construct SK→MAF mapping guide (stored from the official Microsoft Learn page with permission + attribution; enriched with the verified interop bridges and the constructs the official page omits — prompt templates, filters→middleware, structured output, `ChatHistory` providers, vector stores, observability). Detector coverage spans all of these. (Prompts 9 → 10.)
  - **`@maf-cross-migration` specialist agent** + **`maf-from-semantic-kernel` skill** (served at `maf://skills?name=maf-from-semantic-kernel`) — the Mode-2 (no-MCP) surface for the same flow: an @-mention persona and an auto-loadable navigator over the SK→MAF construct map. The four steering sidecars (Copilot / Claude / AGENTS / Cursor) now route "migrate my Semantic Kernel app to MAF." (Tools 26 → 27, resources 6 → 7, skills 13 → 14, agents 7 → 8.)
  - **`samples/sk-sample/`** — a scan-only SK fixture (csproj + source spanning all three strategies) so `migrate-scan` can be demoed against it and pinned by a smoke test. (Read by the detector as text/XML — not compiled.)

### Fixed
- **Architecture/quality assessment hardening pass (multi-agent scorecard across 7 principles, every score adversarially calibrated) — closed the top cluster.** **DRY:** the `{semantic-kernel, sk}` allow-list, previously re-rolled in three surfaces with a subtly divergent empty-string behavior, is now a single `MigrationSources` authority consumed by the CLI, the `maf://migrate-from` resource, and the `maf-migrate-from` prompt (AutoGen becomes a one-line edit). **Drift net:** the reflection-based catalogue guards now extend to the `@`-agents (`.github/agents/*.agent.md` ↔ `AgentCatalogue`) and the skills (`.github/skills/*/SKILL.md` ↔ `AllowedSkillNames`) — the hand-maintained 14-name skill `[InlineData]` list was replaced by a folder-derived source of truth. **Detector robustness:** the `AddOpenTelemetry` / `Invoke*Async` receiver checks now match the chain's whole-word **root identifier** (killing the `kernelMetricsServices.AddOpenTelemetry()` false positive and the `kernelAgent.InvokeAsync` mis-split) instead of a substring; a `*AgentThread` suffix fallback catches concrete thread subtypes like `ChatHistoryAgentThread` (used in the sample) even with `var`; `nameof(SkType)` is no longer counted as a type usage; `Scan()`'s per-file read is `try`-guarded so a locked/deleted file can't abort the whole scan; and the verdict's occurrence threshold is now a named `LargeFootprintThreshold` constant. **Tests:** added coverage for the public `MafDetectSourceFramework` entry point (markdown default, JSON shape + count reconciliation, error shape), `VersionOverride`/`(unpinned)` package branches, the `>8`-occurrence MEDIUM branch, and tightened the sample smoke test to exact strategy counts. A follow-up independent adversarial verification pass (5 areas, every fix re-checked against the compiled assembly + the drift tests proven to fail on divergence) confirmed no regressions and closed the residual seams it surfaced: the prompt source-gate and resource empty/null throw are now tested, the `GetSkill` description literal is pinned to the allowlist, the `*AgentThread` accepted-FP boundary has a documenting test, and `this.kernel.InvokeAsync` now resolves correctly. **967 green across net8/9/10.**
- **In-depth review of the SK→MAF implementation (multi-agent, 5 dimensions, every finding verified) — fixed 24 confirmed issues + the alignment gaps.** Detector correctness (each with a regression test): **Central Package Management** SK packages no longer report "(unpinned)" — versions resolve from `Directory.Packages.props` (`VersionOverride` honored); the `*Planner` **suffix heuristic** that over-matched user types like `CapacityPlanner` is replaced by an explicit SK planner allow-list; added detection for **threads** (`AgentThread` / `OpenAIAssistantAgentThread` / `AzureAIAgentThread` / `OpenAIResponseAgentThread` → sessions), **execution settings** (`*PromptExecutionSettings` / `AgentInvokeOptions` → run options, incl. structured-output `responseFormat`); **`AddOpenTelemetry`** narrowed to a kernel-builder receiver (no longer flags ASP.NET `services.AddOpenTelemetry()`); **`Invoke*Async`** split into agent-invocation vs `kernel.InvokeAsync(fn)` (different MAF targets); type classification now gated to **type-position** nodes so a property *named* like an SK type (`new X { Kernel = … }`, `agent.Kernel`) no longer inflates occurrence counts; and the **verdict** softened — a lone re-architecture is MEDIUM, HARD now requires ≥2 distinct re-architect kinds. CLI: extracted a unit-tested **`MigrateScanCli.Parse`** that fixes `migrate-scan --source <v> <path>` (the `--source` value was being eaten as the path) and the silently-ignored `--source=<v>` equals form (unknown sources now exit 2). Guide: corrected an invented SK API (`Kernel.CreateBuilder().AddOpenAIChatCompletion(...)`), reconciled the detector↔guide "Detected?" column (the two pattern-only rows are marked **⚠ no type signature**), and standardized on `AsAIAgent`. Prompt: gates unsupported sources with a STOP body, makes the scaffold order explicit (mkdir + csproj before `MafNewAgent`), and advertises the `sk` alias. +24 tests; 960 green across net8/9/10.

## [1.4.0] - 2026-06-24

Diagnose → triage → fix, end to end: human-readable `autofix-all`, a first-class finding **confidence** signal, the **`maf-remediate`** fix-it-all conductor (+ playbook skill), the **`doctor --plan --json`** manifest, and a false-positive hardening sweep across the detectors. Hardened by two multi-agent review passes (every finding verified) that also caught and fixed 3 real bugs. 922 tests green across net8/9/10.

### Added
- **`doctor --plan --json` — structured remediation manifest.** The machine-readable sibling of `--plan`: a phased plan (Phase 1 = one `autofix-all` command + the rules it clears; Phase 2 = semantic findings grouped by rule) where every finding carries `confidence` + a `verify_first` flag and all its `occurrences`. Built so the `maf-remediate` loop can iterate deterministically and triage false positives without parsing markdown. `--plan` alone stays the human checklist; `--json` alone stays the flat findings array. (Schema v1; see docs/output-schemas.md.)
- **Finding `confidence` for false-positive triage.** Every `doctor` finding now carries a `confidence` — `certain` (compiler ground-truth) / `high` (structural AST, low FP) / `heuristic` (name-only / text / scope-limited — verify before fixing), derived from the detector audit. Surfaced in the markdown report (heuristic non-auto findings tagged **"⚠ heuristic — verify it's real first"**), in `--plan` (a heuristic count + per-item marker), and in `--json` (additive `confidence` field, schema stays v1). Unknown rules default to `heuristic` (verify-first).
- **`maf-remediate` prompt — the "fix it all" conductor.** Drives the full loop end-to-end: `MafDoctor(plan)` → `MafAutoFixAll` (mechanical) → for each semantic finding, **triage by confidence — confirm every `heuristic` finding with `MafExplainFinding` + the code before editing** → `dotnet build` → re-grade until the grade stops improving, then report fixed vs **skipped-as-false-positive**. Composes existing tools; no own LLM budget. `safe` (default) / `thorough` modes.
- **`maf-remediation-playbook` skill** (served at `maf://skills?name=maf-remediation-playbook`) — the per-rule canonical fix + how to recognize a false positive, keyed by confidence tier. Backs the `maf-remediate` prompt. (Bundled skills 12 → 13; prompts 8 → 9.)
- **`@maf` primary agent** now routes "fix everything / fix all the issues" to `maf-remediate`, distinguishes diagnose (`MafDoctor`) vs mechanical (`autofix-all`) vs full-loop (`maf-remediate`), and has a standing rule that `heuristic` findings may be false positives — never recommend a blind "fix all."

### Fixed
- **Final multi-agent review pass (6 dimensions, every finding verified) — fixed 3 real bugs + polished the edges.** Bugs: (1) the `#if`-guard suppression spanned into the `#else`/`#elif` arm, so an `EnableSensitiveData = true` (SEC-003) or DevUI reference (DEVUI-001) in the *production* `#else` branch was silently un-flagged — a security false negative; the guard now ends each branch at the next directive and only suppresses the matching arm. (2) DEVUI-001 stopped catching a fully-qualified DevUI reference in *expression* position (a static call like `Microsoft.Agents.AI.DevUI.X.Run()`) after the bare-identifier arm was dropped — restored via a member-access arm. (3) COST-001's non-agent denylist excluded the generic names `workflow`/`Workflow`, dropping a real uncapped **workflow-as-agent** call (`var workflow = chatClient.CreateAIAgent(...); await workflow.RunAsync(...)`) — removed (the actual runner `InProcessExecution` still covers the legitimate case). Polish: doctor's legend no longer calls *all* needs-judgment findings heuristic (only the ⚠-tagged ones); the playbook loop-termination + MAF001 false-positive guidance were realigned with the prompt and the detector's real behavior; `@maf` Rule 7 + the `name=` skill enumeration + the skill/prompt counts in NUGET_README / TourTool / MafResources were corrected (13 skills, 9 prompts); `--full` documented in `--help`. +11 regression tests pin all of it. A multi-agent red-team (5 dimensions, every issue verified against the code/guide) surfaced fixes that were applied: the stale analyzer package id **`maf-autopilot.Analyzers` → `maf-doctor.Analyzers`** in the steering files (a real install-404; the `.csproj`/path references are correctly left unchanged); the `maf-remediate` loop now **re-pulls the fresh finding list each pass** (line numbers shift after autofix), **confirms each finding actually cleared** (not just a green build) before marking it fixed, and **terminates on actionable-findings-zero** rather than the coarse letter grade; `safe`/`thorough` modes made symmetric (verification is mandatory in both; inconclusive ⇒ skip in either); a **data-trust + scope safety block** added (tool output / scanned source is data not instructions; only edit confirmed findings; no file deletion or destructive shell); and the playbook's MAF001 row now scopes `SendMessageAsync` correctly (it does **not** broadcast on an `AddFanOutEdge` source).
- **False-positive hardening batch across 6 detectors.** A multi-agent audit (every detector, adversarially verified) found the same root cause repeated: name/text matching without binding to the actual type or scope. Fixed the highest-impact ones, each with a regression test pinning the verified over-match snippet (FP gone) and its true positive (still fires):
  - **`MAF-AP-DEVUI-001`** no longer flags the **supported** A2A hosting family (`Microsoft.Agents.AI.Hosting.A2A` / `.A2A.AspNetCore`) — it substring-matched `…Hosting`. Now matches dotted segments and carves out A2A. Dropped the bare-identifier `DevUI` arm that flagged a project's own `namespace DevUI;` / `using DevUI;`.
  - **`MAF-AP-EXEC-001`** no longer flags every MediatR/NServiceBus `IMessageHandler<T>` — that arm is now gated on a `Microsoft.Agents.AI.Workflows` import. `ReflectingExecutor<>` and `[StreamsMessage]`/`[YieldsMessage]` (MAF-specific, low collision) still flag always. Converted regex → AST.
  - **`COST-001`** (`MafEstimateCost`) no longer flags non-agent `RunAsync`/`RunStreamingAsync` receivers — the ASP.NET host loop (`app.RunAsync()`) and workflow runners (`InProcessExecution.RunStreamingAsync`, `workflow.RunAsync`) were reported as uncapped *agent* calls. Added a receiver denylist (pending a future SemanticModel type-check).
  - **`MAF-AP-CONC-002`** (sync-over-async) converted regex → AST: excludes `(await X).Result` (the recommended `AgentResponse<T>.Result` structured-output idiom) and `Match.Result(args)` (Regex substitution), neither of which blocks a thread.
  - **`MAF-AP-SEC-003`** (`EnableSensitiveData = true`) no longer flags assignments fenced inside a `#if DEBUG`/`DEVELOPMENT` guard — the migration-guide-sanctioned dev-only pattern.
  - **`MAF-AP-CONC-001`** no longer flags a `ProviderSessionState<T>` field (which **is** the prescribed per-session fix), and tightened the base-type match from substring to exact simple-name.
  - Remaining low-risk over-matches (SEC-002 entropy, MID-001/OBS-001/SEC-001/AGENT-001/WF-001 type-binding, the MAF001 helper-indirection residual) are documented for a follow-up semantic-model pass.
- **Fan-out / `MAF001` starvation detection no longer floods false positives.** The detector classified `[MessageHandler]` methods by **return type only** — so *every* handler returning `void` / non-generic `Task` / `ValueTask` was reported as a "fan-out starvation risk," even when it correctly emits downstream via `await context.SendMessageAsync(...)` / `YieldOutputAsync` / `AddEventAsync` (the idiomatic pattern in sequential / group-chat / handoff workflows). It's now **body-aware**: a handler that emits via the workflow context is treated as **OK**; only a handler that returns nothing *and* emits nothing is flagged as a genuine silent dead-end. The narrower fan-out-*edge* rule (an `AddFanOutEdge` source must return `ValueTask<T>`, since `SendMessageAsync` doesn't broadcast there) is topology-specific and remains handled by the cross-file `MafSimulateWorkflow`. This propagates to the `doctor` grade, SARIF, PR-audit, and risk-score, which all consumed the same verdict. The `MAF001` finding's **Why/Fix** text was corrected to match (and notes these findings remain **needs-your-judgment**, not auto-fixable: changing `ValueTask` → `ValueTask<T>` requires choosing the message type and adding a `return`, which a deterministic rewriter can't synthesize).

### Changed
- **`autofix-all` (CLI) now prints a human-readable summary by default** instead of raw JSON. It shows a per-rule breakdown, the changed-file list, and a clear **next step**. The machine-readable JSON (the MCP-tool shape) is still available via the new **`--json`** flag, so scripts/CI are unaffected. The `MafAutoFixAll` MCP tool is unchanged — it still returns JSON to its clients.
  - The **0-files-changed** output is now self-explanatory (this was the most-misread result): it states that 0 ≠ clean, lists the 5 mechanical rules it *can* fix, and routes you to `maf-doctor doctor .` for the full grade and to the `@maf-migration` agent for the semantic findings a deterministic rewriter can't touch.
- **`doctor`** now opens with a one-line note that it's a **read-only diagnosis** — it grades and lists findings but never edits files; the fix path is `autofix-all` (mechanical) + the `@maf-migration` agent (semantic). Clears up the common "doctor didn't fix anything" first-run confusion.
- **Clearer "what to do next" guidance** in both commands. `autofix-all` now states plainly that it applies **deterministic, mechanical fixes only** (all a Roslyn rewriter can safely do) and tells you to get the rest via an LLM — `maf-doctor doctor . --plan`, or in an MCP client ask _"make me a plan to fix these issues."_ `doctor` gained a **tag legend**: _auto-fixable_ = a Roslyn rewriter exists; _needs your judgment_ = no mechanical fix (depends on intent) **and heuristic, so possibly a false positive — verify before changing** — plus the same plan/LLM guidance.

### Docs
- **Upgrade path + the "file is locked" gotcha.** README and quickstart now document `dotnet tool update --global maf-doctor` and the common failure where the editor's running MCP server locks the tool binary — with the fix (`Get-Process maf-doctor | Stop-Process -Force` / `pkill -f maf-doctor`, or close-editor → update → reopen, then re-`init`).
- **Natural-language "fix it all" is now wired into the steering.** The Copilot / Claude / Cursor / AGENTS steering sidecars route "fix all the issues maf-doctor finds" to the `maf-remediate` loop, call out the per-finding `confidence` (heuristic ⇒ confirm before editing), and point at the `maf-remediation-playbook` skill — so the analysis → confidence triage → fix-it-all plan flow works from plain language, not just the explicit prompt. Quickstart Beat 4 gained a matching callout.
- README now leads with a **"New in 1.3.0"** highlight (Tiers 1–3 + the compile-verified auto-fixer).
- Quickstart: added an **"explain a finding"** (`MafExplainFinding`) snippet for discoverability, and corrected the numbers — the sample reports **7** anti-pattern errors (was "ten"), and deterministic `autofix-all` takes it **F (7 → 4 errors)**, not "F → A" (Grade A is the `@maf-migration` agent phase, not the deterministic fixer).
- Quickstart Beat 4 documents the human-readable `autofix-all` default + the `--json` opt-in.

## [1.3.0] - 2026-06-18

Doctor-suggestions overhaul — three tiers that turn each finding from a terse label into something a developer can act on in one read. Each tier shipped after two adversarial multi-agent review passes.

### Added

- **Richer, actionable suggestions (Tier 1).** Every finding now carries a one-line **Why** (the concrete failure mode) and surfaces its **Fix** + an "auto-fixable / needs your judgment" tag + a "N of M auto-fixable → `autofix-all`" hint directly in the human markdown report (previously the fix string was JSON-only). The report shows the **offending source line** for each finding, with content-aware secret redaction (a line matching the hard-coded-key pattern is never echoed). `doctor --all` now **groups findings by rule** (rule-generic header, ×N, severity emoji, ordered by impact). New CLI `--json` flag (JSON was MCP-only). The JSON finding gains an additive `why` field (schema_version stays `"1"`).
- **`MafExplainFinding` tool + `maf-explain-finding` prompt (Tier 2).** Deep-dive a single finding: given its `file:line` (as printed by the report), the tool returns the offending code in context plus the rule's why / fix / auto-fixability and grounding pointers — the substrate a host model (Copilot / Claude / Cursor) uses to explain and propose the fix without spending our own LLM budget. The prompt drives that flow grounded in `maf://constraints` + `maf://guide`.
- **`doctor --plan` / `MafDoctor(format: "plan")` (Tier 3).** Turns the findings into an ordered, checkboxed **remediation plan** for a GitHub issue: Phase 1 batches every auto-fixable finding into one `autofix-all` step; Phase 2 lists the semantic fixes as impact-ordered `- [ ]` tasks (why / fix / file:line, tagged `@maf-migration`). Covers every finding regardless of `--all`; emits no source snippets, so it never echoes a secret.
- **NuGet README explains the install.** A dedicated package README spells out that **`init` is non-destructive — it attaches and updates only, never overwriting your files** (adds its own MCP server entry + regenerates self-contained steering sidecars), lists what you get (MCP server / CLI / plugin / analyzers), and highlights recent features. The same non-destructive framing was added to the main README's Quick Start.

### Fixed

- **NuGet package page no longer shows raw HTML.** The shared README's centered hero (`<p align="center">…<br/><em>…</em></p>`) rendered as literal HTML text on nuget.org (its renderer escapes HTML it doesn't fully support). Both packages now ship a pure-markdown `NUGET_README.md` as their `PackageReadmeFile`; the GitHub README keeps its centered hero.
- **Fan-out findings report the signature line**, not the `[MessageHandler]` attribute line above it — so the offending return type lands on the reported line and in the snippet.
- **`MafDoctor` markdown report no longer dead-ends.** The "Want more" footer advertised MCP-only tool calls to CLI users; it now lists surface-neutral commands (`--all` / `--json` / `--plan` for the CLI, `full:`/`format:` for MCP) and points at `MafExplainFinding`.

## [1.2.11] - 2026-06-18

### Added

- **`doctor --all` / `--full` lists every finding.** The health report ranks all findings by impact but only printed the top 3, with the rest invisible. The new flag prints every finding (anti-pattern, fan-out, prompt-lint, cost) — one line each, ordered by impact — so you can see the full backlog in one pass. Default output is unchanged (top 3) and now ends with a "…and N more. Run with `--all`" hint when findings are hidden. The MCP `MafDoctor` tool gains a matching `full` parameter (`MafDoctor(full: true)`), and the JSON format includes the full `findings` array when `--all` is set.

## [1.2.10] - 2026-06-18

### Fixed

- **Flaky-test class hardened.** Every NonBacktracking regex carried a 100ms `MatchTimeout` that intermittently tripped on the engine's one-time DFA-build cost under CI load (`RegexMatchTimeoutException` — surfaced as `LlmFencingTests.Fence_StripsHtmlComments_Multiple`). Raised to **2s** across all ~17 sites. NonBacktracking is linear and inputs are length-bounded, so the timeout is only a cold-start backstop, not the primary ReDoS defense — raising it doesn't weaken safety.
- **Demo sample builds again.** `samples/maf-1.3-sample` pinned `maf-doctor.Analyzers` at `1.3.0-alpha-1` (never published — the rename left a dead version pin), so `dotnet restore` failed. Repinned to `1.2.9`.

### Added

- **README hero cast.** Added the truthful `install-cast` GIF (install → `maf-doctor doctor .` → grade F + the top fixes) below the intro, via absolute raw URL so it renders on GitHub and nuget.org.

## [1.2.9] - 2026-06-16

### Changed (branding)

- **README logo → white-background `MAFDoctorLogo.png`.** The transparent logo's dark artwork disappeared on dark backgrounds (GitHub dark theme); the white-background version renders consistently everywhere. Republished so the nuget.org package page (README is baked per-version) gets the fix too.

## [1.2.8] - 2026-06-16

### Added (branding)

- **README now leads with the MAF Doctor logo** (`assets/MAFDoctorLogoTransparent.png`, referenced by absolute raw URL so it renders on both GitHub and the nuget.org package page).
- **Both NuGet packages ship a package icon** (`MAFDoctorIcon.png`) — `maf-doctor` and `maf-doctor.Analyzers` now show the brand icon on nuget.org.
- **The MCP server advertises its icon** via the 2025-11 spec's `icons` field on `serverInfo` (`Implementation.Icons`, supported by ModelContextProtocol 1.3.0) — clients that support server icons can render it; older clients ignore it.

## [1.2.7] - 2026-06-16

### Added

- **`maf-doctor --version` / `-v` and `--help` / `-h`.** The CLI had no version or help command, so any non-subcommand argument fell through to MCP-server mode — meaning `maf-doctor --version` (which the quickstart told users to run) just hung on stdio. Both now print and exit cleanly.

### Fixed

- **Claude Code couldn't see the MCP server that `init` wired.** The `.mcp.json` (Claude Code) entry carried a `"type": "stdio"` field that some Claude Code builds don't recognize. Dropped `type` from the Claude entry — stdio is the implied default — while VS Code's `.vscode/mcp.json` keeps it. `init` now emits a `.mcp.json` Claude Code reads cleanly.
- Added a project-root `.mcp.json` to **this** repo (it previously had only the VS-Code-format `.vscode/mcp.json`), so Claude Code picks up maf-doctor when developing the toolkit itself.

## [1.2.6] - 2026-06-14

### Fixed (stop pinning MAF 1.3.0 in served text)

- **Removed hardcoded `MAF 1.3.0` version pins from the tool's user-facing output** — MCP prompt bodies, resource titles/descriptions, tool descriptions, and scaffolder comments said things like "Target MAF version: 1.3.0", "MAF 1.3.0 Hard Constraints", "scaffold MAF 1.3.0 boilerplate". Reworded to version-agnostic phrasing ("MAF", "the latest stable release") so they don't go stale as MAF ships. Audited all 19 served-source files (75 references); fixed 37 display/command pins, deliberately kept the ~38 that are historical facts ("removed in 1.3.0"), per-version data (the compatibility matrix's `1.3.0` row), registry version keys, or user-supplied version-argument examples (diff/migration-path tools).
- **`maf-doctor new agent` no longer prints stale install commands.** `NewCommand` told users to run `dotnet add package Microsoft.Agents.AI --version 1.3.0` (and the Workflows/Generators packages) — pinning a years-old MAF. Dropped the `--version 1.3.0` so NuGet installs the latest stable.
- All 749 + 15 tests pass across net8/9/10 (no behavior change — text only).

## [1.2.5] - 2026-06-14

### Changed (`init` steering layout — no longer merges into your own files)

- **`init` now writes its steering as overwrite-on-reinit sidecars in each tool's auto-loaded convention location, instead of appending into your hand-authored files:**
  - **Copilot** → `.github/instructions/maf-doctor.instructions.md` (with `applyTo: '**'` frontmatter). `init` no longer creates or touches `.github/copilot-instructions.md`.
  - **Claude Code** → `.claude/maf-doctor.md`, imported via a single stable `@.claude/maf-doctor.md` line in `CLAUDE.md` (added once; the rest of your `CLAUDE.md` is never touched).
  - **AGENTS.md** → a sentinel-delimited managed block (`<!-- BEGIN maf-doctor -->…<!-- END maf-doctor -->`) that is replaced in place; content outside the block is preserved.
  - **Cursor** (`--with-cursor`) → `.cursor/rules/maf-doctor.mdc`.
- **Why:** the previous append-into-existing behavior froze the guidance at first-init and (before 1.2.4) could stack duplicates. Sidecars are **overwritten on every `init`**, so re-running refreshes the guidance to the installed version, never duplicates, and never edits your own files. Added sidecar/managed-block tests (16 InitCommand tests green across net8/9/10).

## [1.2.4] - 2026-06-14

### Added

- **`init` now wires the MCP server for Claude Code too.** It writes `.mcp.json` at the repo root (top-level `mcpServers`) alongside the existing `.vscode/mcp.json` (top-level `servers`) — so the `maf-doctor` tools are available natively in Claude Code, not just VS Code / Copilot. Same merge-not-overwrite + legacy-key-recognition + JSONC-safe behavior as the VS Code path. (CLAUDE.md / AGENTS.md steering were already written.)

### Fixed

- **Stale `maf-autopilot` text in the `init`-emitted steering snippets.** The embedded snippets dropped into a user's repo (CLAUDE.md, AGENTS.md, .github/copilot-instructions.md, .cursorrules) still told assistants to "defer to maf-autopilot's tools" / referenced the "maf-autopilot registry". Renamed the prose to maf-doctor (the MCP tool names like `MafDoctor` were already correct).
- **Duplicate-append bug in `init`.** The merge idempotency marker (`## Using maf-doctor`) no longer matched the copilot snippet header after the rename, so re-running `init` appended a second steering section. Headers and markers are realigned, and the marker set now also covers the `.cursorrules` snippet. Added regression + Claude-path tests.

## [1.2.3] - 2026-06-14

### Changed (docs)

- **Toned down inline-code density in the README.** NuGet.org styles inline `code` spans as red-on-pink, so the previous README (~50 inline-code spans) rendered as a wall of red on the package page (it looks fine on GitHub). Rewrote prose to use plain/bold text, keeping backticks only where monospace genuinely helps — the literal API transformations in "Why This Exists" and the one registry path a contributor edits (6 spans total). Fenced command blocks are unchanged.

## [1.2.2] - 2026-06-14

### Fixed (NuGet package metadata)

- **Corrected the published package descriptions.** Both `maf-doctor` and `maf-doctor.Analyzers` still carried the pre-rename blurb on nuget.org ("MAF autopilot MCP server … MAF 1.3.0 migrations" / "Pairs with the maf-autopilot MCP server"). They now describe MAF Doctor accurately and version-agnostically.
- **Removed the README "Naming" note.** It explained the rename/deprecation of a package that was never publicly announced — pointless noise for a new reader. The intro now states plainly that `maf-doctor` is a **.NET global tool installed from NuGet** (`dotnet tool install -g maf-doctor`), with `maf-doctor.Analyzers` as a separate analyzer package.

## [1.2.1] - 2026-06-14

**First release published under the `maf-doctor` package id** (and `maf-doctor.Analyzers`) — this is the version where the rename below actually reaches NuGet.

### Changed (BREAKING — full rename to `maf-doctor`)

- **The NuGet package + CLI are renamed `maf-autopilot` → `maf-doctor`** (and `maf-autopilot.Analyzers` → `maf-doctor.Analyzers`). The brand, repo, MCP server id, and C# namespace were already MAF Doctor; this completes the rename so the package/command match. Done pre-public-announcement, so the break is acceptable.
  - **For users:** `dotnet tool uninstall -g maf-autopilot && dotnet tool install -g maf-doctor`; the command is now `maf-doctor …`. Re-run `maf-doctor init` (it recognizes a legacy `maf-autopilot` mcp.json server entry and won't duplicate). The Docker image runs `maf-doctor.dll`.
  - The old `maf-autopilot` package (≤ 1.2.0) will be **deprecated on nuget.org** with a pointer to `maf-doctor`.
  - The internal `src/maf-autopilot/` project folder + `.csproj`/`.sln` filenames are unchanged (invisible to users; avoids high-churn path rewrites).

### Docs

- **README leaned to the essentials** — removed the Project Structure tree, the Skill Orchestration catalog, and the dated distribution block; deduplicated the `dotnet-inspect` notes; trimmed the companion-MCP recommendation to **Microsoft Learn MCP** (we don't wire Context7/DeepWiki/Serena); added an **"It updates itself"** section explaining how and when the self-update watcher runs (Thursday 06:00 UTC; minors auto-commit, majors escalate); and dropped hardcoded version pins so the docs stop going stale as MAF ships.
- Fixed stale tool/resource/prompt counts and the `maf-autopilot.Analyzers` id in `docs/architecture.md`, and a stale "current release `1.3.0-alpha-5`" claim in `docs/setup.md`. The unrendered demo-cast `<img>` tags were removed (the `.tape` sources + `scripts/make-cast` remain — re-add once rendered).

## [1.2.0] - 2026-06-14

### Added

- **MAF 1.10.0 support** — 22 registry entries (obsolete/changed APIs), the compatibility-matrix row (`Microsoft.Extensions.AI >= 10.6.0`), and the `1.6.1 → 1.10.0` migration guide. Auto-detected by the now-fixed release watcher; migration content authored by the Copilot coding agent and verified by the registry gates. _(AI-authored migration guidance — review against the upstream `microsoft/agent-framework` changelog when convenient.)_

### Fixed (self-update reliability)

The scheduled **MAF Release Watcher** and **MAF Drift Detector** had failed on
**every** scheduled run since creation, for two unrelated reasons:
- **Watcher** — the NuGet version filter was an inline `python3 -c` whose code
  inherited the shell block's indentation → `IndentationError` on every
  scheduled run (only an explicit-version manual dispatch bypassed it). Extracted
  to `.github/scripts/get_latest_stable.py` (+ unit tests).
- **Drift detector** — `gh issue create --label maf-drift` hard-failed because
  the `maf-drift` / `needs-review` labels never existed. The workflow now
  self-provisions them idempotently (`gh label create --force`).
- **Observability** — both workflows open/de-dupe a `maf-release`-labelled
  tracking issue on failure (`if: failure()`), so a scheduled failure can no
  longer go unnoticed for weeks.
- **Engine** — major MAF bumps escalate to a review issue instead of
  auto-committing to `main`; registry append is idempotent; the doctor gained an
  opt-in `doctor … --exclude <substr>` so the drift grade reflects product code
  (samples / test fixtures excluded); the two crons are staggered with
  `concurrency:` guards; the watcher polls daily (was weekly). Plus a batch of
  latent fixes across the AI-fill / verify / semantic-review chain and the
  Python helpers (prerelease-safe guide gen, union guide-section validation,
  placeholder gate, sticky verify comment, non-fatal bot-id resolution, …).
- **CI** — `ci-invariants` gained a workflow-lint + helper-test gate
  (actionlint + `compileall` + `pytest`) so this class of breakage is caught
  pre-merge.

### Changed (brand — MAF Doctor)

- The MCP **server id** is now `maf-doctor` (the brand) in `.vscode/mcp.json`,
  the README / setup / workshop config snippets, and what `maf-autopilot init`
  writes into a user's `.vscode/mcp.json`. The **`command` stays `maf-autopilot`**
  (the frozen global-tool binary), so existing entries keep launching and
  `dotnet tool install --global maf-autopilot` is unchanged. `init` recognizes
  both the new `maf-doctor` and the legacy `maf-autopilot` server keys, so
  re-running it never creates a duplicate.
- **Maintainer follow-ups (post-merge, not in this PR):** rename the GitHub repo
  `joslat/maf-autopilot` → `joslat/maf-doctor` (GitHub auto-redirects old URLs),
  then flip the remaining `joslat/maf-autopilot` URL / `RepositoryUrl` / SARIF /
  OCI-label references to `joslat/maf-doctor` (deferred here to avoid a 404
  window). The GHCR image path follows the repo rename automatically; **new tags
  publish under `ghcr.io/joslat/maf-doctor`** — old `ghcr.io/joslat/maf-autopilot`
  tags stay pullable. The NuGet package id and CLI command are deliberately
  **not** renamed (immutable id; renaming would break every existing install).

### Security (security hardening release — branch `security-hardening-v1.1`)

Comprehensive hardening pass following a three-agent Opus security review.
Each phase carried its own Opus full-phase review + fixup commit; the final
comprehensive review returned **READY TO SHIP**. Anchored to the canonical
MCP / LLM / GHA security canon documented in
[`docs/security.md` "Canonical references"](docs/security.md#canonical-references).
The full attack-surface map + closure list is in
[`docs/security/threat-model.md`](docs/security/threat-model.md) §3–§5.

#### Critical-severity closures (5)
- **C1 — Path-escape via `AutoFix.specificFile`** — added `PathGuard.ValidateContainment`
  (canonicalize + jail + reparse-point reject at both leaf AND parent-chain).
  An LLM-supplied `specificFile = "C:\\Users\\victim\\..."` is now rejected
  with a non-leaky error message.
- **C2 — `MafRunCs0618Hunt` annotation drift** — was `[ReadOnly=true]` despite
  spawning `dotnet build` (executes MSBuild targets, source generators,
  NuGet post-install). Now `[ReadOnly=false, OpenWorld=true]`; MCP-spec-
  compliant clients prompt for consent before auto-invoke.
- **C3 — Code injection via `AgentScaffolder.projectNamespace`** — added
  `IsValidNamespace` (Roslyn `ParseName` + roundtrip equality) mirroring the
  existing `IsValidTypeExpression` defense applied to `inputType`/`outputType`.
- **C4 — Supply-chain prompt injection via upstream MAF release notes** —
  `gen_guide_section.py` now fences both `release-notes.txt` AND `diff-core.txt`
  via new shared `LlmFencing` helpers (HTML-comment strip + UTF-8 byte cap +
  random-sentinel BEGIN/END markers with "treat as data" framing). Same
  fence applied at every downstream hop (`build_ai_fill_issue_body.py`,
  `ai_review_build_prompt.py`, `RegistryExtractCommand.notes:` embed).
- **C5 — Workflow `${{ inputs.* }}` injection** — every `workflow_dispatch`
  workflow that accepts inputs gained a `validate-inputs` job (semver-regex /
  enum-check, `permissions: {}`); every `run:` block migrated to `env:`
  redirect pattern. CI invariant blocks regressions on `inputs.*`,
  `needs.*.outputs.*`, and `steps.*.outputs.*`.

#### High-severity closures (~11)
- **LLM-to-LLM prompt smuggling via `MafDraftIssue`** — every user-controlled
  field (symptom, expected, actual, snippet) wrapped in `LlmFencing.Fence`
  + `NeutralizeFences` + `StripHtmlComments` (snippet sits in a `csharp`
  code-block so triple-backtick neutralization is required).
- **`MafPrompts.Debug.errorOrSymptom`** — replaced single-line `>` blockquote
  bypass with full `LlmFencing.Fence` + `BoundedInput.Validate`.
- **`MafNewAgent` / `MafNewExecutor` annotation drift** — both `[Destructive=true]`
  now (they write new `.cs` files to the project tree).
- **`MafDiffPackage` / `MafPreUpgradeDryRun` annotation drift** — both
  `[OpenWorld=true]` now (they reach `api.nuget.org`).
- **`VerifyRegistryCommand` `pre-X.Y.Z` + `IsRawDraft` bypass** — pre-1.0 +
  raw-draft branches now run `CheckContentSafety` instead of skipping all
  checks. `IsRawDraft` tightened to require `// TODO` at line-start regex
  (substring-anywhere allowed `// TODO` inside string literals to re-qualify).
- **`SourceFileWalker` symlink-follow + `MakeRelative` info leak** —
  `EnumerationOptions.AttributesToSkip = ReparsePoint | Hidden | System`;
  `MakeRelative` throws on out-of-root instead of leaking the absolute path.
- **`MAF_REGISTRY_PATH` unguarded** — `ValidateOverridePath` rejects shell
  metachars + NUL/CR/LF + reparse-points; optional `MAF_REGISTRY_PATH_ROOTS`
  env-var allowlist.
- **`RegistryExtractCommand` persistence injection** — `notes:` field
  attacker-controllable via `dotnet-inspect diff` output is now fenced
  (8 KB cap because the value persists forever in `registry.yaml`).
- **`ai_review_build_prompt.py` PR-registry injection** — each changed
  registry entry now fenced before embedding in the GPT-4o review prompt.
- **`ExecutorSealedRewriter` produces uncompilable `abstract sealed class`** —
  abstract / static Executors are left UNCHANGED (parity with the WF-001 scanner,
  which skips abstract Executors), so `MafAutoFix` / `MafBeforeAfter` honestly
  report "no change" instead of dirtying a scanner-clean file with an advisory
  comment.
- **`SyncOverAsyncRewriter` produces uncompilable `await`** — lock-block /
  non-async-method / accessor / non-async-lambda guards. Same idempotence pattern.

#### G-tier gaps closed (added during the audit)
- **`.github/CODEOWNERS`** — `@joslat` required on security-critical paths
  (`src/maf-autopilot/Tools/`, `Scaffolding/`, `Commands/`, `Prompts/`,
  `Resources/`, `Data/`, `Analyzers/`, `.github/workflows/`, `.github/scripts/`,
  `docs/security*`, `private/`, root metadata like `Dockerfile` and
  `Directory.Build.props`).
- **Root `SECURITY.md`** — GitHub Security tab entry point; 72-hour
  acknowledgement + patch-release SLA + scope + recognition policy.
- **`.github/workflows/mcp-scanner.yml`** — Cisco mcp-scanner runs on every
  PR against `main` touching `src/maf-autopilot/**`, plus weekly Monday cron.
  Sticky PR comment with results. Advisory for the first 5 consecutive
  clean runs, then promote to hard-fail.
- **`maf-pr-audit.yml` compiler-bomb cap** — skip Roslyn-heavy audit when
  PR changes > 200 `.cs` files OR > 5 MB aggregate; `git diff -z` +
  `xargs -0` + `stat -c %s --` for filename-safety.

#### Defense-in-depth additions
- **`BoundedInput.Validate`** — single helper, UTF-8 byte count, six named
  cap classes (`PathBytes` / `IdentifierBytes` / `ShortTextBytes` /
  `SnippetBytes` / `InstructionsBytes` / `AggregateBytes`). Applied across
  every MCP tool's user-controlled string parameter.
- **`LlmFencing.Fence` + `LlmFencing.StripHtmlComments` (C# + Python parity)** —
  the reusable primitive for "user content destined for another LLM."
- **Annotation reflection tests (`McpToolAnnotationTests`)** — locks the
  `[McpServerTool]` contract for every tool whose annotation gates MCP-client
  auto-invocation behavior.
- **Rewriter idempotence property tests (`RewriterIdempotenceTests`)** —
  pass-1 must equal pass-2 for every rewriter; catches a regression class
  that would otherwise only surface in production.
- **`SensitiveDataAnalyzer` dictionary-form parity** — MAF003 now catches
  `["EnableSensitiveData"] = true` in addition to the bare-identifier and
  member-access forms; rewriter extended to remove the standalone statement
  shapes too.
- **Regex hygiene** — every `new Regex(` in `src/maf-autopilot/` carries
  `NonBacktracking + MatchTimeout = 100ms` (and the CI invariant now
  catches the target-typed `Regex X = new(...)` form too).
- **SHA-pinning** — 9 third-party actions across 23 call sites pinned to
  full SHAs with a trailing-comment specific tag; Dependabot's
  `github-actions` ecosystem updates both in lockstep.

#### CI invariants (`.github/workflows/ci-invariants.yml`)
Five hard-fail jobs that lock the invariants going forward:
1. `Process.Start` allowlist + unsafe `Arguments` string property block.
2. Regex hygiene (`NonBacktracking + MatchTimeout` required on every `new Regex(`).
3. `EnumerationOptions` on recursive `Directory.EnumerateFiles` walkers.
4. `${{ inputs.* | needs.*.outputs.* | steps.*.outputs.* }}` blocked inside `run:` blocks.
5. Explicit `permissions:` block on every workflow.

#### Documentation
- `docs/security.md` — six new "Known MCP attack classes" subsections with
  canonical-source citations + a new "Canonical references" section
  enumerating 13 sources (MCP spec, OWASP LLM / MCP / Agentic Top 10s,
  MITRE ATLAS, Cisco MCP scanner, Invariant Labs, Snyk, Keysight, GitHub
  Actions secure-use, NIST SP 800-218A, CSA mcpserver-audit).
- `docs/security/threat-model.md` — §3 expanded with 5 new attack-surface
  subsections (code injection via namespace, LLM-to-LLM smuggling,
  supply-chain PI via release notes, workflow input injection, third-party
  action supply chain); §4 split into "carried over from 1.0" + 18-item
  "Added in 1.1" list; §5 rewritten with 7 documented residual gaps each
  carrying a v1.2.x tracking note.
- `CONTRIBUTING.md` — new 7-item security rubric for MCP tool contributions
  (PathGuard containment, BoundedInput length caps, annotation correctness,
  ArgumentList discipline, LlmFencing on LLM-bound content, recursive walker
  safety, regex hygiene). CI invariants enforce most items mechanically.

### Deferred (1.2.0+)
- **`PullRequestAuditTool` inline `Process.Start`** → migrate to `ProcessRunner`
  (bypasses the 16 MB output cap today; larger refactor than v1.1 scope).
- **`ProcessRunner` streaming-cap-during-read + env scrubbing** (`MAF_FORWARD_ENV=*`
  opt-out) — both risk breaking `dotnet build` for users with custom env setups.
- **Semantic-model upgrade** for `DefaultAzureCredentialRewriter` /
  `FanInArgOrderRewriter` — pure-syntax matching can silently rewrite unrelated
  user types with name collisions. Bounded today by `--dry-run` default
  + idempotence tests.
- **Roslyn-analyzer NuGet signing posture** — undocumented today.
- **Commit-signing requirement** — maintainer choice.
- Full Roslyn data-flow `MafScanPromptInjection` (PROMPT-004 covers the syntactic case today).
- MCP sampling tools (`maf_full_audit`, `maf_migration_suggest`, `maf_open_feedback_issue`) — depend on host sampling support.
- GitHub App `@maf-bot` for org-wide PR comments — separate multi-day project.
- VS Code extension wrapping the MCP server — separate multi-day project.
- **Rung-3 of the AI trust ladder**: flip `COMMENT_ONLY_MODE = False` in `ai_review_post_comment.py` and add `MAF AI Semantic Review` as a required status check to enable AI-gated auto-merge of ai-fill PRs. Defer until 2–3 real MAF releases have validated semantic-review verdicts against manual judgment.

---

## [1.1.0] — 2026-05-18

Minor release focused on **hardening the AI-fill release-watcher pipeline end-to-end**. The watcher chain that auto-extracts breaking-change entries from new MAF NuGet releases is now production-ready, with a 3-layer trust ladder catching defects deterministically. **First MAF 1.6.1 registry data** added.

### Added

- **Rung-2 AI semantic review** (`.github/workflows/maf-ai-semantic-review.yml`) — calls GPT-4o via the **GitHub Models** inference endpoint on every AI-fill PR; per-entry PASS/WARN/FAIL JSON verdict posted as a PR review comment. $0 marginal cost (uses Copilot Pro+ subscription quota). Currently in COMMENT-ONLY mode; can be flipped to a hard gate after 2-3 release cycles of calibration.
- **Cross-file consistency check** (`.github/scripts/verify_crossfile_consistency.py`) — deterministic Python check catching defects the per-entry mechanical check misses: duplicate `**X.Y.Z**` rows in compat-matrix, stale `Current tracked version` line, stale `last-updated:` date, `guide_section` IDs that don't exist in the 1.3.0 migration guide.
- **MAF 1.6.1 registry data** — first entry (`MAF161-EXTENSIO-001` — `WorkflowEvaluationExtensions.EvaluateAsync` signature change adding `expectedOutput` parameter), accompanying migration guide, and compatibility-matrix row.
- **Hardened fill prompt template** (`.github/scripts/ai_fill_issue_prompt.md.tpl`) — restructured action-first / verify-second / detail-below. Includes a runnable bash post-implementation checklist that mirrors the CI gate exactly. 366 → 207 lines (43% smaller).

### Changed

- **Default fill bot is now Claude** (Anthropic Code Agent), previously Copilot Coding Agent. Empirically better at structured-instruction following for this task. Override via `gh workflow run maf-ai-fill-todos.yml -f bot=copilot|codex`.
- **Bot ID resolution is now dynamic** via the GraphQL `suggestedActors` query — no more hardcoded `BOT_kgDOC9w8XQ` constants that can rotate when GitHub re-provisions bot accounts.
- **Watcher pushes to main using a maintainer PAT** (`COPILOT_ASSIGN_PAT`) so it can bypass branch protection's required-status-check rule. Classic branch protection has no bypass-list (Rulesets-only feature); admin users do bypass when `enforce_admins=False`. Commit author identity unchanged — still `github-actions[bot]`.
- **`maf-ai-fill-verify.yml` always reports a status** on every PR (the previous `paths:` filter caused a branch-protection footgun where PRs not touching `registry.yaml` got blocked by missing-required-check). Out-of-scope PRs report success trivially.
- **`maf-pr-audit.yml` skips samples-only PRs** — `samples/find-the-bug/` and `samples/maf-1.2-sample/` are intentionally buggy demo corpora; auditing them produced a noisy F grade misrepresenting toolkit quality.

### Fixed

- **Bash heredoc → Python helpers** for the watcher commit message + AI-fill issue body. Eliminates a class of shell-escaping bugs (the 2026-05-17 backtick-as-command-substitution incident that aborted the watcher with exit 127).
- **`addAssigneesToAssignable` for Coding Agents** now uses a fine-grained PAT — `GITHUB_TOKEN` is categorically rejected by the Copilot Coding Agent assignment API by design (confirmed by upstream `gh-aw` issue #19765 + Copilot maintainer statement: "Copilot does not support bot accounts for issue assignment").
- **Cross-workflow dispatch** from watcher to `maf-ai-fill-todos.yml` needs `actions: write` permission (HTTP 403 "Resource not accessible by integration" without it).
- **Rung-2 trigger** dropped the `paths:` filter — `paths` matching is broken on `ready_for_review` events (state flips carry no commit deltas, so `paths` silently skips). Observed empirically on PR #26.
- 6 additional smaller fixes across the watcher / verify / semantic-review chain (see commit history).

### Dependency bumps (Dependabot)

- `Microsoft.SourceLink.GitHub` 8.0.0 → 10.0.300
- `ModelContextProtocol` 1.2.0 → 1.3.0
- `Microsoft.Extensions.Hosting` 10.0.7 → 10.0.8
- `actions/setup-dotnet` 4 → 5
- `actions/setup-python` 5 → 6
- `docker/metadata-action` 5 → 6
- `docker/build-push-action` 6 → 7
- `marocchino/sticky-pull-request-comment` (patch)

### Distribution

- 🟢 NuGet `maf-autopilot/1.1.0` (multi-targets net8.0/net9.0/net10.0)
- 🟢 NuGet `maf-autopilot.Analyzers/1.1.0` (netstandard2.0)
- 🟢 Docker GHCR `:1.1.0` `:1.1` `:1` `:latest`

---

## [1.0.1] — 2026-05-17

Same-day stabilization patch following 1.0.0. **No functional code changes.** NuGet binary content is byte-identical to 1.0.0 for both packages (`maf-autopilot` + `maf-autopilot.Analyzers`). Diff vs v1.0.0:

```
.dockerignore |  2 ++
Dockerfile    | 37 ++++++++++++++++++++++++++-----------
```

The version bump exists to attach a single tag covering 4 post-1.0.0 Docker pipeline fixes so the `docker-publish.yml` workflow rebuilds the GHCR images cleanly via tag push.

### Fixed

- **Dockerfile** — bumped SDK base image `8.0` → `10.0` (the csproj multi-targets `net8.0;net9.0;net10.0` and SDK 8.0 failed NETSDK1045). Added explicit `--framework net10.0` to `dotnet publish` (required for multi-TFM csprojs). Bumped runtime image to match.
- **Dockerfile** — added `COPY Directory.Packages.props Directory.Build.props global.json` to the build stage. The csproj uses Central Package Management; without these the restore failed NU1015 ("PackageReference items do not have a version specified").
- **Dockerfile** — replaced per-folder source COPYs with single `COPY src/maf-autopilot/` (the prior enumeration missed `Commands/` — added during 1.0.0 prep for `init`, `verify-registry`, `registry-extract`, and `badge` subcommands).
- **.dockerignore** — un-excluded `docs/steering/` (4 steering snippets are `EmbeddedResource` items in the csproj since 1.0.0 — they must be in the Docker build context).

### Distribution

- ✅ NuGet `maf-autopilot/1.0.1` republished (binary identical to 1.0.0)
- ✅ NuGet `maf-autopilot.Analyzers/1.0.1` republished (binary identical to 1.0.0)
- ✅ Docker GHCR `:1.0.1` `:1.0` `:1` `:latest` rebuilt with the 4 fixes baked in

---

## [1.0.0] — 2026-05-17

First stable release. Public API committed. **MAF Doctor** brand introduced.

### Highlights

- **25 curated MCP tools** for diagnosing, fixing, and migrating Microsoft Agent Framework code, **all annotated with MCP behavior hints** (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`) so well-behaved clients can auto-classify them
- **Curated obsolete-API registry** with `applies_to_codebases` markers — version-aware migration paths for MAF 1.0 / 1.2 / 1.3
- **Deterministic Roslyn rewriters** with `--dry-run` and `--all` orchestration modes
- **`MafDoctor`** — A-F holistic health verdict in one call; supports `format=markdown` (default) and `format=json` (7 discrete fields for CI/dashboards)
- **`MafAutoFixAll`** — dependency-ordered fix-everything pass
- **`MafRunCs0618Hunt`** — compiler ground truth + registry join
- **Roslyn analyzer NuGet** (`MAF001` / `002` / `003`) for write-time enforcement
- **AI-fill maintenance loop** — release-watcher + registry-extract + Copilot Coding Agent + verify-registry PR gate
- **Multi-version sample regression CI** — proves fixes don't break older codebases
- **12 skills + 7 specialist agents** for GitHub Copilot Coding Agent
- **7 MCP prompts** + **6 MCP resources** (constraints, registry, guide, rules, help, skills)
- **Plugin packaging via `maf-autopilot init`** — drops `.vscode/mcp.json` + 3 steering snippets (`.github/copilot-instructions.md`, `CLAUDE.md`, `AGENTS.md`) with merge-not-overwrite semantics; optional `--with-cursor` for `.cursorrules`
- Built on **dotnet-inspect v0.7.8** (which surfaces `[Obsolete]` natively); we add MAF-specific curated fix recipes + version-aware filtering on top
- **Security model documented** in `docs/security.md` (user-facing overview) + `docs/security/threat-model.md` (deep technical detail). MCP-server scanned with **Cisco mcp-scanner v4.6.0** (yara analyzer, fully local): **25/25 tools SAFE, 0 findings**.

### Versioning note for alpha users

Previous releases were tagged `1.3.0-alpha-1` through `1.3.0-alpha-3` to track the MAF target version. 1.0.0 decouples our versioning from MAF's. **maf-autopilot 1.0.0 supports MAF 1.0 / 1.2 / 1.3 simultaneously.** Existing alpha users upgrade with:

```bash
dotnet tool update -g maf-autopilot --version 1.0.0
```

### Recommended companion MCPs

For best results, pair maf-autopilot with:

- **[Microsoft Learn MCP](https://learn.microsoft.com/api/mcp)** — live MAF documentation
- **[Context7](https://github.com/upstash/context7)** — version-specific NuGet docs
- **[DeepWiki MCP](https://mcp.deepwiki.com/mcp)** — query `microsoft/agent-framework` source
- **[Serena](https://github.com/oraios/serena)** — semantic IDE-shape navigation across 40+ languages

### Breaking changes from 1.3.0-alpha-3

- Skill `fan-in-static-analyzer` removed (was a redirect; use `maf-fan-out-validator` directly).
- 5 skills renamed with `maf-` prefix for namespace clarity: `fan-out-validator` → `maf-fan-out-validator`, `migration-plan-creator` → `maf-migration-plan-creator`, `migration-retrospective` → `maf-migration-retrospective`, `obsolete-api-registry` → `maf-obsolete-api-registry`, `workflow-smoke-tester` → `maf-workflow-smoke-tester`. Skills without the prefix (`cs0618-hunter`, `dotnet-inspect`, `nuget-diff-analyzer`) are unchanged — those are generic .NET concepts, not MAF-specific.
- `MafDoctor` JSON output re-split into 7 discrete fields (`rule_id`, `severity`, `file`, `line`, `issue`, `fix_description`, `auto_fixable`) — CI dashboards can now link directly to file:line without parsing concatenated strings.
- Tool descriptions tightened (~30% rewritten for verb-first wording, when-to-use clarity, neighbor disambiguation). Existing prompt scripts referencing tools by name still work; descriptions are server-side only.
- `MafDoctor` now supports `format=json` parameter for CI-friendly structured output (default remains `markdown`).
- `dotnet-inspect` dependency bumped from `0.7.6` to `0.7.8` across all 21 pin sites (workflows, agents, skills, tools, tests, guides). v0.7.8 surfaces `[Obsolete]` natively — we add the MAF-specific curated fix recipes + version-aware filtering on top.
- **MCP tool annotations** added to all 25 [McpServerTool] decorations (readOnlyHint / destructiveHint / idempotentHint / openWorldHint). Well-behaved clients (Claude Code, VS Code Copilot) will no longer show "may modify system" confirmation prompts for read-only scanners. Classification: 18 read-only local + 3 read-only open-world (shells dotnet-inspect/build) + 2 destructive writers + 2 idempotent scaffolders. Adopts the pattern from microsoft/skills/mcp-builder.

### Documentation

- New: `AGENTS.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `LICENSE`
- New: `docs/steering/` (paste-able snippets for `.github/copilot-instructions.md`, `CLAUDE.md`, `AGENTS.md`)
- New: `docs/security/threat-model.md`
- New: `docs/output-schemas.md` (JSON schema for `MafDoctor` and `MafAuditPullRequest`)

---

## Pre-1.0 history

### [1.3.0-alpha-3] — 2026-05-12

This release captures the full toolkit after the magic-path sprint, the multi-Opus review round, and the Phase E/F polish.

### Added
- **Greenfield scaffolding** (Phase #60 / Track 1):
  - `MafNewAgent(projectPath, agentName, instructions?)` MCP tool — generates a clean `ChatClientAgent`-based class with `UseOpenTelemetry` wired, `MaxOutputTokens` cap, and a hermetic xUnit smoke test.
  - `MafNewExecutor(projectPath, executorName, inputType?, outputType?)` MCP tool — generates a `sealed partial class : Executor` with a fan-out-safe `Task<T>` handler and a reflection-based shape-verification test.
  - CLI: `maf-autopilot new agent <Name>` and `maf-autopilot new executor <Name>`.
- **Roslyn analyzer companion** (Phase #29 / Track 2): a separate `maf-autopilot.Analyzers` NuGet package ships **MAF001** (fan-out handler must return `Task<T>`/`ValueTask<T>`), **MAF002** (avoid `DefaultAzureCredential` outside test code), **MAF003** (`EnableSensitiveData = true` outside test code). Write-time IDE enforcement.
- **Workflow simulation** (Phase #65 / Track 3): `MafSimulateWorkflow(repoPath)` — static topology graph + Mermaid diagram + per-edge completion forecast. Resolves variable-bound `AddEdge(reviewer, finalizer)` patterns via local-scope type maps.
- **Code explanation** (Phase #64 / Encore): `MafExplain(snippet)` — annotated per-line breakdown of MAF API touchpoints, cross-referenced against the obsolete-API registry + guide sections.
- **Health letter** (Phase #59 / E.4): `MafDoctor(repoPath)` MCP tool + `maf-autopilot doctor .` CLI subcommand — single-command A/B/C/F grade. Aggregates 4 scanners (anti-pattern + fan-out + prompt-lint + cost).
- **PR-scoped audit** (Phase #52): `MafAuditPullRequest(repoPath, baseBranch?)` — runs every scanner only on files changed in this branch vs base.
- **Pre-upgrade dry-run** (Phase #54): `MafPreUpgradeDryRun(repoPath, packageId, currentVersion, targetVersion)` — combines `MafDiffPackage` with codebase grep to surface user-specific breaking-change impact BEFORE any code change.
- **Cost auditor** (Phase #62): `MafEstimateCost(repoPath)` — flags every `RunAsync`/`RunStreamingAsync` site missing a `MaxOutputTokens` cap; estimates input-token cost from nearest `Instructions` literal.
- **Prompt linter** (Phase #61): `MafLintAgentPrompt(repoPath)` — 4 rules covering empty `Instructions`, token bloat, missing refusal patterns, untrusted-input concatenation (prompt-injection risk).
- **Multi-version migration path** (Phase #32): `MafMigrationPath(currentVersion, targetVersion)` — walks the version-keyed guide metadata, returns the ordered set of intermediate-step sections.
- **Anti-pattern scanner** (Phase #56): `MafScanAntiPatterns(repoPath, format?)` — 9 rules across security, concurrency, observability, identity, topology. `format: "markdown" | "sarif"` for CI integration.
- **Fan-out validator** (Phase #41): `MafValidateFanOut(path, format?)` — Roslyn syntax-tree walk for silent fan-in starvation.
- **CS0618 hunter** (Phase #41): `MafRunCs0618Hunt(projectPath)` — shells `dotnet build`, parses diagnostics, joins to registry.
- **NuGet diff** (Phase #41): `MafDiffPackage(packageId, oldVersion, newVersion)` — wraps `dotnet-inspect@0.7.8 diff`.
- **Best-practice reviewer agent** (Phase #42): `maf-best-practice-reviewer.agent.md` — steady-state audit on clean 1.3.0 codebases, produces `audit-report.md`.
- **Docker GHCR distribution** (Phase #28): multi-arch (`linux/amd64`, `linux/arm64`) container, semver-keyed tags, stable-only `latest`.
- **Live rule catalog** (Phase E.6): `maf://rules` resource, auto-generated at runtime from the actual rule set. Kills doc drift structurally.
- **CI/CD workflows**: weekly drift detector, per-PR audit, weekly MAF version watcher with auto-registry-extract via `maf-autopilot registry-extract <pkg> <oldVer> <newVer>` CLI subcommand.

### Changed
- **Tool surface consolidation** (Phase F.2): `MafScanAntiPatternsSarif` + `MafValidateFanOutSarif` removed from the public surface; reach SARIF output via `format="sarif"` parameter on the main tools. 19→17 tools.
- **`MafDoctor` now aggregates 4 scanners** (Phase E.4) — was 2. Includes anti-pattern + fan-out + prompt-lint + cost.
- **`init` now writes a thin pointer** (Phase B.5) to `maf://constraints` instead of an embedded copy. No more drift between this repo and downstream consumer copies.
- **README reconciled with reality** (Phase E.2): tool counts, skill counts, distribution status all synced. New "First 5 minutes" section with three concrete starter commands.
- **`PathGuard` shared helper** (Phase E.1) applied to all 8 `repoPath`/`projectPath` tools. Universal `..` traversal + shell-metacharacter guard.
- **`SourceFileWalker` shared helper** (Phase E.5) deduplicates the 17-place `EnumerateScannableFiles` + `MakeRelative` copy-paste pattern.
- **`RegistryExtractCommand` uses YamlDotNet** (Phase F.3) instead of string-concat. Eliminates YAML-special-character fragility.
- **`MafSimulateWorkflow` resolves variable-bound edges** (Phase A.1 follow-up): previously only matched `AddEdge(TypeName, TypeName)` shapes; now resolves `var reviewer = new ReviewerExecutor()` patterns via local-scope type maps.
- **Scaffolder templates use `UseOpenTelemetry(sourceName: ...)`** instead of `loggerFactory:` — matches the canonical migration-guide form.
- **All 3 agents (`maf-migration`, `maf-auditor`, `maf-best-practice-reviewer`) and `maf-constraints.instructions.md` rewired** to invoke the MCP tools (Phase A.1) instead of "load skill / run by hand."

### Fixed
- **Path traversal**: `MafScanAntiPatterns`, `MafValidateFanOut`, `MafSimulateWorkflow`, `MafDoctor`, `MafEstimateCost`, `MafLintAgentPrompt`, `MafAuditPullRequest`, `MafPreUpgradeDryRun` all gained the `..`-segment guard that was previously only on `MafNewAgent` / `MafNewExecutor`.
- **csproj `<Version>` drift**: was `1.3.0-alpha-5` while NuGet only had `1.3.0-alpha-3`; reverted csproj to match NuGet reality.
- **TROUBLESHOOTING test count**: was stale at "116 tests"; current count is ~365.
- **Generated scaffolder code**: now includes `using System.Threading;` and `using System.Threading.Tasks;` (caught by Phase F.4 compile-validation test).
- **Generated executor template**: `return default!;` changed to `return default(TOutput)!;` to avoid CS8716 in generic contexts.
- **Analyzer help-link anchors**: GitHub-slugified fragments don't resolve reliably; linked to doc root instead.
- **MCP server response on case-insensitive resource lookup**: `MafResources.GetSkill("CS0618-HUNTER")` previously crashed with file-not-found because the embedded-resource lookup was case-sensitive; now normalizes via `HashSet.TryGetValue` canonical form.
- **`InitCommand` malformed-JSON handling**: backs up `.vscode/mcp.json` to `.vscode/mcp.json.bak.<timestamp>-<guid>` on parse failure instead of silently overwriting. Lenient JSONC parsing (comments + trailing commas) is now the default.
- **`MafSimulateWorkflow` "silently returns ✅" failure mode**: variable-binding resolution + per-argument unresolved tracking.
- **`Cs0618HuntTool.MatchToRegistry` false positives**: only extracts the first quoted segment from a CS0618 message (the obsolete symbol), not the replacement hint.
- **`FanOutValidatorTool.ClassifyReturnType`**: `IAsyncEnumerable<T>` correctly classified `Ok`; raw types (`int`, `string`) now flagged `LikelyInvalid` instead of "review manually".

### Test infrastructure
- **0 → 365 xUnit tests** across two test projects (354 main + 11 analyzer).
- **Dogfood pattern**: scaffolder output is regression-tested against `MafScanAntiPatterns` and `MafValidateFanOut`.
- **Compile-validation pattern** (Phase F.4): scaffolder output additionally compiled via `CSharpCompilation.Create` against MAF surface stubs. Caught two real template bugs on first run.
- **CI**: `dotnet test` gates `dotnet pack` in `release.yml`.
- **xUnit + analyzer-testing pins** for deterministic CI (`xunit 2.9.2`, `Microsoft.NET.Test.Sdk 17.12.0`, `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit 1.1.2`).

### Upstream alignment
- **`dotnet-inspect` bumped 0.7.6 → 0.7.8** across all 21 pin sites. v0.7.8 ([release notes](https://github.com/richlander/dotnet-inspect/releases/tag/v0.7.8)) surfaces `[Obsolete]` members in listings — the gap the toolkit was originally built to work around.
- **Author attribution corrected**: `dotnet-inspect` is by [@richlander](https://github.com/richlander), not `filipw` (which is the separate `dnx` tool).

---

## [1.3.0-alpha-2] — 2026-05-01

Internal alpha; superseded by `1.3.0-alpha-3`. Not announced.

---

## [1.3.0-alpha-1] — 2026-04-30

Initial MCP server prototype. Three tools (`MafApiSafety`, `MafRegistryLookup`, `MafRegistryList`), 11 skills, 2 agents, 10 registry entries. Validated against one real migration (`maf-claims-fraud-guardian` 1.2.0 → 1.3.0). Internal alpha; not announced.

[Unreleased]: https://github.com/joslat/maf-doctor/compare/v1.15.0...HEAD
[1.15.0]: https://github.com/joslat/maf-doctor/compare/v1.14.0...v1.15.0
[1.14.0]: https://github.com/joslat/maf-doctor/compare/v1.13.0...v1.14.0
[1.13.0]: https://github.com/joslat/maf-doctor/compare/v1.12.0...v1.13.0
[1.12.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.12.0
[1.11.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.11.0
[1.10.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.10.0
[1.9.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.9.0
[1.8.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.8.0
[1.7.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.7.0
[1.4.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.4.0
[1.3.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.3.0
[1.0.0]: https://github.com/joslat/maf-doctor/releases/tag/v1.0.0
[1.3.0-alpha-3]: https://www.nuget.org/packages/maf-autopilot/1.3.0-alpha-3
[1.3.0-alpha-2]: https://www.nuget.org/packages/maf-autopilot/1.3.0-alpha-2
[1.3.0-alpha-1]: https://www.nuget.org/packages/maf-autopilot/1.3.0-alpha-1
