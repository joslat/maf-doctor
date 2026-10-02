# maf-doctor threat model

> **Audience:** users deciding whether to install maf-doctor in a security-sensitive context. Maintainers verifying mitigations are in place.
>
> **Scope:** what maf-doctor reads, what it writes, what attack surfaces exist, what we mitigate, what we don't.
>
> **Version note:** this document tracks security-relevant changes, not release numbers — it's updated whenever a hardening pass lands, most recently the **2026-07-19 security assessment** (an independent review that produced 30 findings, F-01 through F-30, all addressed; see §3.13–§3.19 and the updated §4/§5 below). Earlier passes are marked "v1.1 closure" per the original 2026 hardening release; §3.1–§3.12 are unchanged from that pass.

## 1. Data read

| Source | Trust level | Read by |
|---|---|---|
| User repo source files (`.cs`, `.csproj`) | **Untrusted** — may contain prompt injection in comments/strings | Most scanners (`MafScanAntiPatterns`, `MafValidateFanOut`, `MafEstimateCost`, etc.) |
| `MAF_REGISTRY_PATH` env-overridden registry YAML | Semi-trusted | `RegistryService.Load()` — capped at 5 MB |
| Embedded registry YAML (built into the nupkg) | Trusted | `RegistryService.Load()` (default path) |
| `dotnet build` output | Trusted (compiler) | `Cs0618HuntTool` |
| GitHub release notes from `microsoft/agent-framework` | **Untrusted** | `maf-release-watcher` workflow → fed to Copilot Coding Agent for AI-fill |
| User's `git diff` against base branch | Untrusted | `PullRequestAuditTool` |

## 2. Data written

| Target | Approval gate |
|---|---|
| `.cs` files in user repo (rewriters) | `dryRun` defaults to `true` (MCP) / preview-only unless `--apply` (CLI) as of the 2026-07-19 pass — previously the documented default and the actual code default disagreed (F-11). Explicit `dryRun: false` / `--apply` to write. |
| Generated scaffolds (`Agents/*.cs`, `Workflows/*Executor.cs`) | Idempotent — skips existing files; logs `already exists`. All-or-nothing since the 2026-07-19 pass: a symlinked target directory refuses the WHOLE scaffold rather than silently writing some files and refusing others (§3.13). |
| `.vscode/mcp.json`, `.mcp.json` (via `init`) | User invokes `init` deliberately; every managed path validated for symlink/reparse-point escapes since the 2026-07-19 pass (§3.13) |
| Steering sidecars (via `init`): `.github/instructions/maf-doctor.instructions.md`, `.claude/maf-doctor.md` + a one-line `CLAUDE.md` import, `AGENTS.md` managed block | Overwrite-on-reinit sidecars only; init never merges into the user's own instruction files |
| Update-check cache (`%LOCALAPPDATA%/maf-doctor/update-check.json`) | Background, non-blocking, silent-on-failure; disable entirely with `MAF_DOCTOR_UPDATE_CHECK=0` (§3.17, documented but not new) |
| Embedded registry path | Not writable from MCP tools — registry maintenance is via `registry-extract` + AI-fill PR-flow |

Every write target above now goes through the shared `SafeWorkspaceWriter` primitive (unpredictable temp filename, `FileMode.CreateNew`, containment/symlink revalidation) — see §3.13. Which absolute paths an MCP-connected agent may even name as `repoPath` in the first place is a separate, additional gate — see §3.14.

## 3. Attack surfaces

### 3.1 Prompt injection via repo content

User repo `.cs` files contain strings, comments, and identifiers. **Mitigated as of v1.1** — the defense has two halves:

- **Tools that re-feed user content to a downstream LLM** (`MafDraftIssue` → GitHub `create_issue`; `MafPrompts.Debug` rendered by the calling client; `RegistryExtractCommand` writing `notes:` that `MafRegistryLookup` later serves; the AI-fill workflow scripts that compose model prompts) wrap user-controlled content via `LlmFencing.Fence` (`src/maf-autopilot/Tools/LlmFencing.cs` + parity helper at `.github/scripts/llm_fencing.py`). The fence strips HTML comments, caps content at the appropriate byte budget, and wraps the result in random-sentinel BEGIN/END markers with explicit "treat as data" framing.
- **Tools that return analytical output** (`MafExplain`, `MafLintAgentPrompt`, every scanner under `MafScanAntiPatterns` / `MafValidateFanOut` / `MafDoctor`) bound the attack surface by construction — output is Roslyn-syntax-extracted identifiers + line numbers + structured findings, never raw user-source-text. A `// Ignore all previous instructions` comment in a user's `.cs` file never reaches a downstream LLM via these paths because comments are not part of the extracted output shape.

Anchored to OWASP LLM05 (Improper Output Handling) + MITRE ATLAS AML.T0051 (LLM Prompt Injection, indirect variant). See `docs/security.md` for the user-facing breakdown of the fenced sites.

### 3.2 Path traversal via `MAF_REGISTRY_PATH`

Environment variable overrides the embedded registry path. **Mitigated as of v1.1** — `RegistryService.ValidateOverridePath` rejects (a) shell metacharacters / NUL / CR / LF in the path string and (b) reparse-points at the file itself. Optional `MAF_REGISTRY_PATH_ROOTS` env var declares a `;`-separated allowlist of acceptable parent directories. The existing 5 MB byte-size cap still applies.

### 3.3 AI-fill loop poisoning

`maf-release-watcher` reads release notes from upstream and dispatches a Copilot Coding Agent task to fill `TODO` placeholders in registry entries. **Mitigated as of v1.1** — every hop in the chain now fences user content via `LlmFencing.Fence`:

- `gen_guide_section.py` fences both the release notes AND the `dotnet-inspect` diff before embedding in the per-version migration guide.
- `RegistryExtractCommand` fences the diff body in the `notes:` field (8 KB cap because the value persists forever in `registry.yaml`).
- `ai_review_build_prompt.py` fences each PR-modified registry entry before embedding in the semantic-review model prompt.
- `VerifyRegistryCommand` `pre-X.Y.Z` and `IsRawDraft` branches now run `CheckContentSafety` (HTML-comment + length caps) instead of bypassing all checks.
- `IsRawDraft` tightened to require `// TODO` at line start via `(?m)^\s*//\s*TODO\b` regex (string literals containing `// TODO` no longer re-qualify as raw drafts).
- `BraceBalanceFailure` uses a bracket stack instead of delta-counting — catches `}{` and `({)}` patterns that v1's count-only check missed.
- `FormatEntry.NeutralizeFences` inserts zero-width spaces between triple backticks in registry examples so they cannot break out of the surrounding `csharp` markdown fence.

### 3.4 Rewriter correctness bugs

A Roslyn rewriter bug could corrupt user `.cs` files. **Mitigations** (strengthened in v1.1):

- `--dry-run` is default-aware on every writer.
- Multi-version regression CI runs every rewriter against `samples/maf-1.0-sample/`, `1.2-sample/`, `1.3-sample/` and verifies the output compiles.
- Corruption guards on the two highest-risk rewriters: `ExecutorSealedRewriter` skips `abstract` / `static` classes (preventing `sealed abstract` compile errors); `SyncOverAsyncRewriter` skips `await`-inside-lock and `await`-in-non-async (preventing CS1996 / CS0117).
- Idempotence property tests (`RewriterIdempotenceTests`) lock the contract that running a rewriter twice produces the same output as once — applies on both happy and skip paths.

### 3.5 `dotnet build` shell-out

`MafRunCs0618Hunt` shells `dotnet build`, which executes the user's project file. If the project has malicious MSBuild targets, those run. **Acceptable, but explicitly annotated** (v1.1) — `[McpServerTool(ReadOnly = false, OpenWorld = true)]` so MCP-spec-compliant clients prompt the user for consent before invoking. The tool description warns about untrusted repos. Users running maf-doctor on untrusted projects should sandbox the process.

### 3.6 Type-expression input in `MafNewExecutor`

`inputType` and `outputType` parameters are Roslyn-parsed and roundtrip-validated. v1.1 added `projectNamespace` to the same defense (`AgentScaffolder.IsValidNamespace`) — pre-fix the namespace flowed verbatim into the generated `namespace {{ns}};` declaration and a payload like `MyApp; class Evil { static int x = Process.Start("calc"); }` produced a compilable file with attacker-controlled types. Security-pinned by `ScaffolderSecurityTests`.

### 3.7 Command injection via tool arguments (Keysight, 2026)

**Reference:** [Keysight blog, 12 Jan 2026](https://www.keysight.com/blogs/en/tech/nwvs/2026/01/12/mcp-command-injection-new-attack-vector). The attack class: an MCP tool concatenates LLM-supplied arguments into a shell command string; the model is prompt-injected into emitting payloads that break out of the intended command.

**Not vulnerable.** Five argv-construction configurations, all inside `ProcessRunner.cs` (`dotnet build`, the fail-closed `dotnet-inspect --version` probe, `dotnet-inspect diff`, the exact-version `dnx` on-demand fallback, and `git diff` via `RunGit`) — as of the 2026-07-19 pass (F-09), `PullRequestAuditTool`'s git invocation was folded in here too, replacing a standalone spawn site. All funnel through `ProcessRunner`'s one shared `Process.Start` call, use `ProcessStartInfo.ArgumentList` (argv-style, no shell parsing), and set `UseShellExecute = false`. No code path uses the unsafe `ProcessStartInfo.Arguments` *string* property. No tool exposes a `command` / `args` parameter to the LLM. Invariant checkable via `grep -rn "Process.Start" src/` AND enforced in CI by `.github/workflows/ci-invariants.yml` (Phase 0 — Job 1 allowlists call-sites, tightened to just `ProcessRunner.cs`). See [`docs/security.md` → "Command injection via tool arguments"](../security.md#command-injection-via-tool-arguments-keysight-2026) for the user-facing breakdown.

### 3.8 Code injection via scaffold-namespace param (v1.1 closure)

`AgentScaffolder.ScaffoldAgent` / `ScaffoldExecutor` accept a `projectNamespace` parameter that flows into the generated source's `namespace {{ns}};` declaration. **Mitigated** — `IsValidNamespace` mirrors the existing `IsValidTypeExpression` defense (pre-reject char filter + Roslyn `ParseName` + roundtrip check). Anchored to OWASP MCP04 Command Injection / path-escape variant.

### 3.9 LLM-to-LLM prompt smuggling via tool outputs (v1.1 closure)

`MafDraftIssue` emits a markdown body the calling agent passes to a sibling `create_issue` MCP tool — i.e. user input ends up in another LLM's context. **Mitigated** — `LlmFencing.Fence` wraps every user-controlled field (symptom, expected, actual, snippet) with explicit "treat as data" framing + random-sentinel BEGIN/END markers + length caps. Tool description instructs the calling agent to pass the body as-is (not strip the fence). Anchored to OWASP LLM05 Improper Output Handling + OWASP Agentic 2026 Indirect Prompt Injection via Tool Output.

### 3.10 Supply-chain prompt injection via upstream release notes (v1.1 closure)

`maf-release-watcher` reads `gh release view microsoft/agent-framework --jq .body` and embeds the result into the per-version migration guide (opened as a PR since the 2026-06-28 "C" refactor, not committed direct-to-main) and downstream AI-fill issue body. A malicious upstream maintainer (TA-1) could ship HTML-comment-disguised injection in release notes. **Mitigated** — `gen_guide_section.py` fences both the release notes AND the `dotnet-inspect` diff via the Python `llm_fencing` helper. Anchored to MITRE ATLAS AML.T0048 (ML Supply Chain Compromise) + AML.T0058 (Context Poisoning).

### 3.11 Workflow input injection via `workflow_dispatch` templating (v1.1 closure)

`${{ inputs.X }}` templates inside `run:` blocks substitute literal text before shell parsing — every such site is a potential command-injection lane if any future contributor changes the surrounding script. **Mitigated** — every workflow_dispatch input now flows via `env:` redirect (`env: { X: ${{ inputs.X }} }` then `$X` in shell). Every workflow_dispatch workflow runs a `validate-inputs` job FIRST (with `permissions: {}`) that regex-checks the input shape (semver / positive-int / enum) before any privileged step. Downstream jobs gate via `needs: validate-inputs`. CI invariant `ci-invariants.yml` Job 4 blocks any future `${{ inputs.* }}` in `run:` blocks. Anchored to GitHub's "Security hardening for GitHub Actions" guidance.

### 3.12 Third-party action supply chain (v1.1 closure)

All third-party GitHub Actions are SHA-pinned (including the semantic review's
Copilot CLI setup). A tag-pinned action like `actions/checkout@v6` resolves to
whatever HEAD that floating tag points to — a compromised upstream account
could push arbitrary code into the action and inherit our runner's tokens
(NUGET_API_KEY, packages:write, the bot App key or COPILOT_ASSIGN_PAT). SHA pins eliminate the
lane; Dependabot is configured to bump both SHA and tag-comment in lockstep.

### 3.13 Predictable-filename write races (2026-07-19 closure)

An independent security assessment found that every write site (`AutoFixTool`, `InitCommand`, the `MafNewAgent`/`MafNewExecutor` scaffolders, `RegistryService`'s `MAF_REGISTRY_PATH` override validator, `UpdateAdvisor`'s cache write) staged through a **predictable** temp filename (e.g. `<file>.autofix.tmp`) using `File.WriteAllText`, which follows an existing symlink at that path rather than refusing it. A repository — or another local actor — that pre-creates that exact filename as a symlink to a file outside the repo can redirect the write there. This is more than a theoretical TOCTOU: the filename was fully predictable and could be committed to a malicious checkout ahead of time.

**Mitigated.** A new shared primitive, `SafeWorkspaceWriter` (`WriteAtomic` / `TryCreateNew`), replaces every ad-hoc write site:

1. Validates the destination against `PathGuard.ValidateContainment` (rejects a symlinked leaf or any symlinked parent segment up to the workspace root).
2. Stages through an **unpredictable** temp filename (`.{guid}.mafdoctor.tmp`) inside the already-validated destination directory — an attacker cannot pre-create a path they can't guess.
3. Opens the temp file with `FileMode.CreateNew`, which refuses to open a pre-existing path (including one raced into place after validation but before the open) rather than following it.
4. Revalidates containment immediately before the atomic replace, narrowing (not eliminating — see the residual TOCTOU note below) the window between check and use.

The same review also found `SourceFileWalker.MakeRelative`'s containment check used a bare `StartsWith` with no separator boundary, so a root of `/work/repo` would incorrectly accept `/work/repo-evil` — fixed to require an exact match or a `root + separator` prefix, matching `PathGuard`'s existing boundary-safe pattern.

Scaffolder writes are additionally now all-or-nothing: previously a symlinked `Agents/` directory refused `Agents/Bot.cs` while a separate, non-symlinked `Tests/` directory still received `Tests/BotTests.cs` — no containment property was violated, but the result was a confusing half-scaffolded state (a test referencing a class that was never created). `MafNewAgent`/`MafNewExecutor` now pre-validate every target file before writing any of them.

**Residual gap (accepted, tracked as F-21):** `PathGuard.ValidateContainment` validates a path, and a caller later reopens it by name — a concurrent local process could theoretically race a symlink into place between validation and use. This requires local write access to the workspace (the same trust boundary as the local user running maf-doctor) and is not remotely exploitable via tool arguments alone; `SafeWorkspaceWriter`'s revalidate-before-replace narrows but does not close this window. True close would require directory-relative/open-at filesystem APIs, tracked as a future hardening item, not done here.

### 3.14 MCP tool calls escaping the intended workspace (2026-07-19 closure)

`PathGuard.ValidateRepoPath` confirmed a `repoPath` argument was syntactically absolute and pointed at an existing directory, but never checked it belonged to a workspace the MCP client actually granted. A model-controlled tool call — including one influenced by prompt injection in repo content the model had already read — could point `repoPath` at any absolute directory the host account can read: another repository, the user's home directory, a mounted drive.

**Mitigated.** A new `WorkspacePolicy` class adds an **MCP-only** containment check wired into `PathGuard.ValidateRepoPath`:

- `MAF_DOCTOR_WORKSPACE_ROOTS` (semicolon-separated absolute paths, set in the MCP server's `env` config) scopes every repo-path-taking tool call to those roots when configured.
- Filesystem roots (`/`, `C:\`) and the user's home directory are **always** rejected in MCP mode, independent of whether an allowlist is configured — no legitimate call targets either.
- Deliberately **CLI-only exempt**: a human running `maf-doctor doctor ~/some/other/repo` directly at their own shell is the trust boundary itself, and must keep working against any path they type. `WorkspacePolicy.EnableMcpMode()` is called from exactly one place in `Program.cs`, on the branch that starts the MCP server — every CLI subcommand returns before reaching it.
- `maf-doctor init` writes `MAF_DOCTOR_WORKSPACE_ROOTS=<repo path>` into the generated MCP config on first write (never overwrites a value a user already customized), so newly-initialized workspaces are scoped to themselves automatically. `MafDoctorStatus` reports the currently active policy.
- Default (unconfigured) behavior remains unrestricted beyond the always-on filesystem-root/home-directory denial, matching prior behavior for existing installs.

**Found in review, before shipping:** the initial implementation's "always reject a filesystem root" check normalized the path (trimming trailing separators) *before* comparing it against `Path.GetPathRoot`. Trimming every trailing `/` off the bare POSIX root `/` collapses it to an empty string, and `Path.GetPathRoot("")` returns empty — silently defeating the check for exactly the input it exists to block. Windows survived by coincidence (`C:\` trims to `C:`, still non-empty). This was caught by an independent review agent that actually ran the test suite on Linux (this project's CI platform) rather than only reading the code, and is fixed by comparing the untrimmed root against the untrimmed full path.

### 3.15 Unbounded subprocess and scan memory consumption (2026-07-19 closure)

Several independent issues let a pathological input (a huge build/diff output, a very large diffed file, a repo large enough to matter) consume memory disproportionate to the caps the code claimed to enforce:

- `ProcessRunner` (backing `dotnet build`, `dotnet-inspect diff`, and `git`) read stdout/stderr fully via `ReadToEndAsync` before truncating at a 16M-character cap — the cap bounded the *returned* string, not memory used *while reading*. **Mitigated:** streams both pipes concurrently against a shared character budget, truncating during the read. Retained managed memory is bounded to a fixed 16M UTF-16 code units regardless of content; the cap counts `char`s read from the pipe, not UTF-8 bytes, so on heavy non-ASCII output the actual pipe-bytes-consumed before truncation can run higher than "16M" suggests — a message-accuracy nuance (round-2 review), not a memory-safety gap.
- `PullRequestAuditTool`'s git invocation read stdout to completion synchronously, then stderr, then called `WaitForExit` — the classic pipe-deadlock shape if git fills the stderr OS buffer while still writing stdout. **Mitigated:** routed through `ProcessRunner`'s concurrent-drain implementation.
- `PullRequestAuditTool.BuildReport` read PR-diff files via a naive `Path.Combine` + `File.ReadAllText` with no containment check, per-file cap, or aggregate-byte cap — a malicious fork PR could submit a symlinked "changed" file to read outside the repo, or enough large files to pressure the Roslyn host. **Mitigated:** every changed file now goes through `PathGuard.ValidateContainment` plus file-count/per-file/aggregate-byte budgets; a scan that skips files reports itself explicitly incomplete (and the "Files scanned" count reflects what was actually scanned, not the total diff size — an earlier version of this fix left the count misleading).
- `SemanticKernelDetectorTool.Scan` read every accepted `.cs` file into one in-memory list before analysis — bounded per-file (10 MB) and per-file-count (50,000) by the walker, but not in aggregate, so the theoretical retained set was still unbounded. **Mitigated:** `SourceFileWalker.ScanBudget` gained a `MaxTotalBytes` (500 MB default) aggregate cap, and the detector was rewritten as two passes (pass 1 only checks for a global SK `using`, never retaining a source body; pass 2 re-enumerates and analyzes one file at a time) so no more than one file's content is held in memory at once. `SkScanResult` now carries a `Truncated` flag surfaced in both markdown and JSON output — a truncated scan no longer renders as an unqualified clean verdict.
  - **Known residual correctness nuance:** pass 1 and pass 2 use independent budgets. On a repo large enough to hit the aggregate cap, if the file declaring the global `using` sorts after pass 1's budget is exhausted, pass 1 never sees it — even though pass 2's separate, later-exhausting budget may still fully analyze in-budget files that were relying on that global using for SK-context. Those files can be silently under-detected, not merely "not looked at." `Truncated` still fires (either budget hit its cap), so the result is flagged incomplete, just not with this exact mechanism spelled out. Requires several hundred MB of `.cs` source to trigger; not closed more precisely here, since doing so would trade a narrow, already-flagged edge case for materially more two-pass complexity.
- `ExplainFindingTool` validated containment before reading a file but had no size cap — **mitigated** with the same 10 MB per-file cap the primary walker already used elsewhere.

### 3.16 Auto-fix and scaffold tool ReadOnly/Destructive semantics (2026-07-19 closure)

Three related findings about MCP behavior-hint accuracy:

- `MafAutoFix`/`MafAutoFixAll` defaulted `dryRun` to `false` — writing by default — while `docs/security.md` had described writers as safe-by-default. **Mitigated:** both MCP tools now default `dryRun` to `true`; the CLI's `autofix-all` now previews unless `--apply` is passed (`--dry-run` remains a no-op alias of the new default; if both flags are given, preview wins). Every suggested-command string the tool itself emits (`doctor`'s remediation plan, the `maf-remediate` prompt's driving instructions, `MafExplainFinding`'s guidance, and the live-served `maf-remediation-playbook` skill document) was updated to include `dryRun: false` / `--apply` so they keep actually applying fixes when followed verbatim — this was the actual bug class here: a client or an agent that reads "run `MafAutoFixAll(repoPath)`" literally now gets a silent no-op preview unless the instruction says so explicitly.
- `MafDiffPackage`/`MafPreUpgradeDryRun` are `ReadOnly=true` (correctly — they write nothing to the workspace), but their shared `dotnet-inspect` invocation can fall back to downloading and executing exact `dnx dotnet-inspect@0.9.1` when the PATH tool is missing, mismatched, or unverifiable. A client can auto-invoke a `ReadOnly=true` tool without confirmation, and a silent package download+execute is a materially different risk than a passive read even though it is out of scope of the annotation's literal meaning (workspace writes). **Mitigated:** the PATH executable is first required to report exact 0.9.1 (optional build metadata is accepted), and the fallback is gated behind `MAF_DOCTOR_ALLOW_TOOL_DOWNLOAD` (default off). Unset, the tool fails closed with install/update instructions instead of running an incompatible executable or downloading one. The annotation itself was kept as-is rather than reclassified, since changing it would also block auto-invocation for the common, low-risk case where the exact tool simply is not installed yet in an otherwise-trusted environment. Note: this gate is shared code, so it also applies to the `maf-doctor registry-extract` CLI subcommand — CI is unaffected (the release-watcher workflow pre-installs `dotnet-inspect` at the same exact version before ever reaching this code), but a local developer without exact 0.9.1 now needs to install/update it or opt into the fallback.
- `MafExplainFinding`'s description was tightened to state plainly that returned source is sent to whichever model is driving the MCP session, and that `ReadOnly=true` describes filesystem effects only, not data exposure — it is not itself a "safe to auto-approve" signal for a sensitive repository. No opt-in flag to withhold source by default was added, since returning grounded source *is* this tool's purpose; workspace-scoping (§3.14) and honest documentation are the actual mitigations here, not gutting the tool.

### 3.17 CI/CD and release supply-chain integrity (2026-07-19 closure)

The largest cluster of findings concerned the GitHub Actions workflows that build, test, audit, and release maf-doctor itself:

- **Floating prerelease installs in privileged jobs.** `maf-release-watcher.yml`'s `analyze-and-update` job (`contents:write`, `pull-requests:write`, `issues:write`, `actions:write`, and a maintainer PAT that bypasses branch protection) — plus `maf-ai-fill-verify.yml`, `maf-drift-detector.yml`, and `maf-pr-audit.yml` — installed `maf-doctor` itself via `dotnet tool install --global maf-doctor --prerelease`, meaning what ran inside those privileged jobs was whatever the newest floating prerelease on NuGet happened to be, not the reviewed code at the commit actually checked out. **Mitigated:** all four now build `maf-doctor` from the already-checked-out, already-reviewed source (`dotnet build ... && dotnet <dll> <subcommand>`) instead. The release-watcher build step was given `continue-on-error: true` to preserve the fault-tolerance boundary the code it replaced had (a build hiccup must not abort the whole weekly PR-opening pipeline) — an earlier version of this fix dropped that boundary and was caught in review.
- **Unpinned Python/scanner installs.** `pip install pyyaml` (no version), `pipx install 'cisco-ai-mcp-scanner>=4,<5'` (a floating range) in jobs carrying `pull-requests:write`. **Mitigated:** exact-pinned (`pyyaml==6.0.1`, `pytest==9.0.3`, `pipx==1.16.0`, `cisco-ai-mcp-scanner==4.6.0` — the last one pinned specifically to the version this document's §7 scan results were actually recorded against). This is version pinning, not full hash-pinning (`pip --require-hashes`); the latter is a larger reproducibility investment tracked under §3.18/§5.
- **Untrusted artifact trust in a privileged comment-posting workflow.** `maf-pr-audit-comment.yml` (a `workflow_run` companion with `pull-requests:write`, correctly avoiding `pull_request_target` and never checking out fork-supplied code) posted to whatever PR number an artifact — produced by the unprivileged job that ran Roslyn analysis over the PR's own, possibly fork-supplied source — claimed, with no validation. **Mitigated:** the PR number is now resolved authoritatively via `gh api repos/{repo}/commits/{head_sha}/pulls`, using `head_sha` from the trusted `workflow_run` event context (set by GitHub, not artifact content), and cross-checked against the artifact's claim; a mismatch or unresolvable SHA fails closed. The comment body is now capped and stamps the audited SHA.
- **Release version derivation inconsistency.** `release.yml`'s csproj-version-override step's condition covered manual input and `v`-prefixed tags, but not unprefixed numeric tags — even though the version-*computation* step already handled that case correctly. Pushing an unprefixed tag would compute the right version for the GitHub Release notes but silently skip overriding the csproj, so the published NuGet package could carry a stale, mismatched version. **Mitigated:** both the override decision and the "is this a tag" decision (previously a separate 10-way `startsWith` OR-chain, itself a duplicated-logic risk) are now derived once, in the version-computation step, and a new verification step reads the csproj back and fails the release if it doesn't match the resolved version.
- **Advisory-only security gates.** `mcp-scanner.yml`'s hard-fail was deferred behind `continue-on-error: true` pending 5 consecutive clean runs (both PR and cron triggers), and `maf-pr-audit.yml`'s compiler-bomb-cap skip case exited successfully with no failure signal (samples-only skips, a legitimate scope decision, correctly did not fail). **Mitigated:** the mcp-scanner promotion criterion was verified for real — not assumed — by fetching actual scanner output (not just job status, which the scan step's own `|| true` had already masked) for the 5 most recent runs via the GitHub API, confirming each showed zero findings across all 28 tools; `continue-on-error: true` was then removed and a dedicated final step decides pass/fail by checking scan-results.txt's *exact* shape (not merely that the clean-marker phrase appears somewhere in it — an earlier version used `grep -q`, which a hypothetical future output format mixing the clean marker with real findings could have passed; caught in review and hardened to an exact two-line match). `maf-pr-audit.yml` now distinguishes the skip reason and fails the job specifically for the compiler-bomb-cap case (never for the legitimate samples-only case), reporting exact file-count/byte metrics and explicit "INCOMPLETE" wording rather than implying a clean result.
- **`$GITHUB_OUTPUT` heredoc injection via PR content.** `maf-ai-fill-verify.yml` echoed `verify-registry`/cross-file-check output — derived from validating the PR's own, possibly adversarial `registry.yaml` — into `$GITHUB_OUTPUT` using a fixed `output<<EOF ... EOF` delimiter pair. A crafted registry entry containing a standalone `EOF` line closes the heredoc early, and the remainder is then parsed as literal new `key=value` GITHUB_OUTPUT entries — reproduced and confirmed exploitable against the pre-fix pattern before fixing it. **Mitigated:** both sites generate a random delimiter per run and verify it doesn't collide with the tool's own output before use, with a bounded retry cap.

### 3.18 Build and dependency reproducibility (2026-07-19 closure)

- `packages.lock.json` added for all 4 .NET projects (`RestorePackagesWithLockFile` in `Directory.Build.props`); CI restores in `--locked-mode`, failing if a project's lock file disagrees with `Directory.Packages.props` instead of silently resolving to whatever the transitive dependency graph produces that day.
- Found and fixed a separate, pre-existing bug while wiring this up: `.gitignore`'s negation for `packages.lock.json` had its explanatory comment on the same line as the pattern (`!packages.lock.json  # explicitly opt-in via csproj`). Git does not treat a trailing same-line `#` as a comment — only a line whose first character is `#` is a comment — so the whole line, comment text included, was parsed as one literal (non-matching) pattern. The negation had silently never taken effect; every lock file in the repo had been gitignored despite the stated intent.
- `global.json`'s `allowPrerelease` flipped from `true` to `false` — a machine with only a prerelease SDK installed now fails the build loudly instead of silently building/testing against an unreviewed toolchain. `rollForward` and `version` were tightened from `latestMajor`/`8.0.100` to `latestMinor`/`10.0.100` — `latestMajor` can silently roll onto a future, unreleased major SDK; `latestMinor` locks to the major version actually in use while still tolerating patch/feature drift. (An earlier attempt to tighten `rollForward` alone, without also raising `version`, was empirically shown in review to break the net8/9/10 multi-target build — an 8.x-only SDK cannot restore net9.0/net10.0 targets, the same NETSDK1045 constraint the Dockerfile's own build-stage comment already documented from the project's alpha era. The corrected combination was verified via a clean-room build against a single-SDK container, not just reasoned about.)
- `Dockerfile`'s `FROM` lines are now digest-pinned (`mcr.microsoft.com/dotnet/sdk@sha256:...`, `.../runtime@sha256:...`) instead of the mutable `:10.0` tag, so a compromised or tampered push under that tag can't silently change what the image builds from. Dependabot already tracks Docker-ecosystem updates and will PR digest bumps on the same weekly cadence as the SHA-pinned GitHub Actions elsewhere in this repo.
- Not done in this pass: full hash-pinning of Python dependencies (`pip --require-hashes`, which needs a maintained hash manifest for every transitive dependency), and SBOM/provenance/artifact-signing for the published NuGet packages and container image. Tracked in §5.

### 3.19 Documentation accuracy as a trust signal (2026-07-19 closure)

Several `docs/security.md` claims had drifted from the implementation they described: categorical "Are we vulnerable? No." language for the LLM-fencing mitigations (F-18; §3.1, §3.9, §3.10 above) — which are probabilistic defenses against a model that might not respect the framing, not deterministic guarantees — versus the same categorical language correctly applied to genuinely structural guarantees (argv-only process spawn, env-redirected workflow inputs, Roslyn roundtrip validation). A Dockerfile comment claimed the server "writes nothing to the filesystem at runtime," which was never true once auto-fix/scaffold/`init` are counted. **Mitigated:** the fencing-based claims were relabeled "Mitigated, not eliminated" with an explanation of what the fencing actually does and doesn't guarantee; the genuinely deterministic claims were left as "No" since softening those would itself be an inaccuracy in the other direction. The Dockerfile comment was corrected, and `docs/security.md` gained a "Container deployment" section documenting read-only vs. write-enabled `docker run` profiles, and an "Outbound network behavior" section documenting the `MAF_DOCTOR_UPDATE_CHECK` env var — which already existed in code and worked correctly, but was undocumented anywhere outside the source.

## 4. What we mitigate

### Carried over from 1.0

- ✅ `PathGuard.ValidateRepoPath` validates repo-path inputs across tools.
- ✅ 5 MB cap on env-overridden registry.
- ✅ `--dry-run` default-aware on all writers.
- ✅ `verify-registry` PR-gate on AI-fill output (structural checks).
- ✅ Type-expression Roslyn-parsing on `MafNewExecutor` inputs.
- ✅ Multi-version regression CI on rewriters.

### Added in 1.1 (security hardening release)

- ✅ **Path-escape via secondary path parameters** — `PathGuard.ValidateContainment` jails resolved paths inside `repoPath`, rejecting absolute-path bypass, `..`-segment escape, and symlink redirect (including file-level symlinks).
- ✅ **Annotation correctness** — MCP-spec annotations (`ReadOnly`/`Destructive`/`Idempotent`/`OpenWorld`) audited and corrected: `MafRunCs0618Hunt` (`ReadOnly=false, OpenWorld=true`), `MafNewAgent`/`MafNewExecutor` (`Destructive=true`), `MafDiffPackage`/`MafPreUpgradeDryRun` (`OpenWorld=true`). Reflection tests pin the contract.
- ✅ **Scaffold namespace validation** — `AgentScaffolder.IsValidNamespace` blocks code-injection via `projectNamespace`.
- ✅ **LLM data-fencing** — `LlmFencing.Fence` wraps user-controlled content destined for any downstream LLM, with HTML-comment strip + UTF-8 byte cap + random-sentinel BEGIN/END markers.
- ✅ **Workflow input validation** — every `workflow_dispatch` workflow has a `validate-inputs` job (regex/enum check before any privileged step) and routes inputs via `env:` redirect, not `${{ }}` templating in `run:` blocks.
- ✅ **Workflow least-privilege** — explicit `permissions:` block on every workflow; `validate-inputs` jobs run with `permissions: {}`.
- ✅ **Third-party action SHA-pinning** — 9 actions × 23 call sites; Dependabot keeps current.
- ✅ **Rewriter corruption guards** — `ExecutorSealedRewriter` skips abstract/static; `SyncOverAsyncRewriter` skips lock/non-async. Idempotence property tests across all 5 rewriters.
- ✅ **Analyzer false-negative reduction** — `SensitiveDataAnalyzer` handles `ImplicitElementAccessSyntax` + `ElementAccessExpressionSyntax` (parity with rewriter); generated-code flags consistent across MAF001/002/003.
- ✅ **Bounded-input length caps** — `BoundedInput.Validate` on every user-controlled string input to MCP tools.
- ✅ **Symlink-aware recursive walks** — `SourceFileWalker` sets `AttributesToSkip = ReparsePoint | Hidden | System`.
- ✅ **Path leakage closed** — `SourceFileWalker.MakeRelative` throws on out-of-root rather than leaking the absolute path.
- ✅ **`MAF_REGISTRY_PATH` containment** — `ValidateOverridePath` rejects metachars + reparse-points; optional `MAF_REGISTRY_PATH_ROOTS` allowlist.
- ✅ **Regex hygiene** — `NonBacktracking + MatchTimeout=100ms` on every scanner regex.
- ✅ **VerifyRegistry tightening** — `pre-X.Y.Z` + `IsRawDraft` bypass closed; nesting-aware brace balance; `CheckContentSafety` runs even on skipped entries; `FormatEntry` neutralizes triple-backticks.
- ✅ **Persistence-layer fencing** — `RegistryExtractCommand.cs` fences `notes:` content (8 KB cap, lives forever in `registry.yaml`).
- ✅ **PR-audit compiler-bomb cap** — `maf-pr-audit.yml` skips Roslyn audit when changed `.cs` files > 200 OR > 5 MB.
- ✅ **CI invariants** — `ci-invariants.yml` blocks future regressions: `Process.Start` allowlist, regex hygiene, `EnumerationOptions` on recursive walkers, no `${{ inputs.* }}` in `run:` blocks, `permissions:` block required.
- ✅ **CODEOWNERS** — explicit pins for security-critical paths (Tools/, Scaffolding/, Commands/, Analyzers/, .github/, docs/security*, etc.).

### Added in the 2026-07-19 security assessment

- ✅ **Symlink-safe atomic writes everywhere** — `SafeWorkspaceWriter` (unpredictable temp filename + `FileMode.CreateNew` + revalidation) replaces every ad-hoc write site (auto-fix, `init`, scaffolders, registry-override reader, update cache). §3.13.
- ✅ **MCP workspace allowlist** — `MAF_DOCTOR_WORKSPACE_ROOTS`, MCP-only, filesystem-root/home-directory always rejected, CLI unaffected. §3.14.
- ✅ **Streaming-bounded subprocess capture** — `ProcessRunner` bounds memory during the read, not just the returned string; concurrent stdout/stderr drain closes a pipe-deadlock shape in the PR-audit git invocation. §3.15.
- ✅ **PR-audit containment + size budgets** — changed-diff files validated via `PathGuard.ValidateContainment`, file-count/per-file/aggregate-byte caps, honest "N of M files scanned" + explicit incomplete-scan reporting. §3.15.
- ✅ **Aggregate scan memory budget** — `ScanBudget.MaxTotalBytes`, two-pass SK detector holding at most one file's content at a time, `Truncated` flag surfaced in every output format. §3.15.
- ✅ **Bounded direct reads** — `MafExplainFinding` gained the same 10 MB per-file cap the primary walker already enforced elsewhere. §3.15.
- ✅ **Preview-by-default destructive tools** — `MafAutoFix`/`MafAutoFixAll` MCP default and CLI `autofix-all` both preview unless explicitly told to write; every tool-suggested command string (including the live-served `maf-remediation-playbook` skill) updated to keep applying fixes when followed literally. §3.16.
- ✅ **Gated on-demand tool download** — `dnx dotnet-inspect` auto-download behind `MAF_DOCTOR_ALLOW_TOOL_DOWNLOAD` (default off), closing the "ReadOnly tool silently downloads and executes code" gap. §3.16.
- ✅ **Read-only ≠ safe-to-auto-approve, stated plainly** — `MafExplainFinding`'s description now says returned source goes to the connected model. §3.16.
- ✅ **No floating prerelease installs in privileged CI jobs** — `maf-release-watcher.yml` and 3 other workflows build `maf-doctor` from the already-reviewed checkout instead of `dotnet tool install --prerelease`. §3.17.
- ✅ **Exact-pinned Python/scanner tooling** — `pyyaml`, `pytest`, `pipx`, `cisco-ai-mcp-scanner` all exact-versioned, not floating ranges. §3.17.
- ✅ **Authoritative PR-identity resolution** — the privileged PR-audit-comment workflow resolves the PR number via the GitHub API from the trusted `workflow_run` head SHA, not an unvalidated artifact claim. §3.17.
- ✅ **Single source of truth for release version resolution** — `release.yml`'s tag/input/csproj version derivation unified; a post-override verification step fails the release if the packed version doesn't match. §3.17.
- ✅ **mcp-scanner promoted to a hard-fail required gate** — verified via real scanner output (not just job status) across 5 consecutive clean runs before flipping; the pass/fail check itself hardened from substring match to exact-shape match. §3.17.
- ✅ **Fail-closed on skipped mechanical PR audits** — the compiler-bomb-cap skip case (not the legitimate samples-only skip) now fails the check with exact metrics, instead of silently passing. §3.17.
- ✅ **Randomized `$GITHUB_OUTPUT` delimiters** — closes a reproduced-and-confirmed heredoc-injection lane via PR-influenced tool output. §3.17.
- ✅ **Locked NuGet restore** — `packages.lock.json` for all 4 projects, CI restores in `--locked-mode`. §3.18.
- ✅ **No-prerelease-SDK-by-default, major-version-locked roll-forward** — `global.json` tightened, verified against the project's actual net8/9/10 multi-target constraint. §3.18.
- ✅ **Digest-pinned container base images** — `Dockerfile` FROM lines pinned by `sha256`, tracked by the existing Dependabot Docker ecosystem entry. §3.18.
- ✅ **Documentation-implementation consistency pass** — categorical prompt-injection-immunity claims relabeled honestly; false "writes nothing" container claim corrected; previously-undocumented `MAF_DOCTOR_UPDATE_CHECK` and container deployment profiles documented. §3.19.

### Added in the Fable-5 residual remediation (2026-07)

The residual review of v1.13.0 (115 findings → 9 phases, PRs #154–163) closed the
remaining reporting / CI / automation gaps. Security-relevant additions:

- ✅ **Subprocess environment scrubbing (SEC-09)** — `ProcessRunner` strips credential-like
  env vars (`*TOKEN*`/`*SECRET*`/`*KEY*`/`*PASSWORD*`/`*PAT`/`AWS_*`/`AZURE_*`/`GITHUB_*`/`GH_*`)
  from every child (`dotnet`/`dotnet-inspect`/`git`) before spawn — closes the §5 env-inheritance gap.
- ✅ **Git config neutralization (SEC-22)** — every `git` invocation forces
  `core.fsmonitor=false` / `core.hooksPath=` / `core.pager=cat` / `protocol.ext.allow=never`
  + `GIT_CONFIG_NOSYSTEM`, so a hostile `.git/config` in an untrusted repo can't execute commands.
- ✅ **Build-provenance attestation (SEC-13)** — `release.yml` runs SHA-pinned
  `actions/attest-build-provenance` over the `.nupkg`s before push and gates publish on a
  tag-must-be-on-`main` guard (`merge-base --is-ancestor`) — partially closes the §5 provenance gap.
- ✅ **NuGet vulnerability-audit teeth (SEC-15)** — `Directory.Build.props` sets
  `NuGetAuditMode=all` + `WarningsAsErrors NU1903;NU1904`, so a HIGH/CRITICAL transitive advisory
  fails restore in every CI lane including release.
- ✅ **Fence-label injection closed (SEC-01, High)** — the LLM data-fence *label* is sanitized to
  `[A-Za-z0-9._-]` (cap 64) in BOTH `LlmFencing.cs` and `llm_fencing.py`, so an attacker-controlled
  registry id can't inject text OUTSIDE the data fence.
- ✅ **Fork-safe PR commenting (SEC-11)** — the scan/verify jobs that build PR-supplied (possibly
  fork) code no longer hold `pull-requests: write`; they upload an artifact and one generalized
  `workflow_run` companion posts the sticky comment from the base-repo context.
- ✅ **Fail-closed release classification (WM-11)** — the release-watcher captures the
  `dotnet-inspect` diff as stdout-only, empties it on any non-zero exit, and requires the closing
  `**Summary:**` marker, so a garbled/truncated diff classifies as *breaking*, never additive.
- ✅ **Non-vacuous scanner gate (SEC-12) + stream separation (WM-13)** — a `MIN_TOOLS` floor rejects
  a "0 tools scanned" handshake, and the scanner's own stderr no longer merges into the allowlisted
  results file where a benign line could forge the verdict.
- ✅ **Widened CI templating-injection gate (WM-14)** — `ci-invariants` now also covers single-line
  `run:` scalars and the `github.event.pull_request/issue/comment/review/commit` free-text lanes.
- ✅ **Container capability boundary (SEC-10)** — SDK-only tools detect the missing binary and return
  an explicit "use the global tool" message instead of an opaque crash. See
  [`docs/security.md` § Container capability boundary](../security.md#container-capability-boundary-sec-10).

## 5. Acknowledged residual gaps

- ❌ **Symlink-defense tests are silent no-ops on an unprivileged local Windows dev machine** — every symlink-based regression test (`PathGuardTests`, `SafeWorkspaceWriterTests`, `InitCommandTests`, `NewAgentToolMcpTests`, `NewCommandTests`, `PullRequestAuditToolTests`, `ValidateOverridePathTests`, and others) catches `File.CreateSymbolicLink`'s `UnauthorizedAccessException`/`IOException` and returns early when the OS denies symlink creation — which it does by default on Windows without admin rights or Developer Mode. xUnit 2.9.3 (this project's version) has no native dynamic-skip mechanism (`Assert.Skip` is xUnit v3-only), so these early returns report as **Passed**, not Skipped — a contributor running `dotnet test` on an unprivileged Windows machine gets a fully green symlink-defense suite that never actually exercised a single symlink. Round-3 review independently confirmed this twice with working proof: (1) instrumenting a test with markers showed the post-symlink-creation assertions are never reached here; (2) reverting `NewCommand.cs`'s F-03 containment preflight to its pre-fix state and re-running its symlink tests showed they **still pass** against the genuinely broken code. **Not a gap on CI** — `ubuntu-latest` runners have symlink privilege by default (confirmed: the full suite passes with real symlink creation under Docker's `mcr.microsoft.com/dotnet/sdk` image, which runs as root), so these tests provide real protection where it matters for merge-blocking. The gap is specifically local-dev false confidence. Properly fixing the *reporting* (not just the underlying protection, which is real) would mean adding a third-party package (`Xunit.SkippableFact` or equivalent) across every affected test file plus a `packages.lock.json` regeneration — judged out of scope for this remediation pass since it touches test files this branch didn't otherwise need to modify and adds a new dependency; flagged here for a dedicated follow-up given three independent findings of the same gap.
- ❌ **No sandbox around `dotnet build` (F-05)** — `MafRunCs0618Hunt` now correctly annotated `ReadOnly=false, OpenWorld=true` so MCP-spec-compliant clients prompt before invoking. True OS sandboxing remains out of scope for an OSS .NET CLI. Users running maf-doctor on untrusted repos should sandbox externally (devcontainer, AppArmor, etc.).
- ❌ **Rewriter pure-syntax matching** — `DefaultAzureCredentialRewriter`/`FanInArgOrderRewriter` semantic-model upgrade deferred to v1.2.x (significant refactor; bounded by `--dry-run` default + idempotence tests).
- ✅ **`ProcessRunner` streaming output cap** — closed in the 2026-07-19 pass (§3.15); memory is now bounded during the read, not just the returned string.
- ✅ **`ProcessRunner` env scrubbing** — closed in the Fable-5 remediation (SEC-09, §4). Credential-like env vars are stripped from every child before spawn.
- ✅ **`PullRequestAuditTool` inline `Process.Start`** — closed in the 2026-07-19 pass (§3.15); now routed through `ProcessRunner.RunGit`'s concurrent-drain implementation, closing both the deadlock shape and the bounded-output gap.
- ❌ **Roslyn-analyzer NuGet signing posture** — not yet documented; track for a future pass.
- ❌ **Commit signing requirement** — maintainer's choice; not enforced today.
- ❌ **Local TOCTOU between path validation and use** — `SafeWorkspaceWriter` (§3.13) narrows but does not eliminate the window between `PathGuard.ValidateContainment` and the actual file open; closing this fully needs directory-relative/open-at filesystem APIs. Requires local write access to the same workspace to exploit — not remotely triggerable via tool arguments.
- ❌ **Full hash-pinning of Python dependencies** — the 2026-07-19 pass exact-pinned every floating pip/pipx install (§3.17), which closes "floating install, no review point," but stops short of `pip --require-hashes`, which needs a maintained hash manifest for every transitive dependency. Version pinning was judged the right scope for this pass; hash-pinning is a further hardening step.
- ⚠️ **SBOM and artifact signing** — *partially* closed. Build **provenance attestation** now ships (SEC-13, §4: `actions/attest-build-provenance` over the `.nupkg`s + tag-on-`main` guard), so a published package is attributable to an exact reviewed commit + workflow run. Still open: a full **SBOM** and NuGet package **signature** (the attestation attests provenance, not a signed SBOM). NuGet.org Trusted Publishing (OIDC) — which would also remove the API key from the `dotnet nuget push` argv — is noted in `release.yml` as the next step.
- ❌ **Bare-name subprocess resolution (F-25)** — `ProcessRunner` spawns `dotnet`, `dotnet-inspect`, `dnx`, and `git` by bare command name (`new ProcessStartInfo("dotnet")`, etc.), resolved via the OS's `PATH` at run time. There is no logging of the resolved absolute path, no pinning to a specific binary, and no version verification before invocation — a `PATH`-planting attack (a malicious `dotnet` earlier on `PATH` than the real one) would run silently. Not remotely exploitable via tool arguments; requires local control over the execution environment's `PATH`, at which point the local machine is already substantially compromised. Round-2 review flagged this specifically because it received zero code change and zero prior mention here, unlike every other deliberately-deferred item on this list — recorded now for that reason, not because the risk profile changed. (The Fable-5 SEC-22 pass narrowed the `git`-specific surface — hostile `.git/config` command-execution via fsmonitor/hooks/pager/ext is now neutralized — but PATH-planting of the `dotnet`/`git`/`dnx` binaries themselves remains unpinned.)

## 6. AGT alignment

[Microsoft Agent Governance Toolkit (AGT)](https://learn.microsoft.com/en-us/agent-framework/agent-governance) published a 26.67% policy-violation rate for red-team prompts against prompt-only-safety systems. We do not rely on prompt-only safety: detection is deterministic (Roslyn syntax trees + curated registry + compiler diagnostics), and writes are gated by preview-by-default + explicit opt-in (F-11, §3.16 — `autofix-all` previews unless `--apply`/`dryRun: false` is passed) plus structural validators (`verify-registry`). The LLM-facing surface (`MafExplain`, Copilot Coding Agent AI-fill) is acknowledged as a residual exposure in §5.

## 7. MCP security scans

**Policy:** we only depend on scanners that are **fully open-source, fully local (no phone-home), and require no third-party account**. This keeps the supply chain small and avoids vendor lock-in.

> **For the user-facing overview** of how we tackle security at the MCP + repo level, see [`docs/security.md`](../security.md). This file is the deeper technical record.

### Note: these are CLI tools, not VS Code extensions

The scanner below is a Python CLI tool installed via `pipx` or `uv`. It is NOT in the VS Code extension marketplace. Looking for it there will find nothing — install via Python tooling as documented below.

### Primary scanner — Cisco mcp-scanner (yara analyzer)

**[Cisco mcp-scanner](https://github.com/cisco-ai-defense/mcp-scanner)** — YARA-based pattern matching for known threat patterns (prompt injection, tool poisoning, credential harvesting, code execution).

- **Maintained by:** Cisco AI Defense
- **License:** Apache-2.0
- **Reach:** 926+ stars, v4.6.0 (April 2026)
- **PyPI:** `cisco-ai-mcp-scanner`
- **Local-only in `yara` mode:** the `api` and `llm` analyzers need API keys (Cisco AI Defense / OpenAI) — we don't use those; the `yara` analyzer runs entirely on-disk

```bash
# Install (requires Python 3.10+ and pipx)
python -m pip install --user pipx
python -m pipx ensurepath
pipx install cisco-ai-mcp-scanner

# Scan our server (yara analyzer only — fully local, no API keys)
mcp-scanner --analyzers yara --format summary --hide-safe \
  stdio --stdio-command=dotnet \
  --stdio-arg=run --stdio-arg=--project --stdio-arg=src/maf-autopilot \
  --stdio-arg=--framework --stdio-arg=net10.0 --stdio-arg=--no-build \
  --stderr-file=/tmp/maf-server.log
```

### Optional cross-check — CSA mcpserver-audit

**[ModelContextProtocol-Security/mcpserver-audit](https://github.com/ModelContextProtocol-Security/mcpserver-audit)** — part of the **Cloud Security Alliance** Model Context Protocol Security initiative. Helps audit MCP servers before adoption. Useful as an independent cross-check when comparing scanner results.

```bash
# See upstream README for install + usage
```

### Recurrence cadence

- **Every PR against `main`** that touches `src/maf-autopilot/**`: `.github/workflows/mcp-scanner.yml` runs the scanner, posts a sticky PR comment, and — as of 2026-07-20 (F-16, §3.17) — **fails the check on any finding**. This was advisory (`continue-on-error: true`) until verified via real scanner output (not just job status) across 5 consecutive clean runs spanning both the PR and cron triggers; see the workflow's rollback playbook if a future false positive needs triage.
- **Weekly cron**: scheduled run as a backstop for findings introduced by upstream YARA-rule / tooling drift even without a code change on our side.
- **Pre-tag** (every major + minor release): re-run manually if any tool was added or modified since the last CI pass; update the point-in-time record below.

### Last scan results

The point-in-time scan snapshot is maintained in one place — **[`docs/security.md`
§ Latest scan results](../security.md#latest-scan-results)** — so the numbers don't
drift between two files. In short: the most recent Cisco mcp-scanner (yara) pass
reported **zero findings**, all tools enumerating cleanly via stdio. The
authoritative *current* status is whatever CI's now-required, hard-failing
mcp-scanner check shows on the latest `main` run (see cadence above).

### Tools we deliberately do NOT use

We evaluated these and chose not to depend on them. The reasoning is explicit so users know the supply chain stays small:

| Tool | Why excluded |
|---|---|
| **[Snyk agent-scan](https://github.com/invariantlabs-ai/mcp-scan)** (originally Invariant Labs mcp-scan, now Snyk) | Requires a `SNYK_TOKEN` from a Snyk account. README confirms it "validates the components, both with local checks **and by invoking the Agent Scan API**" — not local-only; phones home to Snyk's servers. Currently in "Open Preview" (free), but Snyk's commercial tier is paid ($52-$98/dev/month) — post-GA pricing uncertain. Account requirement = vendor lock-in regardless. |
| **[kapilduraphe/mcp-watch](https://github.com/kapilduraphe/mcp-watch)** | Single-maintainer hobby project. Quality may be fine, but a security tool from an unvetted source is itself a supply-chain risk. |
| **Commercial-only scanners** (Pangea, Enkrypt AI, AQtive Guard) | Fine if your org has a license, but we don't gate the open-source release on them. |
| **"MCP Scanner" / "mcp-scan" VS Code extensions** | None of the established scanners ship as VS Code extensions. If you see one in the marketplace, verify the publisher carefully before installing. |
