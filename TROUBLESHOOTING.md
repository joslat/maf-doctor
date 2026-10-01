# Troubleshooting maf-doctor

If you hit something that isn't here, please open an issue with the failing command and its output — the troubleshooting catalog grows from real reports.

## Installation

### `dotnet tool install --global maf-doctor` fails with "no compatible packages"

`maf-doctor` ships **stable** on NuGet, so the plain command works (no `--prerelease` flag needed):

```bash
dotnet tool install --global maf-doctor
```

If you still hit "no compatible packages," your feed config is probably filtering nuget.org — confirm `dotnet nuget list source` includes `https://api.nuget.org/v3/index.json`. (The old `maf-autopilot` package is deprecated — install `maf-doctor`.)

### `dotnet tool install` succeeds but `maf-doctor` is not on PATH

`dotnet` installs global tools to `~/.dotnet/tools` (Linux/macOS) or `%USERPROFILE%\.dotnet\tools` (Windows). Add that directory to `PATH`:

```bash
# Linux/macOS — add to ~/.bashrc or ~/.zshrc
export PATH="$PATH:$HOME/.dotnet/tools"

# Windows — PowerShell, persists across sessions
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";$env:USERPROFILE\.dotnet\tools", "User")
```

### Update / upgrade (and the "file is locked" error)

```bash
dotnet tool update --global maf-doctor
dotnet tool uninstall --global maf-doctor   # if you need to remove it
```

> ⚠️ **Update fails with "The file is locked by another process"?** Your editor keeps the `maf-doctor` MCP server **running**, which holds the tool binary, so `dotnet tool update` can't replace it. Stop every running instance first, then update:
>
> ```powershell
> # Windows — kills all running instances, then update
> Get-Process maf-doctor -ErrorAction SilentlyContinue | Stop-Process -Force
> dotnet tool update --global maf-doctor
> ```
> ```bash
> # macOS / Linux
> pkill -f maf-doctor; dotnet tool update --global maf-doctor
> ```
>
> If you have **both** VS Code and Claude Code open, each holds its **own** lock — the `Stop-Process`/`pkill` commands above kill every instance (more reliable than closing editors one at a time). The alternative is to close **all** editors running the server (or disable it in each), update, then reopen.
>
> **After updating:** re-run `maf-doctor init` to refresh the steering sidecars, **and reload the editor / MCP server** so it loads the new binary — VS Code: `Developer: Reload Window`; Claude Code: reload the project. A bare process restart isn't enough: the MCP host caches tool/resource descriptors, so without a full reload you'll keep talking to the old version ("I upgraded but nothing changed").

## `maf-doctor init`

### "⚠ .vscode/mcp.json exists but could not be parsed as JSON"

Your existing `.vscode/mcp.json` has a syntax error (or trailing comments, which are not valid JSON). `init` has backed it up to `.vscode/mcp.json.bak.<UTC-timestamp>`. Inspect the backup, fix the JSON manually, then re-run `maf-doctor init`.

### "ℹ .github/copilot-instructions.md already exists — not overwriting"

`init` won't overwrite an existing file by design — you may have hand-edited it. To regenerate from scratch: delete the file and re-run `init`.

### `init` leaves stale `.bak.*` files

Currently `init` does not clean its own backups. Safe to delete them yourself once you've recovered any needed content.

### `mcp.json` had JSON-with-comments — was it backed up?

VS Code officially treats `mcp.json` as JSONC: `//` line comments, `/* */` block comments, and trailing commas are valid. `maf-doctor init` uses lenient JSONC parsing — your comment-bearing file is preserved untouched, no `.bak` is created, and the `maf-doctor` server entry is merged in. If you see a `.bak.*` despite using only valid JSONC, you're on an older build — update with `dotnet tool update --global maf-doctor`.

## MCP server not visible in Copilot Chat

### "Server: maf-doctor is not connected"

1. Open the **Output** panel → channel **MCP**. If startup failed, the stack trace is there.
2. Confirm `.vscode/mcp.json` lists `maf-doctor` under `servers`. Run `maf-doctor init` if not.
3. Confirm the command resolves: in a terminal, run `maf-doctor --version` (or just `maf-doctor` and Ctrl-C). If "command not found," go back to the PATH section above.
4. Reload VS Code (`Developer: Reload Window`). MCP servers are started on workspace load.

### Tools appear but resources don't (or vice versa)

Restart VS Code. The MCP host caches resource/tool descriptors aggressively; a stale cache survives the server process being restarted. A full window reload clears it.

### Tool name confusion: `maf_api_safety` vs `MafApiSafety`

The SDK emits PascalCase from C# method names. Some older doc snippets used snake_case (`maf_api_safety`). Both refer to the same tool. The PascalCase form is what the model sees and invokes — use it when writing prompts.

## `dotnet-inspect` issues

### `dotnet-inspect 0.9.1 diff` fails with NuGet rate limit / 429

NuGet's flat-container API rate-limits aggressive callers. Retry after 60s. If it persists, clear NuGet's local caches:

```bash
dotnet nuget locals all --clear
```

### `dotnet-inspect` doesn't show `[Obsolete]` overloads

**Use the repository's exact v0.9.1 pin.** v0.7.8 ([release notes](https://github.com/richlander/dotnet-inspect/releases/tag/v0.7.8)) first closed [issue #316](https://github.com/richlander/dotnet-inspect/issues/316); earlier versions miss obsoletions at the overload level. Check your version:

```bash
dotnet-inspect --version
```

If you're using `dnx dotnet-inspect` (no version pin), it picks up the latest. Pin to `@0.9.1` for reproducible CI and parity with the release watcher.

### The compiler reports CS0618 but `dotnet-inspect` says the API is fine

This is expected and *not* a bug in either tool. Three cases where only the compiler sees obsoletion:

1. **Transitive** — your API calls something that calls something that's obsolete.
2. **Overload-resolution** — your literal types happened to bind to a more-specific, obsoleted overload.
3. **Project-local** — your team marked an internal type `[Obsolete]`. Static inspection of the NuGet package can't see this.

Use `cs0618-hunter` (the compiler-based skill) for runtime ground-truth. Use `dotnet-inspect` for pre-build static checks against published packages.

## Migration loop

### `@maf-migration` agent appears to hang on a task

Three usual causes:
1. **`dotnet build` is itself hung** (e.g., MSBuild lock contention with a running VS Code Build task). Open a terminal and try `dotnet build` yourself — if it hangs, the agent is correctly stuck waiting.
2. **A task hit a real blocker the agent doesn't know how to resolve.** Read the latest message in the chat; the agent will usually report what it tried. Override by editing `src/docs/migration-plan.md` (mark the task ❌ and add a note).
3. **You've exceeded a chat-session token limit.** Open a fresh chat — the agent reads the plan from disk on every turn, so it picks up where you left off.

### "Task marked verified but the build is still failing"

The agent has a strict golden rule: never mark verified without a passing build. If you see this, something edited the plan directly. Re-run `dotnet build`, and either fix the underlying problem or revert the verification mark.

### `dotnet-inspect` is not installed in the migration loop

Install the repository-supported tool version globally:

```bash
dotnet tool install --global dotnet-inspect --version 0.9.1
```

The MCP tools verify that PATH installation before every diff, accepting `0.9.1` with optional build metadata. A missing, mismatched, or unverifiable tool fails closed; their opt-in `dnx` fallback resolves exact `dotnet-inspect@0.9.1`.

## Self-update workflows (GitHub Actions)

These workflows keep the repo in sync with MAF:

- **MAF Release Watcher** (`maf-release-watcher.yml`) — Thursdays 06:00 UTC. Detects the next stable MAF on NuGet (oldest pending first), diffs every package surface in `.github/maf-package-surfaces.json`, records the train in `.github/maf-train-lock.json`, and opens a scaffold PR on `release-watcher/maf-X.Y.Z`. Only one watcher PR is open at a time; an open one warns after 7 days and fails the watcher after 14. Major bumps open a tracking issue instead.
- **Fill a MAF release-watcher scaffold** (`maf-registry-fill.md`, an agentic workflow) — runs when the scaffold PR opens, fills the TODOs in Actions, runs the release checklist plus the obligations gate, and pushes to the PR branch (data files only).
- **MAF AI-Fill PR Verify** (`maf-ai-fill-verify.yml`, required check) — the obligations contract, train-lock immutability, REVIEW-sentinel replacement, `verify-registry`, cross-file consistency, and the autonomy envelope (watcher PRs may only touch data paths). Releases without breaking changes then auto-merge; breaking ones wait for review.
- **MAF Knowledge Freshness** (`maf-freshness.yml`) — daily. Compares the MAF release covered on `main` and in the latest release tag with nuget.org (SLO: ≤ 1 release and ≤ 10 days behind), and lists any new `Microsoft.Agents.AI*` package that is not tracked. Keeps a rolling issue open for each and turns the run red.
- **MAF Drift Detector** (`maf-drift-detector.yml`) — Mondays; runs `MafDoctor` and opens/updates a `maf-drift` issue when the grade drops below A.
- **Auto-release** (`auto-release.yml`) — when a watcher PR changes `.maf-version` on `main`, tags the next minor version so `release.yml` tests, canary-installs (install from the packed `.nupkg`, MCP handshake, `verify-registry` on the embedded registry) and publishes it. Disable with `gh variable set AUTO_RELEASE --body false`.
- **Dependabot lockfile repair** (`dependabot-lockfile-repair.yml`) — regenerates NuGet lock files for all target frameworks on Dependabot PRs (Dependabot writes them for one framework only).

### How do I know if a scheduled run failed?

Each workflow has a `notify-on-failure` job: on any failure it opens (or updates) **one rolling issue** titled `🔴 Self-update workflow failed: <name>`, labelled `maf-release`. The issue names the failure class, the first failure, the streak, and the matching section of [`docs/runbooks/self-update.md`](docs/runbooks/self-update.md); from the second consecutive failure it is labelled `p0` and mentions the owner. Close the issue once the cause is fixed.

**Optional belt-and-suspenders:** enable *Settings → Notifications → "Send notifications for failed workflows you've created"* for a native email. It only emails on the **first** failure of a streak, which is why the issue-based notifier above is the primary signal.

### Watcher runs but nothing changes on `main`

The MAF version on NuGet hasn't moved — the watcher exits cleanly when `.maf-version` already equals the latest stable NuGet release. Check the `check-for-new-maf-release` step for `has_update=false`.

### Scheduled run fails immediately on the NuGet check with `IndentationError`

You're on a build predating the task-1.1 fix. The NuGet version filter is now a real module, `.github/scripts/get_latest_stable.py`; pull `main`.

### "Release notes not available" in the generated guide

Release notes are fetched from `microsoft/agent-framework` (tags `dotnet-X.Y.Z`, falling back to `vX.Y.Z`). If MAF hasn't published notes for that tag yet, a placeholder is used — harmless.

### Drift detector keeps reporting grade F / re-opening the same issue

The drift detector excludes `samples/`, `*.Tests/`, and `find-the-bug/` (the intentional anti-pattern bait), so a drift grade below A reflects **product** code — treat a re-opening issue as a real regression and run `maf-doctor doctor . --exclude samples/ --exclude .Tests/ --exclude find-the-bug/` locally to reproduce.

## CI

### Tests fail locally but pass in CI (or vice versa)

Check the test SDK version drift — `src/maf-autopilot.Tests/maf-autopilot.Tests.csproj` pins specific versions for `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `coverlet.collector`. If `dotnet restore` resolves different versions locally, your `nuget.config` is overriding.

### The test suite (~1,200 tests as of 2026-07) times out on Windows

xUnit's parallel runner respects logical-CPU count. On constrained machines, force serial:

```bash
dotnet test src/maf-autopilot.Tests/maf-autopilot.Tests.csproj -- xunit.parallelizeAssembly=false xunit.parallelizeTestCollections=false
```
