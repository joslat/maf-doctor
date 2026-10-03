# maf-doctor Workshop — From Zero to Migrated Codebase

> **Four entry points:**
>
> | Audience | What to read | Time |
> |---|---|---|
> | "I have 30 seconds, what is this?" | The animated GIFs at the top of `/README.md` | 30 sec |
> | "Give me a 2-minute live teaser" | [Find the Bug](#find-the-bug-2-minute-audience-teaser) below | 2 min |
> | "Show me the wow in 5-10 minutes" | [⚡ Quick Speedrun](#-quick-speedrun--5-10-minutes-of-wow) below | 5-10 min |
> | "I want the full hands-on tour" | [Full workshop](#full-workshop-50-minutes) below | ~50 min |
> | "I'm going to demo or record this" | [Recording / Demo Guide](#recording--demo-guide) at the end | reference |
>
> **Total document length:** long — but you only ever need ONE section based on your audience.

---

## Find the Bug — 2-minute audience teaser

> **Use this** before the full speedrun to give the audience a visceral sense of what "invisible bugs" means. See [`samples/find-the-bug/`](./find-the-bug/) for the full facilitator script, snippets, and answer key.

Three 20-line C# snippets. Each has ONE subtle MAF anti-pattern. You have 30 seconds to spot all three — no compiling, no looking things up. Then run the toolkit:

```bash
maf-doctor doctor samples/find-the-bug/
```

It finds them in ~850 ms. See the [answer key](./find-the-bug/answer-key.md) for what each bug is and how the toolkit catches it.

**Facilitator note:** This is the fastest way to land the core value proposition — "even senior MAF developers miss these; the toolkit doesn't."

---

## What is this? (the 30-second preamble)

You're looking at a workshop for **maf-doctor** — an AI co-pilot toolkit for the **Microsoft Agent Framework (MAF)**. Three concepts:

- **MAF** is Microsoft's .NET framework for building multi-agent AI workflows (agents that talk to each other, plus a workflow runtime that orchestrates them). Current stable: 1.3.0; previews up to 1.5.0.
- **maf-doctor** is a toolkit (MCP server + Roslyn analyzer NuGet + GitHub Copilot agents + skills) that **audits** MAF code for the silent-failure patterns that compile clean but break at runtime, AND **auto-fixes** the mechanical issues without an LLM in the loop.
- **`samples/maf-1.3-sample/`** is a deliberately-broken FraudClaimsTriage workflow we use to dogfood the toolkit. Every "broken" pattern is on purpose — the workshop walks you through finding + fixing all of them.

**Why a workshop instead of a README:** the toolkit's value is invisible until you watch it find a bug nobody else catches. The workshop is "show, don't tell."

---

## ⚡ Quick Speedrun — 5-10 minutes of "wow"

> **Goal:** minimum time-to-wow. By minute 10 you've installed the toolkit, watched it diagnose a broken codebase (including silent-starvation bugs the build never warns about), and watched it **deterministically clear the mechanical errors** with one command — with the remaining *semantic* issues clearly flagged for the agent phase. **No Copilot Chat. No agents. No setup beyond the .NET SDK.** (Grade A comes later, in the agent-driven Phase D — deterministic auto-fix alone doesn't claim it.)
>
> **Audience:** someone who hasn't installed anything yet and wants the elevator pitch in real time.
>
> **Output:** a 5-10 minute screen-recorded demo OR a live presentation moment.

### Speedrun prerequisite (60 seconds)

You need exactly two things:

```bash
# 1. .NET 8 SDK or later — check:
dotnet --version          # should print 8.x, 9.x, or 10.x

# 2. The maf-doctor global tool:
dotnet tool install -g maf-doctor

# Verify:
maf-doctor --version
```

If you don't have a clone of this repo handy, also:

```bash
git clone https://github.com/joslat/maf-doctor
cd maf-doctor
```

### The 7-beat speedrun (~5 minutes)

Run these in your terminal, one beat at a time. Each beat is 30-60 seconds with narration.

| # | Command | Expected output | What to say |
|---|---|---|---|
| **1** | `cd samples/maf-1.3-sample` | — | _"This is a real MAF 1.3.0 codebase. A small fraud-claims triage workflow. Looks fine. Let's see if it is."_ |
| **2** | `dotnet build` | 4 Warning(s), 0 Error(s) — 1 × `CS0618` + 3 × `MAF001` | _"Builds clean — well, with 4 warnings the developer probably dismissed. The analyzer's already firing on three executor methods. We'll see why in a second."_ |
| **3** | `maf-doctor doctor .` | 🔴 **Grade F** — 7 errors + 3 silent-starvation risks | _"The toolkit grades it: F. Seven anti-pattern errors — and three silent-starvation risks that compile clean and BREAK at runtime. No build warning. No exception. Just silently wrong output."_ |
| **4** | `maf-doctor autofix-all . --dry-run` | human-readable: 4 files would change, per-rule breakdown (add `--json` for machine output) | _"This is what would change — preview only. Four files, across the mechanical rules. Deterministic. No LLM in this loop."_ |
| **5** | `maf-doctor autofix-all . --apply` | human-readable: 4 files changed + next step | _"Apply for real. Roslyn rewriters under the hood — same code path you'd trust in a Microsoft refactor extension."_ |
| **6** | `maf-doctor doctor .` | 🟠 **Grade F** — 4 errors + 3 starvation (down from 7+3) | _"Three mechanical errors gone — deterministically, no LLM. Still F: the rest are semantic — a hard-coded key, shared provider state, and the fan-out starvation bugs — and those need judgment. That's the hand-off to the `@maf-migration` agent (next section), which is what drives the grade to A."_ |
| **7** | `maf-doctor doctor . --plan` | ordered remediation plan | _"And here's the punch list: Phase 1 was the autofix we just ran; Phase 2 is the semantic work, as checkboxes you can paste into a GitHub issue or hand to the agent."_ |

### Visual bonus beat (~30 seconds, optional but high impact)

If you can show your IDE alongside the terminal:

| # | Action | What attendees see | What to say |
|---|---|---|---|
| **8** | `code samples/maf-1.3-sample/` then open `Executors/OsintInvestigator.cs` | Red squiggly under `ValueTask` return type; hover shows `MAF001` with the explanation | _"And it's not just the CLI. The same rule fires write-time — the developer sees the squiggle before they even save the file. Shift-left baked in."_ |

### Reset for the next attendee (~10 seconds)

```bash
git restore samples/maf-1.3-sample/
```

### Why this works as a 5-minute demo

- **Total command count: 7 commands.** No more than what fits on a slide.
- **No Copilot Chat required.** No agent setup. No tokens consumed. Pure CLI.
- **One "whoa" per beat.** Build warnings (beat 2) → grade F (3) → preview (4) → fix (5) → still F but errors cleared 7→4 (6) → plan (7). Each beat adds one piece of evidence; grade A comes later, in the agent flow.
- **Resettable in one line.** Re-run the speedrun on any laptop without setup cost.

---

## Full workshop (50 minutes)

> **Audience:** workshop attendees + maintainers running the toolkit end-to-end on a real MAF 1.3.0 codebase. Covers the agent-driven flow (`@maf-auditor` / `@maf-migration`), the multi-agent surface, and the post-migration tooling.
>
> **What you'll do:** open the `maf-1.3-sample/` codebase standalone in VS Code Insiders, wire up the `maf-doctor` MCP server, walk through each of the 26 MCP tools at least once, run the full migration loop, and finish with a multi-version regression plan.

### Prerequisites

| Tool | Why | Install |
|---|---|---|
| **VS Code Insiders** (or stable) | MCP server + Copilot Chat client | https://code.visualstudio.com/insiders/ |
| **GitHub Copilot subscription** | needed to use `@maf-*` agents and `/maf-*` prompts | any Copilot tier |
| **.NET 8 SDK or later** | builds the sample (sample targets `net8.0`) | https://dotnet.microsoft.com/download |
| **`maf-doctor` global tool** | the MCP server itself | `dotnet tool install -g maf-doctor` |
| _(optional)_ Azure OpenAI deployment | only needed for `--run` mode of the sample; not needed for the workshop | https://portal.azure.com |

Verify the tool is on PATH — install only if missing:

**bash / zsh:**

```bash
if ! command -v maf-doctor >/dev/null 2>&1; then
  dotnet tool install -g maf-doctor
  # If PATH still misses after install: export PATH="$HOME/.dotnet/tools:$PATH"
fi
maf-doctor --version
# → maf-doctor 1.0.0 (or later)
```

**PowerShell:**

```powershell
if (-not (Get-Command maf-doctor -ErrorAction SilentlyContinue)) {
    dotnet tool install -g maf-doctor
}
maf-doctor --version
```

> **Already installed?** `dotnet tool update -g maf-doctor` for the latest alpha.

### Where the workshop fits in the docs

The workshop is the **doing** part. For background, refer back to:
- [`/README.md`](../README.md) — the 30-second pitch + animated demos.
- [`/docs/architecture.md`](../docs/architecture.md) — how the 4 .NET projects fit together.

---

### Phase A — Setup (Steps 1-2, ~5 min)

This phase gets a sample folder open in VS Code with the MCP server wired up. After Phase A you're ready to call any toolkit tool from Copilot Chat.

#### Step 1 — Open the sample STANDALONE in VS Code Insiders

The workshop opens **only the sample folder**, not the full maf-doctor repo. This mirrors how a real customer would experience the toolkit — they don't have the source on their machine, just the installed global tool.

```bash
# From wherever you cloned this repo:
code-insiders samples/maf-1.3-sample/
```

Or via GUI: **File → Open Folder…** and pick `samples/maf-1.3-sample`.

You should see this layout in the Explorer pane:

```
maf-1.3-sample/
├── .editorconfig                       ← demotes MAF001/2/3 to warnings (so build stays green)
├── Agents/
│   ├── DecisionAgent.cs
│   └── IntakeAgent.cs
├── Executors/
│   ├── AggregatorExecutor.cs
│   ├── HistoryInvestigator.cs           ← triggers MAF001
│   ├── NotificationExecutor.cs
│   ├── OsintInvestigator.cs             ← triggers MAF001
│   └── TransactionInvestigator.cs       ← triggers MAF001 + MAF-AP-WF-001
├── Providers/
│   └── CaseContextProvider.cs           ← triggers MAF-AP-CONC-001
├── Workflows/
│   └── FraudClaimsWorkflow.cs           ← triggers MAF130-FAN-IN-001 (CS0618)
├── ChatClientFactory.cs                 ← triggers MAF-AP-SEC-001 + MAF-AP-SEC-002 + MAF-AP-SEC-003
├── Config.cs
├── MafSample.FraudClaims.csproj         ← pinned to MAF 1.3.0; analyzer NuGet wired in
├── Models.cs
├── Program.cs
└── README.md
```

> **Talk-track tip:** point out the 7 files marked with anti-pattern comments. "We don't have to hunt for the bugs; this sample's README documents them. The point is to watch the toolkit FIND them anyway."

#### Step 2 — Wire up the MCP server

Create `.vscode/mcp.json` inside the sample folder:

```json
{
  "servers": {
    "maf-doctor": {
      "type": "stdio",
      "command": "maf-doctor",
      "args": []
    }
  }
}
```

VS Code Insiders auto-detects this file. After saving, open Copilot Chat and verify:

- The `@maf` agent appears in `@`-mention completions.
- The `/maf-audit`, `/maf-migrate`, `/maf-cs0618-hunt` slash commands appear in `/`-completions.
- `maf://constraints`, `maf://guide`, `maf://registry`, `maf://rules`, `maf://skills?name=…` are referenceable as resources.

> **Troubleshooting:** if no MCP is detected, check `View → Output → MCP` for the server log. Most commonly: `maf-doctor` is not on PATH — re-run `dotnet tool install -g maf-doctor` and reopen VS Code.

---

### Phase B — Discovery (Steps 3-5, ~10 min)

This phase finds the bugs. You'll run the doctor + 4 individual scanners + see the analyzer fire write-time. After Phase B you know everything wrong with this codebase.

#### Step 3 — Smell test before the toolkit runs

Build and run the sample to establish a baseline:

```bash
dotnet build
# → 4 Warning(s), 0 Error(s)
```

The 4 warnings:
- 1 × `CS0618` — the deliberate obsolete `AddFanInBarrierEdge(target, sources)` overload.
- 3 × `MAF001` — the 3 fan-out executors returning non-generic `ValueTask`. **This is the analyzer firing at write-time** (the same rule the doctor runs at scan-time, now visible inline).

```bash
dotnet run
# → prints topology + deliberate-anti-pattern list, exits 0
```

> **Talk-track:** _"Four warnings the developer can dismiss. Three of them are silent-starvation risks. The codebase compiles AND runs AND produces wrong output. Without our toolkit, you'd never know."_

#### Step 4 — Ask Copilot Chat for a health grade

Switch to Copilot Chat (still inside the sample folder):

```
@maf doctor — what's the health grade of this codebase?
```

Expected:

```
## 🔴 MAF health grade: F

_7 error(s) + 3 silent-starvation risk(s)_

| Metric | Count |
|---|---:|
| Anti-pattern errors | 7 |
| Anti-pattern warnings | 3 |
| Silent-starvation risks (fan-out handlers) | 3 |

### Top fixes (ordered by impact)

1. [MafValidateFanOut] HandleAsync at Executors/HistoryInvestigator.cs:21 returns ValueTask — fan-out handler must return ValueTask<T> · needs your judgment
   - Why: a fan-out handler that doesn't return ValueTask<T> (or a T) yields no message to the fan-in barrier — aggregation silently runs on partial data, no exception.
   - Fix: change the return type to Task<T>, ValueTask<T>, or IAsyncEnumerable<T>.
2. [MafValidateFanOut] HandleAsync at Executors/OsintInvestigator.cs:25 returns ValueTask — …
3. [MafValidateFanOut] HandleAsync at Executors/TransactionInvestigator.cs:23 returns ValueTask — …

_…and 10 more. Run with `--all` (CLI) or `full: true` (MafDoctor) for every finding._

💡 3 of 13 finding(s) are auto-fixable — apply with `maf-doctor autofix-all . --apply`. The rest need your judgment.
```

> **What `MafDoctor` does under the hood:** composes 4 scanners — anti-pattern (Roslyn + regex), fan-out validator (Roslyn), prompt-lint, and token-cap auditor — into a single weighted grade. The verdict is the single most actionable signal in the toolkit.

#### Step 5 — Drill into each scanner

Each row below is one Copilot Chat prompt. Run them sequentially — each surfaces a different angle.

| # | Ask Copilot | Tool invoked | What you'll see |
|---|---|---|---|
| **5a** | "show me every anti-pattern in this code" | `MafScanAntiPatterns` | 7 errors + 3 warnings; each finding with file + line + rule ID |
| **5b** | "check the fan-out executors for silent-starvation risks" | `MafValidateFanOut` | The 3 investigators flagged — `HandleAsync` returns `ValueTask` not `ValueTask<T>` |
| **5c** | "diagram the workflow topology" | `MafSimulateWorkflow` | Mermaid graph: intake → fan-out → fan-in → decision → notify |
| **5d** | "find any CS0618 warnings the compiler reports" | `MafRunCs0618Hunt` | `WorkflowBuilder.AddFanInBarrierEdge` obsolete overload at `Workflows/FraudClaimsWorkflow.cs:50` |
| **5e** | "what registry entries apply to a codebase pinned to MAF 1.3.0?" | `MafScoreMigrationRisk` (preview) + `MafCompatibility` | Per-version applicable rule list, filtered by the W.A `applies_to_codebases` field |

> **Talk-track moment:** _"Five different lenses on the same codebase. The doctor's grade is the synthesis; each scanner is one underlying signal. You'll be back here a lot — when something breaks in production, you ask 'which scanner caught it?' and follow the trail."_

#### Step 5b — Watch the analyzer fire at WRITE time

The toolkit ships TWO products: the MCP server (scan-time) and a separate **`maf-doctor.Analyzers` NuGet** (write-time). The sample's csproj already references it:

```xml
<PackageReference Include="maf-doctor.Analyzers" Version="1.0.0">
  <IncludeAssets>analyzers; build</IncludeAssets>
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Open `Executors/OsintInvestigator.cs` in VS Code Insiders. You should see a **squiggly red underline** under the `ValueTask` return type, with a hover that says:

> **MAF001:** Method 'HandleAsync' is decorated with [MessageHandler] but returns 'ValueTask' — this produces no downstream message and silently starves the fan-in barrier. Return Task<T> or ValueTask<T> where T is the downstream message type.

That's the **shift-left** angle. Same rule, fires at write time inside the editor — before the developer even saves the file.

> **Why does the sample's `.editorconfig` pin MAF001/MAF002/MAF003 to `warning`?** That is their default (MAF001 was `error` until 2026-10; it cannot see the workflow topology, so it no longer fails builds by default). The pin keeps the workshop sample building even where a parent `.editorconfig` raises them; a consumer that wants CI to fail sets them to `error`.

---

### Phase C — Pre-migration intelligence (Steps 6-7, ~5 min) — NEW post-W

This phase doesn't fix anything yet. It tells you **how bad** the migration will be (risk score) and **what would change** (before/after preview). After Phase C you can decide: deterministic auto-fix, or judgement-driven agent migration?

#### Step 6 — Get a risk verdict (`MafScoreMigrationRisk`)

Before committing to a migration, ask the toolkit how big a job it is.

```
@maf use MafScoreMigrationRisk to score this codebase
```

Expected JSON output (paraphrased):

```json
{
  "verdict": "HARD",
  "score": 17,
  "thresholds": { "easyBelow": 5, "hardAtOrAbove": 15 },
  "breakdown": {
    "antiPatternErrors": 7,
    "silentStarvationRisks": 3,
    "cs0246Count": 0,
    "cs0618Count": 1,
    "cs0117Count": 0
  },
  "recommendation": "1+ day migration. Plan for rework. Prepare a rollback branch BEFORE starting..."
}
```

> **What this proves:** the toolkit doesn't just find bugs — it sets expectations. A senior dev knows when a migration's a one-hour cleanup vs. a multi-day rewrite. This gives every dev that intuition.

#### Step 7 — Preview the auto-fix changes (`MafBeforeAfter`)

Before any file is written, see exactly what would change.

```
@maf use MafBeforeAfter to preview fixes for MAF-AP-SEC-001, MAF-AP-WF-001, MAF130-FAN-IN-001
```

Expected output: a single markdown document with `diff --git`-style hunks for every affected file across the requested rule set. Maintainer reads ONE document and approves/rejects the whole change-set.

> **Why this matters:** without `MafBeforeAfter`, the choice is "run AutoFix blind and trust it" or "open every file by hand and inspect." This is the middle path — the auto-fix is dry-run-style, output is a reviewable diff.

---

### Phase D — Migration (Steps 8-10, ~15 min)

This phase does the work. You'll see TWO paths:
- **Deterministic fast path** — `MafAutoFix --all` (no LLM, ~5 seconds, fixes the mechanical rules).
- **Agent-driven flow** — `@maf-auditor` + `@maf-migration` (Copilot Chat, ~15 minutes, handles the judgement-bound work the auto-fixer can't).

The two are complementary, not competing. After Phase D the sample is on grade A.

#### Step 8 — The deterministic fast path (`MafAutoFix --all`)

Reset the sample to its broken state first (if you ran the auto-fix in Step 7's preview already):

```bash
git restore samples/maf-1.3-sample/
```

Then run the batch auto-fixer from the terminal:

```bash
maf-doctor autofix-all . --apply
```

Expected JSON output:

```json
{
  "dryRun": false,
  "orderOfExecution": [
    "MAF-AP-WF-001",
    "MAF-AP-SEC-003",
    "MAF-AP-SEC-001",
    "MAF130-FAN-IN-001",
    "MAF-AP-CONC-002"
  ],
  "totalDistinctFilesChanged": 4,
  "affectedFiles": [
    "ChatClientFactory.cs",
    "Executors/TransactionInvestigator.cs",
    ...
  ]
}
```

`dotnet build` is now down from 4 warnings → 1 warning (the CS0618 from the fan-in arg-order issue is the only one left, and it's gone after AutoFix wrote the swap — re-run if the build cache is stale).

Re-run the doctor:

```bash
maf-doctor doctor .
```

Should still report **grade F**, but with **errors down from 7 to 4** — auto-fix cleared the mechanical rule families (DefaultAzureCredential, EnableSensitiveData, the `sealed partial` executor, the fan-in arg-order swap, and the sync-over-async warning). The remaining errors are *semantic* — a hard-coded API key and shared `AIContextProvider` state — and the 3 fan-out starvation risks need a return-type change that's a judgment call. Those go to the agent flow (Step 9), which is what drives the grade to A.

> **Talk-track:** _"Five rule families. Four files. Zero LLM calls. Took 800 milliseconds. A CI bot can do this on every PR — no Copilot subscription required, no token costs, fully deterministic. It won't reach grade A on its own — the semantic fixes need the agent — but it clears the mechanical backlog every time."_

#### Step 9 — The agent-driven flow (`@maf-auditor` + `@maf-migration`)

The toolkit handles the **mechanical** rules deterministically (Step 8). But some migrations need **judgement** — e.g. the `CaseContextProvider`'s instance-fields-on-an-AIContextProvider issue can't be safely rewritten without understanding the provider's role in the workflow. That's where the agents come in.

Reset to broken state, then drive the full agent flow:

```bash
git restore samples/maf-1.3-sample/
```

```
@maf-auditor scan this codebase and write a migration plan to docs/migration-plan.md
```

Expected: a markdown plan with one section per finding, each citing the registry entry (e.g. `MAF130-FAN-IN-001`) and showing before/after code. The plan should match the embedded-anti-pattern list in `README.md`.

> **Phase T.6 caveat:** as of 2026-05-13, the registry includes 7 entries marked `applies_to_codebases: "pre-1.0.0"` (the Phase W.A chronology corrections). These describe pre-public-release patterns and CANNOT fire on a 1.3.0 codebase. If the auditor mentions them generally, ignore those rows. See [`samples/maf-1.3-sample/README.md` § Phase T registry corrections](./maf-1.3-sample/README.md#phase-t-registry-corrections-real-findings-from-this-dogfood).

Then execute the plan:

```
@maf-migration execute the plan in docs/migration-plan.md, with dotnet build gates between each task
```

Watch each task land. The agent should:

1. Add `sealed` to `TransactionInvestigator`.
2. Change all 3 investigators' `HandleAsync` returns from `ValueTask` → `ValueTask<InvestigationFinding>`.
3. Flip the `AddFanInBarrierEdge` arg order (sources first, target second).
4. Replace `new DefaultAzureCredential()` with `new ManagedIdentityCredential()`.
5. Remove `EnableSensitiveData = true`.
6. Add `.UseOpenTelemetry()` to the chat-client pipeline.
7. Lift the mutable fields off `CaseContextProvider` into `ProviderSessionState<T>` ← _the agent's judgement value-add; AutoFix can't do this safely_.
8. Eliminate the `.Result` blocking call in `NotificationExecutor`.
9. Wire both branches of the `.Use(...)` middleware.
10. Delete the hard-coded `api-key=…` string literal.

After each task, `dotnet build` should stay green (modulo the deliberate CS0618 which goes away after task 3).

#### Step 10 — Verify

```
@maf doctor — what's the health grade now?
```

Expected:

```
🟢 MAF health grade: A     (or 🟡 B if minor warnings remain)
0 anti-pattern errors, 0 silent-starvation risks
```

```bash
dotnet build
# → 0 Warning(s), 0 Error(s)
```

---

### Phase E — Polish + post-migration (Steps 11-12, ~10 min)

This phase covers what comes AFTER the migration: the second-opinion review + multi-version planning. After Phase E you know how to use the full toolkit, not just the migration loop.

#### Step 11 — Multi-agent retrospective (`@maf-best-practice-reviewer`)

After Step 10 confirms grade A/B, ask the second-opinion agent:

```
@maf-best-practice-reviewer review the migrated codebase and flag what's still suboptimal
```

Expected: the reviewer flags `good-but-could-be-better` patterns the registry doesn't cover — executor IDs that are too generic, agents lacking explicit token caps, OpenTelemetry traces missing custom tags, etc.

This demonstrates the **multi-agent surface**:
- `@maf-migration` fixed the registry-driven anti-patterns (deterministic; mechanical).
- `@maf-best-practice-reviewer` catches the next tier of polish (judgement-bound; experiential).

Together they model the "two-pass review" pattern real teams use.

> **Workshop bonus track (5 min):** pick one of the reviewer's findings, then ask `@maf-migration` to apply it. That's a complete "improvement loop" — and a perfect transition into the closing slide. The toolkit can be invoked iteratively, not just once-and-done.

#### Step 12 — Multi-version migration roadmap (`MafGenerateRegressionPlan`) — NEW W.12

Now that the sample's at MAF 1.3.0 + grade A, imagine you needed to land at MAF 1.5.0. Ask the toolkit:

```
@maf use MafGenerateRegressionPlan from 1.3.0 to 1.5.0
```

Expected output: a Mermaid flowchart roadmap (one node per intermediate version step) + per-step change counts + links to per-version migration guides + execution instructions.

```mermaid
flowchart LR
  v130([1.3.0<br/><i>source</i>])
  v140([1.4.0<br/>4 change(s)])
  v150([1.5.0<br/>2 change(s)])
  v130 --> v140
  v140 --> v150
```

> **Talk-track moment:** _"Most teams have a customer pinned to 1.0 or 1.1 who wants to land at the latest. That's not one migration; it's a chain. The toolkit walks the registry per version, gives you a roadmap with counts at each step, and links the guide. Then you do AutoFix at each hop. The whole chain in one afternoon instead of one week."_

---

### Cleanup / Reset

Throw away all migration changes and return the sample to its broken-for-the-next-attendee state:

```bash
# From repo root (NOT inside the sample)
git restore samples/maf-1.3-sample/
rm -rf samples/maf-1.3-sample/.vscode
rm -f samples/maf-1.3-sample/docs/migration-plan.md
```

(Or just `git stash` if you want to compare before vs. after later.)

---

### Takeaways

- The sample is a **fixed-cost regression artefact** — every future MAF version can be regression-tested against the same sample by re-running the migration loop.
- The toolkit detects **7 distinct anti-pattern errors + 3 silent-starvation risks** from a single ~150-LoC codebase.
- **Two complementary paths:** deterministic (`MafAutoFix --all`, ~5 seconds, no LLM) for the mechanical 80% of rules; agent-driven (`@maf-migration`) for the judgement-bound 20%.
- The whole detect → score → preview → fix → verify → retrospective → multi-version loop runs in **~50 minutes of chat interaction**.
- The 3 registry-drift findings recorded during Phase T are the kind of corrections that only surface from real-world dogfooding — exactly the value-add Phase T was meant to produce.

### Where to go next

- **Fork the sample.** Add your own anti-patterns + re-run the workshop to test new toolkit rules.
- **Try the analyzer NuGet** in your own project: `dotnet add package maf-doctor.Analyzers`.
- **Read the MAF migration guides** in [`/guides/`](../guides/) — `maf-1.3.0`, `maf-1.4.0`, `maf-1.5.0`, plus `maf-current-migration-guide.md` (auto-regenerated).
- **Browse the 7 Copilot Chat agents:** `@maf`, `@maf-auditor`, `@maf-migration`, `@maf-best-practice-reviewer`, `@maf-incident-responder`, `@maf-rollback`, `@maf-onboarding`.

---

## Recording / Demo Guide

> **For maintainers preparing a talk, screencast, or marketing video.**
>
> This guide tells you exactly which recordings to make, how to prepare, and what to say. Four recordings cover the full audience spectrum.

### Recording catalog — what to make, when, where

| # | Recording | Length | Audience | Tool | Output path |
|---|---|---|---|---|---|
| **R1** | `install-cast.gif` | ~30 s | "30-second viewer" | `vhs` (declarative `.tape`) | `docs/assets/install-cast.gif` |
| **R2** | `migration-cast.gif` | ~30 s | "30-second viewer" | `vhs` (declarative `.tape`) | `docs/assets/migration-cast.gif` |
| **R3** | `quick-demo.mp4` | 5-10 min | "show me wow" | OBS / QuickTime / screen-recorder | wherever you host video |
| **R4** | `full-workshop.mp4` | ~50 min | "conference talk" | OBS / QuickTime | wherever you host video |

The first two are **automatable** (just run the `.tape` scripts). The last two are **live recordings** that need a person.

---

### R1 — `install-cast.gif` (the README's first impression)

**Status.** Source artifact already shipped (Phase W.4). Rendering is Phase X.3.

| Aspect | Detail |
|---|---|
| **Preparation** | (a) vhs + ffmpeg + ttyd installed. (b) `maf-doctor` global tool installed on your machine. (c) The repo cloned. |
| **Before state** | Terminal at repo root, no maf-doctor installed (or version unverified). |
| **What to do** | `bash scripts/make-cast.sh install` |
| **What to say** | _(no narration — the .tape file is autonomous)_ |
| **After state** | `docs/assets/install-cast.gif` rendered (~500 KB - 2 MB). |
| **Where it goes** | Already embedded in `/README.md` between header and feature list. |
| **Re-render when** | `docs/assets/install-cast.tape` changes, OR the toolkit's CLI surface changes meaningfully. |

---

### R2 — `migration-cast.gif` (the auto-fix loop)

**Status.** Source artifact shipped this session (#8). Rendering is Phase X.3.

| Aspect | Detail |
|---|---|
| **Preparation** | Same as R1 + `samples/maf-1.3-sample/` exists in the cloned repo. |
| **Before state** | Terminal at repo root. Sample is in its broken-for-attendee state (`git restore` if needed). |
| **What to do** | `bash scripts/make-cast.sh migration` |
| **What to say** | _(no narration — .tape is autonomous)_ |
| **After state** | `docs/assets/migration-cast.gif` rendered. |
| **Where it goes** | Already embedded in `/README.md` below install-cast. |
| **Re-render when** | `docs/assets/migration-cast.tape` changes, OR `MafAutoFix --all`'s output JSON shape changes. |

---

### R3 — `quick-demo.mp4` (5-10 min "show me wow" — RECOMMENDED to record)

**Status.** Script is the [Quick Speedrun](#-quick-speedrun--5-10-minutes-of-wow) section above. Not yet recorded.

| Aspect | Detail |
|---|---|
| **Length target** | 5-10 minutes (try for 7) |
| **Audience** | Conference attendee at a booth, prospect on a sales call, manager getting a 1-on-1 demo |
| **Tools needed** | OBS Studio (or QuickTime on macOS) for screen-record; a mic for voiceover; a clean shell + a code editor side-by-side |
| **Preparation (~10 min)** | (a) Fresh terminal session in `samples/maf-1.3-sample/` (sample in broken state). (b) VS Code Insiders open with `Executors/OsintInvestigator.cs` already focused. (c) Have the 7-beat script open on a second monitor for reference. (d) Run through it once dry to time yourself. |
| **Before state** | Terminal: empty prompt at `samples/maf-1.3-sample/`. VS Code: OsintInvestigator.cs visible, MAF001 squiggly already showing on the `ValueTask` return type. |
| **What to do** | Follow the 7-beat speedrun above, in order. Each beat ~30-60 seconds with voiceover. |
| **What to say** | The "What to say" column of the speedrun table. Adapt the wording to your voice. |
| **After state** | Terminal: doctor reports grade **F** — 4 errors (down from 7) + 3 starvation; the remaining errors are semantic, so Grade A is the next-section agent flow, NOT the autofix. VS Code: the OsintInvestigator.cs MAF001 squiggle is STILL there (fan-out starvation needs judgment, not auto-fix). |
| **Cleanup (~30 sec)** | `git restore samples/maf-1.3-sample/` then refresh VS Code so the squiggles come back for the next take. |
| **Where it goes** | YouTube / Loom / Mux / wherever you host. Embed in `/README.md` as a "Watch the 7-minute demo" link below the GIFs. |

**Script outline (paste into your recording app's notes):**

```
[0:00 — 0:30]  Hook
  "Microsoft Agent Framework is great. But MAF code is famously easy to
  ship with silent bugs — workflows that compile, run, and produce wrong
  output. I want to show you a toolkit that catches them."

[0:30 — 1:30]  Beat 1-2 — orient the sample + dotnet build
  cd samples/maf-1.3-sample
  dotnet build
  "Looks fine. Four warnings the developer probably dismissed. Three of
   them are silent-starvation risks — we'll see what that means."

[1:30 — 3:00]  Beat 3 — the doctor
  maf-doctor doctor .
  "Grade F. Seven anti-pattern errors. Three silent-starvation risks.
   These compile clean and break at runtime. No build warning, no
   exception, just silently wrong output. The toolkit found them all
   in 850 milliseconds."

[3:00 — 4:00]  Beat 4 — preview
  maf-doctor autofix-all . --dry-run
  "This is what would change — preview only. Four files, five rule
   families. Deterministic — no LLM in this loop."

[4:00 — 5:00]  Beat 5 — fix for real
  maf-doctor autofix-all . --apply
  "Apply for real. Roslyn rewriters — same machinery a Microsoft refactor
   extension would use."

[5:00 — 6:00]  Beat 6 — verify
  maf-doctor doctor .
  "Still Grade F — but three mechanical errors are gone, 7 down to 4,
   deterministically, in under 30 seconds. The rest are semantic — a
   hard-coded key, shared provider state, the fan-out starvation bugs —
   and THOSE are the hand-off to the @maf-migration agent, which is what
   drives the grade to A in the next section."

[6:00 — 7:00]  Visual bonus — VS Code analyzer
  (switch to VS Code; the MAF001 squiggle on the ValueTask return is
   STILL there — this is the write-time DETECTION angle, not a fix; the
   fan-out starvation is one of the semantic bugs the agent handles)
  "Same rule fires write-time too. Your developer sees the squiggle
   before they save the file. Shift-left baked in."

[7:00 — 7:30]  Close
  "maf-doctor 1.3.0 on NuGet. Install with one command. Audits any
   MAF codebase. github.com/joslat/maf-doctor."
```

**Recording tips:**
- **Single take + heavy editing > many takes + light editing.** Use editing to trim ums and silences; don't try to read the script perfectly first time.
- **Type slowly.** Audience needs to follow what you're typing. 50-80 chars/sec feels natural; 100+ is too fast.
- **Hand-zoom into the terminal output** when the grade letter appears. The "F" and "A" should fill the screen for ~2 seconds each.

---

### R4 — `full-workshop.mp4` (50 min conference talk)

**Status.** Not yet recorded. Lowest priority of the four (most niche audience).

| Aspect | Detail |
|---|---|
| **Length target** | ~50 minutes (matches the Full Workshop section above) |
| **Audience** | Conference attendees, technical training, university course |
| **Tools needed** | Same as R3 + a presentation slide deck for chapter transitions |
| **Preparation (~30 min)** | (a) Render R1 + R2 first — embed them as intro before live demo starts. (b) Set up VS Code Insiders + Copilot Chat with valid subscription. (c) Test the agent loop end-to-end at least twice to catch flakes. (d) Have the [Full Workshop](#full-workshop-50-minutes) section open as your speaker notes. |
| **Before state** | Slide 1: "maf-doctor Workshop — From Zero to Migrated Codebase" + your name. |
| **What to do** | Walk through Phase A → B → C → D → E in order. Stop after each Phase for Q&A (~2 min each). Total: ~40 min content + ~10 min Q&A. |
| **What to say** | The "Talk-track" callout boxes throughout the workshop sections. They're written as one-liners you can read verbatim or paraphrase. |
| **After state** | Final slide: "Try it: `dotnet tool install -g maf-doctor`; github.com/joslat/maf-doctor; questions?" |
| **Where it goes** | YouTube / conference recording platform. |

**Tip:** if recording over 50 minutes, plan **deliberate dead air at chapter transitions** (between Phase A → B, B → C, etc.) so your editor can cut cleanly. ~3 seconds of silent screen between phases makes editing 10× easier.

---

### Cross-recording assets to prepare ONCE

| Asset | Used by | Where |
|---|---|---|
| **A clean shell theme** (dark Dracula or similar) | R1, R2, R3, R4 | Configure your terminal app before any recording |
| **A presentation-mode `code-insiders` window** (large font, hidden minimap) | R3, R4 | VS Code: `Ctrl+,` → Zen Mode + font size 18+ |
| **Sample's broken state confirmed** (`git status` is clean, `dotnet build` shows the 4 warnings) | R3, R4 | Once per recording session |
| **`maf-doctor --version` shows the right alpha** | R1, R3, R4 | Once per recording session |
| **A clean recording folder** with no other tabs/notifications/dock icons visible | R3, R4 | OS-level setup |

---

License: MIT. Workshop content reusable; the sample's deliberate anti-patterns are NOT.
