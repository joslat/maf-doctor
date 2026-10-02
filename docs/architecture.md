# Architecture

> **Last updated:** 2026-06-27 — v1.5.0: Semantic Kernel → MAF cross-framework migration.

This document explains what each piece of `maf-doctor` is, how the pieces fit together, and where the structural choices were deliberate (vs. accidental).

---

## What is `maf-doctor`?

A toolkit that turns **GitHub Copilot Chat into a Microsoft Agent Framework (MAF) expert** for the lifetime of a .NET project.

It ships through **four channels**:

1. **MCP server (NuGet tool)** — the main product. 27 [Model Context Protocol](https://modelcontextprotocol.io/) tools that Copilot can invoke during chats: anti-pattern scanner, fan-out validator, workflow simulator, scaffolders, PR auditor, cross-framework (Semantic Kernel → MAF) migration scanner, etc.
2. **MCP server (Docker)** — same product, containerised on GHCR (multi-arch amd64+arm64), for users without a .NET SDK on the host.
3. **Roslyn analyzer (separate NuGet)** — `maf-doctor.Analyzers`. Ships 3 write-time rules (MAF001–003) into any consumer project's compiler. Independent of the MCP server.
4. **Skills + agents (GitHub-native)** — 15 SKILL.md files + 8 agent personas + 3 always-loaded instruction files. Loaded by GitHub Copilot Chat directly from `.github/`.

The MCP server, the analyzer NuGet, and the skill bundle are independently shippable. A user can adopt any one, two, or all three.

---

## Component map

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          maf-doctor repo                                 │
│                                                                          │
│  /Directory.Packages.props  ← central package mgmt (CPM)                │
│  /Directory.Build.props     ← shared compiler settings                  │
│  /global.json               ← SDK pin: 8.0.100 + rollForward: latestMajor│
│                                                                          │
│  ┌─────────────────────────┐   ┌────────────────────────────────────┐   │
│  │  /src/maf-autopilot/    │   │  /src/maf-autopilot.Analyzers/    │   │
│  │  ── MCP server          │   │  ── Roslyn analyzers              │   │
│  │  ── net8.0;net9.0;net10.0   │   │  ── netstandard2.0 (compiler host)│
│  │  ── NuGet: maf-doctor   │   │  ── NuGet: maf-doctor.Analyzers   │   │
│  │  ── Docker: GHCR (net8) │   │  ── 3 rules (MAF001/002/003)      │   │
│  └────────┬────────────────┘   └────────┬──────────────────────────┘   │
│           │ ProjectReference (none)     │ ProjectReference (none)        │
│           │ EmbeddedResource:           │ AdditionalFiles:               │
│           │  ../../.github/skills/*.md  │  AnalyzerReleases.Shipped.md   │
│           │  ../../.github/instructions/                                  │
│           │  ../../guides/maf-1.3.0…    │                                │
│           │                                                              │
│  ┌────────▼────────────────┐   ┌────────▼──────────────────────────┐   │
│  │  /src/maf-autopilot.Tests│  │ /src/maf-autopilot.Analyzers.Tests│   │
│  │  ── 978 xUnit tests       │  │ ── 15 xUnit tests                │   │
│  │  ── net8/9/10 (x3 runs)  │  │ ── net8/9/10 (x3 runs)            │   │
│  │  ── ProjectReference     │  │ ── ProjectReference               │   │
│  │     maf-autopilot.csproj │  │    maf-autopilot.Analyzers.csproj │   │
│  └─────────────────────────┘   └────────────────────────────────────┘   │
│                                                                          │
│  /.github/                                                               │
│  ├── agents/        8 agent personas (Copilot Chat loads via @-mention) │
│  ├── instructions/  3 auto-loaded instruction files (applyTo: globs)    │
│  ├── skills/        14 skill docs (SKILL.md per dir)                    │
│  ├── scripts/       2 Python CI helpers (called by workflows)           │
│  ├── workflows/     6 GitHub Actions                                    │
│  └── dependabot.yml security: pin SHAs, update weekly                   │
│                                                                          │
│  /docs/             this folder — index.md catalogues it                │
│  /guides/           per-version migration guides (1.3.0, 1.4.0, 1.5.0,  │
│                     plus auto-generated maf-current-migration-guide.md) │
│  /samples/          dogfood fixtures — maf-1.3-sample/ is the A.8       │
│                     unblocker (Phase T): a deliberate-anti-pattern      │
│                     MAF 1.3.0 codebase the toolkit is exercised against │
│  /Dockerfile        multi-stage build for the GHCR image (net8 LTS base)│
│  /maf-autopilot.sln solution file referencing the 4 .NET projects       │
│                     (the sample is intentionally NOT in the sln)        │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## What each .NET project IS

### `maf-doctor` (the MCP server)

**Path:** `/src/maf-autopilot/`
**Target frameworks:** `net8.0;net9.0;net10.0` (multi-target — NuGet picks the best TFM at install time)
**Output:** a single executable (`maf-doctor.dll`) packaged as a dotnet global tool + a Docker image (Dockerfile uses `net8.0` base for smallest container).
**Dependencies:** `ModelContextProtocol` 1.2.0, `Microsoft.CodeAnalysis.CSharp` 4.11.0, `YamlDotNet` 16.2.1, `Microsoft.Extensions.Hosting` 10.0.7 (the 10.0.x line internally ships net8/net9/net10 TFM assemblies). Versions pinned centrally in `Directory.Packages.props`.

**What it does:**
- Speaks MCP over stdio (when run as a tool) or stdio-in-container (when run via Docker).
- Exposes **28 tools** (`[McpServerTool]`-decorated methods under `Tools/*.cs`) — see the README for the live catalogue.
- Exposes **7 resources** (`maf://guide`, `maf://constraints`, `maf://registry`, `maf://rules`, `maf://help`, `maf://migrate-from?source=...`, `maf://skills?name=...`).
- Exposes **11 prompts** (`maf-audit`, `maf-migrate`, `maf-remediate`, `maf-migrate-from`, `maf-cs0618-hunt`, `maf-review`, `maf-debug`, `maf-explain-finding`, `maf-scaffold`, `maf-onboarding`, `maf-help`).
- Provides **CLI subcommands**: `init` (wires `.vscode/mcp.json`), `doctor` (health grade), `autofix-all`, `new agent` / `new executor` (scaffolders), `migrate-scan` (SK→MAF inventory), `badge`, `verify-registry` / `registry-extract` (CI helpers).

**Embeds at build time** (via `<EmbeddedResource>`): the 15 SKILL.md files, the hard-constraints instruction file (served at `maf://constraints`), the 4 steering snippets (Copilot / Claude / AGENTS / Cursor), the obsolete-API registry YAML, the migration guide, and the SK→MAF mapping. This is why the runtime container only needs a single .dll — everything ships inline.

### `maf-doctor.Analyzers` (the Roslyn analyzer NuGet)

**Path:** `/src/maf-autopilot.Analyzers/`
**Target framework:** **netstandard2.0** (mandatory — Roslyn analyzers load into the compiler host, which targets netstandard2.0).
**Output:** an analyzer NuGet that drops a DLL into consumer projects' `analyzers/dotnet/cs/`.
**Dependencies:** `Microsoft.CodeAnalysis.CSharp` 4.11.0, `Microsoft.CodeAnalysis.Analyzers` 3.11.0 (both `PrivateAssets="all"`).

**What it does:** ships 3 diagnostic rules that fire at write-time in any consumer project:
- `MAF001` — fan-out handler must return `Task<T>` / `ValueTask<T>` / `IAsyncEnumerable<T>` (else silent fan-in starvation).
- `MAF002` — avoid `DefaultAzureCredential` in production code.
- `MAF003` — avoid `EnableSensitiveData = true` outside test code.

**Critically: it has ZERO dependencies on the MCP server.** A consumer can install just the analyzers without ever touching `maf-doctor` the tool.

### `maf-autopilot.Tests`

**Path:** `/src/maf-autopilot.Tests/`
**Target frameworks:** `net8.0;net9.0;net10.0` — each test is exercised 3 times, once per TFM.
**Output:** an xUnit test assembly (not packed; never published).
**Dependencies:** `ProjectReference` to `maf-autopilot`; `xunit` 2.9.2; `Microsoft.NET.Test.Sdk` 17.12.0; `Microsoft.CodeAnalysis.CSharp` 4.11.0 (for the compile-validation tests). Versions pinned centrally.

**Current size:** 978 tests. Coverage spans every MCP tool (entry + pure-core), every scaffolder template, security regression pins, registry haystack search, the doc↔code drift guards, and the auto-update additivity guarantees.

### `maf-autopilot.Analyzers.Tests`

**Path:** `/src/maf-autopilot.Analyzers.Tests/`
**Target frameworks:** `net8.0;net9.0;net10.0` (test host can target any of net8/9/10 even though the analyzer itself is netstandard2.0).
**Output:** an xUnit test assembly.
**Dependencies:** `ProjectReference` to `maf-autopilot.Analyzers`; `xunit` 2.9.2; `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit` 1.1.2. Versions pinned centrally.

**Current size:** 15 tests — one happy-path + one negative case per rule, plus rule-metadata validation.

---

## Naming conventions

| Surface | Convention | Example |
|---|---|---|
| MCP tools | `Maf<Verb><Object>` (PascalCase) | `MafScanAntiPatterns`, `MafDoctor` |
| MCP resources | `maf://<concept>` | `maf://constraints` |
| MCP prompts | `maf-<topic>` (hyphenated) | `maf-audit` |
| Analyzer rules | `MAF<NNN>` (three digits) | `MAF001` |
| Anti-pattern scanner rules | `MAF-AP-<AREA>-<NNN>` | `MAF-AP-SEC-001` |
| Registry entries | `MAF<MAJ><MIN><PATCH>-<AREA>-<NNN>` | `MAF130-FAN-IN-001` |
| Agents | `maf-<role>.agent.md` (no `-agent` stem) | `maf-incident-responder.agent.md` |
| Instructions | `maf-<topic>.instructions.md` | `maf-constraints.instructions.md` |
| Skills | `maf-` prefix for MAF-specific skills; no prefix for skills wrapping external tools by their own name | `maf-anti-pattern-scanner` vs `dotnet-inspect` |
| CLI subcommands | natural English | `init`, `doctor`, `new agent`, `registry-extract` |

See [`/CONTRIBUTING.md`](../CONTRIBUTING.md) §"Skill naming convention" + "Adding an agent" for the rationale.

---

## Build & dependency graph

```
maf-autopilot.sln
    │
    ├── /src/maf-autopilot                    (net8.0;net9.0;net10.0 console / NuGet tool)
    │       └── embeds: .github/skills/*.md, .github/instructions/*.md,
    │                   guides/maf-1.3.0-migration-guide.md,
    │                   .github/skills/maf-obsolete-api-registry/registry.yaml
    │
    ├── /src/maf-autopilot.Analyzers          (netstandard2.0 / NuGet)
    │       └── (no project references — fully standalone)
    │
    ├── /src/maf-autopilot.Tests              (net8.0;net9.0;net10.0 / xUnit)
    │       └── ProjectReference: maf-autopilot
    │
    └── /src/maf-autopilot.Analyzers.Tests    (net8.0;net9.0;net10.0 / xUnit)
            └── ProjectReference: maf-autopilot.Analyzers
```

`dotnet build maf-autopilot.sln` builds all four across all 3 TFMs (the analyzer stays netstandard2.0). `dotnet test maf-autopilot.sln` runs **993 unique tests × 3 TFMs = 2979 test executions** in both test projects. `release.yml` packs `maf-autopilot` and `maf-autopilot.Analyzers` separately; `docker-publish.yml` packages only the MCP server.

---

## Multi-targeting

The MCP server NuGet `maf-doctor` ships TFM assemblies for **net8.0 + net9.0 + net10.0** inside a single `nupkg`. NuGet's TFM resolution selects the best match at consumer install time:

- A user on the .NET 8 LTS runtime installs the `net8.0` binary.
- A user on the .NET 9 STS runtime installs the `net9.0` binary.
- A user on the .NET 10 LTS runtime installs the `net10.0` binary.

This pattern matches the sibling [`AgentEval`](https://github.com/joslat/AgentEval) project (same maintainer, same dependency tree including MAF 1.3.0). Field-tested infrastructure.

**Supporting files at the repo root:**

| File | Purpose |
|---|---|
| `Directory.Packages.props` | Central Package Management (CPM). Every `<PackageReference>` in every csproj drops its inline `Version=` — versions pin here. `CentralPackageTransitivePinningEnabled=true` so a security pin (e.g. a CVE patch on a transitive dep) takes effect even on packages we don't reference directly. |
| `Directory.Build.props` | Shared compiler settings (`LangVersion`, `ImplicitUsings`, `Nullable`). Per-project csprojs may override (the analyzer uses its own `LangVersion=latest` + locked `netstandard2.0`). |
| `global.json` | Pins `sdk.version=8.0.100` with `rollForward: latestMajor` + `allowPrerelease: true`. Any installed SDK ≥ 8.x can build the solution. |

**The Roslyn analyzer (`maf-doctor.Analyzers`) is excluded from multi-targeting.** It MUST stay on `netstandard2.0` — analyzers load into the compiler host (csc.exe / VS / OmniSharp), which itself runs on the netstandard2.0 surface. Multi-targeting an analyzer would not change consumer reach (consumer csproj loads the netstandard2.0 DLL regardless of consumer TFM) and would break compatibility with older compiler hosts.

**The Dockerfile is excluded.** Only the multi-target NuGet ships all 3 TFMs; the container can only choose one runtime. Net8 LTS is the smallest, longest-supported runtime image — picked for the smallest container footprint.

**Adding net11 (future):** when .NET 11 ships (Nov 2027), the change is additive: edit `<TargetFrameworks>` in the 3 multi-target csprojs from `net8.0;net9.0;net10.0` to `net8.0;net9.0;net10.0;net11.0`. Drop `net9.0` after its EOL — also additive, non-breaking for net8/10 consumers.

---

## Distribution model

| Channel | What ships | Who consumes | Where |
|---|---|---|---|
| NuGet (server) | `maf-doctor` .NET global tool | Developers installing via `dotnet tool install` | `nuget.org/packages/maf-doctor` |
| NuGet (analyzer) | `maf-doctor.Analyzers` analyzer | Consumer .csproj `<PackageReference>` | `nuget.org/packages/maf-doctor.Analyzers` |
| Docker | Multi-arch image (amd64+arm64) | Developers without .NET SDK on host | `ghcr.io/joslat/maf-doctor:<semver>` |
| Skills-only | The `.github/` directory copied into a consumer repo | Users wanting agents+skills without the MCP server | `git clone` |

---

## Auto-update — additivity

The `maf-release-watcher` workflow runs weekly + on-demand, detecting new MAF releases on NuGet. It processes the **oldest untracked stable release first** and allows only one watcher PR in flight, so multiple upstream releases between cron ticks cannot disappear from the version chain. When a new version (e.g. 1.4.0) is found, it opens an auto-PR that updates the toolkit. **This update must be ADDITIVE — every existing entry, rule, and reference for prior versions survives unchanged.** This invariant is critical: a downstream user upgrading from 1.3 → 1.4 still needs the toolkit's full 1.3 knowledge intact.

### What's additive (the data plane — automated)

| Surface | Additive? | How it's enforced |
|---|---|---|
| `.github/skills/maf-obsolete-api-registry/registry.yaml` | ✅ | `maf-doctor registry-extract` emits draft entries; watcher appends via `>> $REGISTRY`. **Pinned by test** `RegistryExtractCommandTests.Additivity_DraftEntries_AppendedToExistingRegistry_PreserveAllOriginalEntries`. |
| `docs/compatibility-matrix.md` | ✅ | `.github/scripts/update_compat_matrix.py` inserts at top of data section; checks for duplicate version rows (no-op on re-run). Idempotency documented in the script's docstring. |
| `guides/maf-<NEW-VERSION>-migration-guide.md` | ✅ | `.github/scripts/gen_guide_section.py` writes a NEW per-version file. If the file already exists, the AUTO-GENERATED region is overwritten but the `## Human additions` heading and everything below it is preserved. The 1.3.0 guide is never touched. |
| `.maf-version` (tracked version pointer) | ✅ (overwritten cleanly) | One-line file; the watcher writes the next pending stable version. The old value is replaced — but the old version's registry/matrix/guide data stays intact (rows above). |

### What's NOT additive (the code plane — requires a human PR)

| Surface | Why not | What human work is needed when MAF X.Y ships |
|---|---|---|
| Anti-pattern scanner rules (`AntiPatternScannerTool.AllRules`) | Hard-coded in C#. Adding a new rule requires writing a Roslyn syntax walker. | If a new anti-pattern emerges in MAF X.Y, file a new rule with id `MAF-AP-<AREA>-NNN`. |
| Roslyn analyzer rules (`MAF001` / `MAF002` / `MAF003`) | Hard-coded in C#. Analyzer surface is a public API. | If a new write-time enforcement is appropriate, file a new analyzer `MAF004+` with `AnalyzerReleases.Unshipped.md` updated. |
| MCP tools (`[McpServerTool]` methods) | Tool surface only changes via human PR. | If MAF X.Y introduces a pattern worth a new tool, file the tool + its test + its `MafTour` catalogue row. |
| Agent personas | Mostly static markdown with embedded decision trees. | Rarely needs change between minor MAF versions. |

### When MAF X.Y ships — the actual flow

1. **Watcher fires** (weekly Thursday 06:00 UTC cron OR manual `gh workflow run maf-release-watcher.yml`; a manual run can set `maf_version` for a specific version and `push_target` for a safe dry-run against a throwaway branch).
2. **Detection step** reads NuGet's stable-version index and selects the oldest version newer than `.maf-version`. If another watcher PR is open, it exits as a clean no-op until that PR merges or closes; this serializes the backlog.
3. **Major-version check** sets `is_major=true` for X.0 bumps. **Majors are NOT auto-committed** — they escalate to a `maf-release,needs-review` tracking issue (with the breaking-API diffs attached as run artifacts) for human-driven migration. Minor/patch bumps continue automatically.
4. **Package planning + dotnet-inspect diff** resolves and scans the targeted release-critical surface set declared in `.github/maf-package-surfaces.json` (currently ten; this is not a claim of exhaustive first-party package coverage). Exact package versions, lifecycle events, and deterministic artifact names are recorded in `maf-package-plan.json`; every diff is independently validated and uploaded with its result ledger.
5. **`registry-extract` CLI** consumes each validated `--diff-file` (no repeated network diff), emits package-scoped draft YAML entries, and records success/failure plus generated-entry counts for every surface in `maf-registry-extraction-results.json`. The append step de-dupes drafts against existing entry ids; package-scoped REVIEW drafts cover extraction failures and structural changes outside the extractor's removed/signature recipes.
6. **Append step** appends the de-duplicated drafts to the live `registry.yaml`. A separator comment marks the auto-appended region.
7. **Matrix update** inserts a new top row in `compatibility-matrix.md`.
8. **Guide generation** writes a per-version file `guides/maf-X.Y.0-migration-guide.md` and regenerates the cumulative guide (existing per-version guides untouched).
9. **Commit to `release-watcher/maf-X.Y.Z` and open a PR**. The scaffold never pushes directly to `main`; the bot GitHub App's token (or the PAT fallback) is used so the PR-created event triggers the normal CI workflows.
10. **AI fill**: opening the scaffold PR starts the `maf-registry-fill` agentic workflow, which fills the TODOs on the PR branch; `maf-fill-repair` retries once, then labels the PR `needs-human` for a maintainer to fill directly. Either route is gated by rung-1 `verify-registry` + rung-2 semantic review.
11. **On any failure**, a `notify-on-failure` job opens/updates a `maf-release` tracking issue so the maintainer is alerted (scheduled failures no longer pass silently).

### How additivity is engraved

- **Test pinned**: `RegistryExtractCommandTests.Additivity_DraftEntries_AppendedToExistingRegistry_PreserveAllOriginalEntries` builds a synthetic combined document and asserts every original entry survives.
- **Test pinned**: `Additivity_EmptyDraftSet_LeavesExistingRegistryUntouched` asserts no-op when there's nothing to add.
- **Workflow design**: every modification is `>>` (append) or "new file" — never `>` (overwrite) or `sed -i` over existing entries.
- **Python script docstrings**: each helper script (`gen_guide_section.py`, `update_compat_matrix.py`) explicitly documents idempotency + the protected sections (`## Human additions`).
- **PR review gate**: every scaffold lands through a PR, the required `verify` context checks watcher-managed files, and the maintainer reviews the generated diff before merge.

If any of those guards regresses, this section needs an update to reflect the new reality. **Don't silently break additivity.**

---

## Structural decisions — closed

### `/mcp/` → `/src/` rename (✅ done 2026-05-12)

The repo originally grouped the 4 .NET projects under `/mcp/`. This was non-standard (the dominant .NET convention used by `dotnet/runtime`, `dotnet/aspnetcore`, `dotnet/efcore` is `/src/` for source projects) and misleading (the analyzer NuGet under `/mcp/maf-autopilot.Analyzers/` is NOT an MCP server). The rename landed pre-1.0 to avoid post-release URL/path ossification.

The 4 projects are kept **flat under `/src/`** rather than split into `/src/` + `/tests/`. For this project's scale (4 projects) the extra directory level adds overhead without payoff; the `.Tests` suffix already makes the relationship obvious.

### `.github/scripts/` for CI helpers — kept (not moved)

The 2 Python scripts (`gen_guide_section.py`, `update_compat_matrix.py`) are called exclusively by `.github/workflows/maf-release-watcher.yml`. Moving to a top-level `/scripts/` would suggest general-purpose use (they aren't). Wrapping as skills would be a category error (skills are markdown knowledge, not executable code). Kept in place; their `__pycache__/` is gitignored.

### Should `.github/scripts/` move to `/scripts/`?

**Status: open. Recommendation: keep where it is.**

The 2 Python scripts (`gen_guide_section.py`, `update_compat_matrix.py`) are CI-only — called exclusively by `.github/workflows/maf-release-watcher.yml`. Putting them next to the workflow that calls them keeps the relationship discoverable.

Moving to a top-level `/scripts/` would:
- Suggest they're general-purpose dev scripts (they aren't).
- Decouple them from the workflow that owns them.
- Add a top-level directory for ~70 lines of Python.

**Not a skill, either.** Skills are markdown knowledge files loaded into Copilot's context. These are executable Python. Wrapping them as skills would be a category error.
