---
name: maf-remediation-playbook
description: "The fix-everything playbook for the maf-remediate loop: per-rule canonical fix + how to tell a real finding from a false positive (by confidence tier). Use when driving a codebase to a clean MAF health grade — triage heuristic findings before changing code."
---

# maf-remediation-playbook — fix real issues, skip false positives

> **Used by the `maf-remediate` prompt.** It drives the loop; this skill is the per-rule reference: what the canonical fix is, and — for the heuristic detectors — how to recognise a false positive before you touch code.

## The loop (quick reference)

1. `MafDoctor(repoPath, format: "plan")` + `MafDoctor(repoPath, format: "json", full: true)` — the plan + every finding with a `confidence`.
2. `MafAutoFixAll(repoPath, dryRun: false)` — the deterministic, mechanical fixes (F-11: `dryRun` now defaults to `true`/preview-only; omitting it here would silently apply nothing). `dotnet build` (green).
3. For each remaining finding **in plan order**: triage by `confidence` (below) → fix or skip → `dotnet build` → confirm the finding actually cleared (re-scan), not just a green build.
4. Re-pull `MafDoctor(format: "json", full: true)` (line numbers shift after edits) and keep working findings until the only ones left are explicitly skipped (false positive) or deferred to human judgment — **NOT** merely until the A/B/C/F letter stops moving (the grade has wide bands and won't shift for most single fixes). Report fixed vs skipped-as-false-positive.

## Confidence tiers (the triage signal)

Every finding carries `confidence`:

- **`certain`** — compiler ground-truth (CS0618). No false positives. Fix it.
- **`high`** — structural AST rule, low false-positive risk. Apply the canonical fix with a quick sanity check.
- **`heuristic`** — name-only / text / scope-limited. **May be a false positive.** Call `MafExplainFinding(repoPath, file, line)`, read the surrounding code, and confirm it's a *real* problem **before** changing anything. When unsure after reading the code — in **either** mode — skip and flag for human review; never edit on a maybe.

## Per-rule playbook

| Rule | Confidence | Canonical fix | If it's a FALSE POSITIVE (skip) |
|---|---|---|---|
| **MAF001** (fan-out starvation) | high | Return `ValueTask<T>` — the value is auto-broadcast; **required** on an `AddFanOutEdge` source (`SendMessageAsync` does NOT broadcast on a fan-out edge). For a plain `AddEdge` target, `await context.SendMessageAsync(...)` is also valid. | **Rarely a false positive.** The source-level detector already treats any handler that emits via `context.SendMessageAsync`/`YieldOutputAsync`/`AddEventAsync` as OK, so a MAF001 finding means the handler returns nothing *and* emits nothing — a genuine dead-end. (The fan-out-EDGE nuance — a `SendMessageAsync`-only handler that happens to sit on an `AddFanOutEdge` source — is resolved cross-file by `MafSimulateWorkflow`, not by MAF001.) |
| **MAF-AP-EXEC-001** (legacy executor) | high | Delete `[StreamsMessage]`/`[YieldsMessage]`; migrate `ReflectingExecutor<>` → `sealed partial : Executor` + `[MessageHandler]`. | An `IMessageHandler<T>` that is MediatR / NServiceBus / a hand-rolled bus (no `Microsoft.Agents.AI.Workflows`). |
| **MAF-AP-DEVUI-001** (DevUI/Hosting) | high | Wrap the unsupported preview reference in `#if DEVUI_ENABLED`. | A supported `Microsoft.Agents.AI.Hosting.A2A[.AspNetCore]` using, or a project's own `namespace DevUI;`. |
| **MAF-AP-SEC-001** (DefaultAzureCredential) | high | `ManagedIdentityCredential` in production. | Already inside an `env.IsDevelopment()` / `#if DEBUG` dev-only branch. |
| **MAF-AP-SEC-003** (EnableSensitiveData) | high | Make it env-driven (`= env.IsDevelopment()`) or fence behind `#if DEBUG`. | Same-named `bool` on an unrelated (non-OTel) type. |
| **MAF-AP-CONC-001** (provider field) | high | Move per-session state into a `readonly ProviderSessionState<T>`. | The field already IS `ProviderSessionState<T>`, or an injected `readonly` dependency. |
| **MAF-AP-CONC-002** (`.Result`/`.Wait()`) | high | Make the chain `await`-first; propagate `async` + a `CancellationToken`. | `(await x).Result` (AgentResponse<T> payload) or `Match.Result(...)` (Regex) — not a blocking Task. |
| **MAF-AP-AGENT-001** (top-level Instructions) | high | Move `Instructions` into the nested `ChatOptions` (no top-level property since before MAF 1.0; CS0117). | A user type named `ChatClientAgentOptions` in your own namespace. |
| **MAF-AP-WF-001** (sealed Executor) | high | Add `sealed partial` to the concrete Executor. | A non-MAF base type that happens to be named `Executor`. |
| **MAF-AP-WF-002** (duplicate handler) | high | Keep one `[MessageHandler]` per message type: merge them, or give each its own message type. | Handlers in different executors, or for genuinely different types the syntax can't tell apart (e.g. two `using` aliases). |
| **MAF-AP-WF-003** (`async void` handler) | high | Return `ValueTask` / `ValueTask<T>`. | — (an `async void` method without `[MessageHandler]` is not flagged). |
| **MAF-AP-APPROVAL-001** (approval without session) | high | `await agent.RunAsync(approvalMessage, session)` with the session that produced the request. | The response is sent through a helper that passes the session itself, or `DisableApprovalResponseBinding = true` with a service-side conversation you have checked. |
| **COST-001** (uncapped agent call) | **heuristic** | Set `MaxOutputTokens` on the nearest `ChatOptions`. | `app.RunAsync()` (ASP.NET host), `InProcessExecution.RunStreamingAsync`/`workflow.RunAsync` (workflow runners) — not agent calls. |
| **MAF-AP-SEC-002** (hard-coded key) | **heuristic** | Source the key from env/Key Vault; rotate the leaked one. | A non-secret string that merely starts with `sk-` (a SKU id, slug, or `sk-xxxx` placeholder). |
| **MAF-AP-OBS-001** (no OpenTelemetry) | **heuristic** | Chain `.AsBuilder().UseOpenTelemetry(...)` on the client/agent. | The `IChatClient` is instrumented in another file and injected here. |
| **MAF-AP-MID-001** (middleware) | **heuristic** | Provide both `runFunc:` and `runStreamingFunc:` (or the `sharedFunc:` overload). | A non-MAF builder whose `Use(runFunc:)` has no streaming concept. |
| **PROMPT-001/002/003/004** (prompt lint) | **heuristic** | Non-empty prompt; split bloat; add refusal guidance; never interpolate untrusted input into Instructions. | An `Instructions` property on a non-agent type (recipe, build step, form field). |

## Rules

- Make the **minimal** change that resolves a finding; don't refactor opportunistically.
- `dotnet build` green after every change; never mark a finding fixed on a red build.
- Hard constraints at `maf://constraints` override everything here.
- A skipped false positive is a SUCCESS, not a failure — record it with a one-line reason.
