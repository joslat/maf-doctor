# MAF 1.3.0 FraudClaimsTriage Sample

```
╔══════════════════════════════════════════════════════════════════════════════╗
║                                                                              ║
║   ⚠️   WARNING — INTENTIONAL ANTI-PATTERN FIXTURE   ⚠️                       ║
║                                                                              ║
║   This codebase is a DELIBERATE collection of MAF 1.3 anti-patterns.         ║
║   Every "broken" pattern here is on purpose. Do NOT copy any of this code    ║
║   into a real project — it's bait for the maf-doctor toolkit to find         ║
║   and fix. The toolkit's job is to detect, plan, and remediate every one     ║
║   of the anti-patterns embedded below.                                       ║
║                                                                              ║
║   If you are migrating a real MAF codebase, see /guides/ for the canonical   ║
║   per-version migration guides, NOT this sample.                             ║
║                                                                              ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

A small but realistic multi-agent **Microsoft Agent Framework 1.3.0** codebase
that deliberately embeds anti-patterns the `maf-doctor` toolkit was built to
find. Used as **Phase T — the A.8 migration-dogfood unblocker**.

**Want a guided run?** See [`samples/workshop.md`](../workshop.md) — a ~50-minute
hands-on walkthrough that opens this sample standalone in VS Code Insiders,
wires up the `maf-doctor` MCP server, and exercises the toolkit
end-to-end.

This sample:

- **Pins MAF 1.3.0 exactly** (not `>= 1.3.0`) so the surface stays stable.
- **Targets `net8.0`** — the lowest TFM `maf-doctor` now supports. A real
  customer with an aged codebase is the realistic Phase T fixture.
- **Builds clean** with one expected `CS0618` warning (the obsolete fan-in
  edge — see below).
- **Is NOT part of `maf-autopilot.sln`.** It restores its own NuGet feed
  (`Microsoft.Agents.AI 1.3.0`); the toolkit's central package versions track
  newer releases.

---

## Topology

```
IntakeAgent                     ← ChatClientAgent (Azure OpenAI)
   │
   ├── (fan-out)
   │
   ├──► OsintInvestigator       ─┐
   ├──► HistoryInvestigator      ├─► AggregatorExecutor ─► DecisionAgent ─► NotificationExecutor
   └──► TransactionInvestigator ─┘
                                  ↑
                                  └── (fan-in barrier)
```

Theme: triage incoming insurance fraud claims. Each investigator scores risk;
the aggregator collects all three; the decision agent decides accept / refer /
reject; the notifier dispatches.

---

## Embedded anti-patterns

The sample triggers the maf-doctor toolkit on **10 anti-pattern errors,
3 warnings, and 3 silent-starvation risks** in `dotnet run --project
src/maf-autopilot -- doctor samples/maf-1.3-sample` (grade: F).

| ID | File | What |
|---|---|---|
| `MAF-AP-SEC-001` / `MAF002` | `ChatClientFactory.cs` | `new DefaultAzureCredential()` |
| `MAF-AP-SEC-002` | `ChatClientFactory.cs` | Hard-coded `"api-key=…"` literal |
| `MAF-AP-SEC-003` / `MAF003` | `ChatClientFactory.cs` | `EnableSensitiveData = true` |
| `MAF-AP-OBS-001` | `ChatClientFactory.cs` | Agent built without `UseOpenTelemetry()` |
| `MAF-AP-CONC-001` | `Providers/CaseContextProvider.cs` | Mutable instance fields on `AIContextProvider` subclass |
| `MAF-AP-CONC-002` | `Executors/NotificationExecutor.cs` | Sync-over-async via `.Result` |
| `MAF-AP-WF-001` | `Executors/TransactionInvestigator.cs` | Executor class missing `sealed` |
| `MAF130-EXEC-001` / `MAF001` | `Executors/{Osint,History,Transaction}Investigator.cs` | Fan-out `[MessageHandler]` returns non-generic `ValueTask` → silent fan-in starvation |
| `MAF130-FAN-IN-001` | `Workflows/FraudClaimsWorkflow.cs` | `AddFanInBarrierEdge(target, sources)` — obsolete overload (CS0618) |
| `MAF130-MIDDLEWARE-001` | `ChatClientFactory.cs` | `.Use(runFunc, null)` — streaming branch bypassed |

---

## How to run

### Dry-run (default) — no LLM call, ~150 ms

```bash
# from the maf-doctor repo root
dotnet build samples/maf-1.3-sample/MafSample.FraudClaims.csproj
dotnet run --project samples/maf-1.3-sample/MafSample.FraudClaims.csproj
```

Builds the workflow, prints the topology + embedded-anti-pattern list, exits 0.
No network calls, no Azure auth needed.

### With LLM (`--run`)

```bash
export AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com
export AZURE_OPENAI_API_KEY=...
export AZURE_OPENAI_DEPLOYMENT=gpt-4o

dotnet run --project samples/maf-1.3-sample/MafSample.FraudClaims.csproj -- --run
```

`--run` is currently a stub — the sample is meant to exercise the migration
toolkit on the **source** not at runtime.

### Have the maf-doctor toolkit audit it

```bash
# Doctor — A through F grade + per-tool detail pointers
dotnet run --project src/maf-autopilot --framework net10.0 -- doctor samples/maf-1.3-sample

# Full anti-pattern scan
dotnet run --project src/maf-autopilot --framework net10.0 -- ...  # via MCP, or use the published tool:
dotnet tool install -g maf-doctor --prerelease
maf-doctor doctor samples/maf-1.3-sample
```

Expected output (current state, 2026-05-13):

```
🔴 MAF health grade: F
10 error(s) + 3 silent-starvation risk(s)

Top fixes (ordered by impact):
  1. HandleAsync at Executors/HistoryInvestigator.cs:19 returns ValueTask — fan-out handler must return Task<T>
  2. HandleAsync at Executors/OsintInvestigator.cs:23 returns ValueTask — fan-out handler must return Task<T>
  3. HandleAsync at Executors/TransactionInvestigator.cs:21 returns ValueTask — fan-out handler must return Task<T>
```

---

## Phase T registry corrections (real findings from this dogfood)

Building this sample surfaced **three drift points between the
`maf-obsolete-api-registry/registry.yaml` and the real MAF 1.3.0 surface**.
These are exactly the kind of corrections Phase T was supposed to produce.

### 1. `MAF130-SESSION-001` references `AgentThread`, which is REMOVED

The registry documents the sync `agent.SerializeSession(session)` anti-pattern
on `AgentThread`, but `AgentThread` is fully removed in 1.3.0 (CS0246, per
the registry's own `MAF130-THREAD-001` entry). Code referencing the obsolete
sync pattern can't compile on 1.3.0 at all — so it's a **pre-1.3.0 customer
codebase pattern**, not a 1.3.0 surface pattern. The registry should reframe
this entry as "migration from pre-1.3 → 1.3" (alongside `MAF130-THREAD-001`)
rather than "obsolete in 1.3."

Why this matters: the dogfood sample initially included a `SessionPersister.cs`
that triggered `MAF130-SESSION-001`. It would not compile. We dropped the file
and surfaced this as Phase T feedback.

### 2. `MAF130-INSTRUCTIONS-001` describes a property that no longer exists

The registry claims `ChatClientAgentOptions.Instructions` exists at the top
level in 1.3.0 but is "silently ignored." Reality: the property was **removed
outright** by 1.3.0 GA (verified with `dotnet-inspect member
Microsoft.Agents.AI@1.3.0 ChatClientAgentOptions`). The only `Instructions`
property in the 1.3.0 type is nested inside `ChatOptions`.

Same downgrade: the entry should describe a pre-1.3.0 surface anti-pattern,
not a 1.3.0 surface anti-pattern.

**Update (2026-10-02):** a binary check across versions showed the property was
already gone before 1.0 GA (present in 1.0.0-preview.251110.2, absent from
1.0.0-rc3 and every 1.x release). The entry now applies to `pre-1.0.0` codebases
with `cs_warning: CS0117`.

### 3. `MAF130-MIDDLEWARE-001` parameter names are wrong

The registry's `example_before` uses named parameters `.Use(runFunc: …,
runStreamingFunc: …)`. The actual `ChatClientBuilder.Use(...)` overload
exists, but its parameter names are **not** `runFunc` / `runStreamingFunc`.
Customer code following the registry's example verbatim would fail
to compile.

The anti-pattern itself is real — passing `null` for the streaming branch
creates a silent bypass — but only positional usage works. The registry
should drop the named-argument convention from the example.

---

## What this sample does NOT exercise

These registry IDs require pre-1.3.0 surfaces (removed types / removed
attributes), so they cannot fire from a 1.3.0 codebase:

- `MAF130-THREAD-001` — `AgentThread` is removed (CS0246)
- `MAF130-A2A-001/002` — A2A registration / `MapA2A` are removed
- `MAF130-STREAM-001` — `InProcessExecution` is removed
- `MAF130-EVENT-001` — `AgentRunUpdateEvent` is removed (renamed)
- `MAF130-ATTR-001/002` — `[StreamsMessage]` and `[YieldsMessage]` are removed
- `MAF130-EXEC-002` — missing `partial` would be a build break, not a sample anti-pattern

A separate `samples/maf-1.2-sample/` codebase (deferred) could exercise these.

---

## What the migration toolkit SHOULD do with this sample

The end-to-end expectation (Phase T.3 → T.5):

1. **`@maf-auditor`** generates a `migration-plan.md` listing each finding.
2. **`@maf-migration`** executes that plan:
   - Rewrites the 3 investigators' `[MessageHandler]` returns from `ValueTask` →
     `ValueTask<InvestigationFinding>`.
   - Adds the missing `sealed` modifier to `TransactionInvestigator`.
   - Flips the fan-in arg order to the new pattern.
   - Replaces `DefaultAzureCredential` with `ManagedIdentityCredential`.
   - Removes `EnableSensitiveData = true`.
   - Adds `.UseOpenTelemetry()` to the chat-client pipeline.
   - Pulls the mutable fields off `CaseContextProvider` into a
     `ProviderSessionState<T>`.
   - Eliminates the `.Result` blocking call.
   - Wires both branches of the `.Use(...)` middleware.
3. After migration, **`MafDoctor`** returns **grade A or B**, the sample still
   compiles, and the workflow topology stays equivalent.

---

## Provenance

Inspired by the structural style of
[`AgentEval.TravelDemo`](https://github.com/joslat/AgentEval/tree/main/samples/AgentEval.TravelDemo)
(same maintainer's sibling project) — multi-agent MAF workflow, factory-per-agent
file layout, factory functions returning `ChatClientAgent`.

License: MIT. Not intended for production use; the embedded anti-patterns are
intentional.
