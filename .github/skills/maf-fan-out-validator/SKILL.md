---
name: maf-fan-out-validator
description: "Validates fan-out / fan-in workflow topology — detects the silent fan-in starvation pattern (handlers returning void / non-generic Task that produce no downstream message). Canonical rules + procedure for what the compiler cannot catch and the build cannot surface."
---

# fan-out-validator — silent fan-in starvation detection

> **⚡ Prefer the MCP tools.** The `maf-autopilot` MCP server exposes:
> - **`MafValidateFanOut(repoPath)`** — Roslyn syntax walk; flags any `[MessageHandler]` returning `void` / non-generic `Task` / `ValueTask` (the silent-starvation patterns).
> - **`MafSimulateWorkflow(repoPath)`** — builds the full executor topology graph (fan-out + fan-in edges resolved through variable bindings), emits a Mermaid diagram, and gives an end-to-end completion forecast.
>
> Use both: `MafValidateFanOut` for per-handler verdicts, `MafSimulateWorkflow` for "would this workflow complete?"
>
> *This skill formerly had a sibling `fan-in-static-analyzer` that walked the same patterns by hand; both have been merged here now that the MCP tools cover the procedural work.*

## The bug class — "silent fan-in starvation"

A fan-out executor's `[MessageHandler]` method MUST return one of:
- `ValueTask<TMessage>`
- `TMessage` (a synchronous handler; the returned value is sent the same way)

Checked by building and running a workflow on Microsoft.Agents.AI.Workflows 1.3.0 and 1.23.0 (2026-10-03). `Task<TMessage>` is **not** one of them: the source generator rejects it (`MAFGENWF002`). `IAsyncEnumerable<TMessage>` builds but does **not** stream: the iterator object is sent as one message, which fails the run. Send items one at a time with `await context.SendMessageAsync(item)`.

A handler that returns `void`, `Task`, or `ValueTask` (non-generic) produces **no output message**. The fan-in barrier on the downstream edge then **starves silently** — the workflow exits cleanly but incompletely.

**This is NOT a build error.** `dotnet build` is clean. Tests that don't exercise the fan-in path are clean. The bug only surfaces in production traces (or with one of the tools above).

## Return-type verdict table

| Return type                              | Verdict                  | Why                                       |
|------------------------------------------|--------------------------|-------------------------------------------|
| `ValueTask<TMessage>`                    | ✅ OK                    | Produces a downstream message             |
| `int`, `string`, any concrete `TMessage` | ✅ OK                    | Synchronous handler; the value is sent    |
| `void`                                   | ❌ SILENT_STARVATION_RISK | No completion signal AND no message      |
| `Task`, `ValueTask` (non-generic)        | ❌ SILENT_STARVATION_RISK | Completes but emits nothing               |
| `Task<TMessage>`                         | ❌ LIKELY_INVALID         | Source generator rejects it (`MAFGENWF002`) |
| `IAsyncEnumerable<TMessage>`             | ❌ LIKELY_INVALID         | Sends the iterator as one message; the run fails |

A `void` / `ValueTask` handler that emits with `context.SendMessageAsync` / `YieldOutputAsync` / `AddEventAsync` is OK.

## Fan-in argument-order rule

The canonical 1.3.0 form of `AddFanInBarrierEdge` is:

```csharp
builder.AddFanInBarrierEdge(
    new[] { sourceA, sourceB, sourceC },   // sources first (IEnumerable<ExecutorBinding>)
    target);                               // target last (single ExecutorBinding)
```

The legacy `(target, sources)` overload is `[Obsolete]` in 1.3.0 — triggers `CS0618`. The compiler catches that one. (See registry entry `MAF130-FAN-IN-001`.)

## Manual fallback

When the MCP tools are unavailable, walk every `MethodDeclarationSyntax` in the codebase that has `[MessageHandler]` and inspect its `ReturnType`. Apply the verdict table above. Cross-reference each fan-out edge against the producing executor's handler.

## Companion analyzer (write-time enforcement)

The `maf-doctor.Analyzers` NuGet package ships **`MAF001`** (Warning severity: it sees one method, not the workflow topology; set `dotnet_diagnostic.MAF001.severity = error` to fail CI) for exactly this pattern — a `[MessageHandler]` returning `void` or a non-generic awaitable that emits nothing. Add `<PackageReference Include="maf-doctor.Analyzers" />` to your project to catch this at write-time, before commit.
