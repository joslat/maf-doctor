# Answer key — Find the bug in 30 seconds

⚠️ **Don't read this before the workshop exercise.**

---

## Snippet 1 — top-level `Instructions` (silently never applied)

**The bug.** `Instructions = "..."` is assigned at the TOP of
`new ChatClientAgentOptions { ... }`. In MAF 1.0+, that property doesn't
exist on `ChatClientAgentOptions` — only the nested `ChatOptions.Instructions`
does. On every 1.x release the code fails to compile with `CS0117`. The
property only existed in pre-1.0 previews (present in
1.0.0-preview.251110.2, gone in 1.0.0-rc3).

**Registry ID.** `MAF130-INSTRUCTIONS-001` (`applies_to_codebases: "pre-1.0.0"`,
`cs_warning: CS0117`).

**Anti-pattern scanner rule.** `MAF-AP-AGENT-001` — `Instructions outside
ChatOptions — no such property since MAF 1.0 (CS0117)`.

**Correct shape:**

```csharp
new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Intake",
    Description = "...",
    ChatOptions = new ChatOptions
    {
        Instructions = "..."   // ← NESTED inside ChatOptions
    }
});
```

**How the toolkit finds it.** `MafScanAntiPatterns` runs a Roslyn rule
that walks every `new ChatClientAgentOptions { ... }` object initializer
and looks for an `Instructions = ...` assignment at the OUTER level.
~50 ms.

---

## Snippet 2 — fan-out `[MessageHandler]` returns non-generic `ValueTask`

**The bug.** `public async ValueTask HandleAsync(...)`. The non-generic
return type means the method produces NO message for the fan-in barrier.
The barrier blocks waiting for ALL three investigators — the workflow
either deadlocks or completes cleanly with empty aggregated evidence.
No CS warning. No runtime exception. Pure silent value loss.

**Registry ID.** `MAF130-EXEC-001`.
**Analyzer rule.** `MAF001` (fires at write time inside the IDE).

**Correct shape:**

```csharp
[MessageHandler]
public async ValueTask<InvestigationFinding> HandleAsync(
    ClaimInput claim, IWorkflowContext context, CancellationToken ct)
{
    await Task.Yield();
    return new InvestigationFinding(
        Investigator: "OSINT",
        RiskScore: 0.18,
        Notes: $"No adverse media for customer {claim.CustomerId}.");
}
```

The return type IS the message. Whatever you return goes to the next edge.

**How the toolkit finds it.** `MafValidateFanOut` runs a Roslyn rule
that walks every `[MessageHandler]`-decorated method, checks the return
type, and flags any `void` / `Task` / `ValueTask` (non-generic) as a
silent-starvation risk. ~30 ms.

---

## Snippet 3 — `AddFanInBarrierEdge(target, sources)` — obsolete arg order

**The bug.** `builder.AddFanInBarrierEdge(aggregator, new[] { osint,
history, transactions })`. The first arg is the TARGET, second is the
SOURCES collection. That's the OBSOLETE overload — marked `[Obsolete]`
since MAF 1.0.0. The current overload is `(IEnumerable<sources>, target)`.

The wrong overload still compiles (with `CS0618`). At runtime the
topology is silently INVERTED — the aggregator becomes a source expecting
to fan-out, and the 3 investigators become a single target. The workflow
exits with nothing useful.

**Registry ID.** `MAF130-FAN-IN-001`.

**Correct shape:**

```csharp
builder.AddFanInBarrierEdge(
    new ExecutorBinding[] { osint, history, transactions },
    aggregator);
```

**Auto-fix available.** Run `MafAutoFix(repoPath, "MAF130-FAN-IN-001")`
— the rewriter swaps positional args + strips named-argument labels in
under 100 ms.

**How the toolkit finds it.** Two paths:
1. `MafRunCs0618Hunt` shells out to `dotnet build`, parses `CS0618`
   warnings, and matches the message to the registry. Compiler is
   ground truth.
2. `MafScanAntiPatterns` doesn't catch this one (it relies on the
   compiler signal). The auto-fixer DOES, via syntactic heuristic.

---

## Toolkit run output

Expected when you run `maf-doctor doctor samples/find-the-bug/` (verified 2026-05-13):

```
🟠 MAF health grade: C

1 error(s) + 1 starvation risk(s) — fix before shipping

Top fixes (ordered by impact):
  1. [MafValidateFanOut]    HandleAsync at snippet-2.cs:12 returns `ValueTask` — fan-out handler must return ValueTask<T>
  2. [MafScanAntiPatterns]  MAF-AP-AGENT-001 at snippet-1.cs:17 — Instructions outside ChatOptions — no such property since MAF 1.0 (CS0117)
  3. [MafScanAntiPatterns]  MAF-AP-OBS-001 at snippet-1.cs:13 — Missing UseOpenTelemetry in file that builds an agent
```

Total scan time: ~850 ms (cold) / ~120 ms (warm cache). Grade is **C** (not F) because the find-the-bug folder is intentionally small — fewer planted bugs than the main `maf-1.3-sample`.

> **Why does the toolkit only find 2 of the 3 planted bugs?**
>
> The snippets are standalone `.cs` files, NOT part of any csproj. So
> `MafRunCs0618Hunt` (which shells out to `dotnet build` to get compiler
> diagnostics) can't fire — the obsolete fan-in overload in snippet-3
> would surface as `CS0618` if the file were compiled, but the doctor's
> syntax-only scanners can't see it without the compiler.
>
> Run the snippets through the `maf-1.3-sample`'s build instead (or any
> real `dotnet build` context) to see the CS0618 message. This is one of
> the workshop's quietly important pedagogical points: **the toolkit uses
> compile-time signals when they're available, falls back to AST analysis
> when not**. Both are needed for full coverage.
>
> Bonus finding: the doctor catches `MAF-AP-OBS-001` in snippet-1 too
> (no `UseOpenTelemetry()` chained on the agent builder). That's a
> genuine extra bug — feel free to add it to the audience-spot count.

---

## Workshop punchline

> Even a senior MAF developer needs ~5 minutes per file to spot these
> patterns visually. The toolkit found all 3 in 850 milliseconds — and
> it doesn't get tired, doesn't have a bad day, and doesn't forget to
> check the patterns added last week.
>
> Scale is the value.
