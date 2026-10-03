---
name: maf-anti-pattern-scanner
description: "Scans a MAF 1.3.0 codebase for known anti-patterns AFTER migration is complete. Prefer the MCP tool MafScanAntiPatterns for one-shot scans; this skill is for understanding the rule taxonomy or contributing new anti-pattern rules. Detects insecure credential usage (DefaultAzureCredential, hard-coded keys), unsafe defaults (EnableSensitiveData=true in non-dev), thread-unsafe state in AIContextProvider, missing observability (UseOpenTelemetry not wired), and missing identity (ManagedIdentityCredential preferred over secret-based auth). Counterpart to migration tooling: this is steady-state best-practice review."
---

# maf-anti-pattern-scanner

The migration tooling answers "is this code on MAF 1.3.0?" This skill answers a different question: **"is this MAF 1.3.0 code following best practices?"** Same codebase, different lens.

## When to use

- After a migration plan finishes — verify the converted code isn't merely compiling, it's idiomatic.
- On any clean 1.3.0 codebase as a periodic audit.
- In CI on PRs touching MAF agent/workflow code, scoped to the diff.

## When NOT to use

- For migration discovery — that's `maf-obsolete-api-registry` + `cs0618-hunter`. This skill assumes you're already on 1.3.0.
- For runtime correctness checks — that's `maf-fan-out-validator`. This skill checks *idiom and configuration*, not topology correctness.

## Anti-patterns covered

Each is a "rule" with a unique ID, a `severity`, and a deterministic search pattern. The MCP tool `MafScanAntiPatterns` ingests this list.

### Security

#### `MAF-AP-SEC-001` — `DefaultAzureCredential` in production code  (severity: error)
**Pattern:** `new DefaultAzureCredential()` anywhere outside a test project (`*Tests.csproj`).
**Why:** `DefaultAzureCredential` walks an authentication chain that includes developer credentials, environment variables, and managed identity. The chain order can change between SDK versions; production deployments should NEVER tolerate that surprise. Pin to the specific credential type your environment uses.
**Fix:** Replace with `ManagedIdentityCredential` (Azure-hosted) or `WorkloadIdentityCredential` (Kubernetes), or `ClientSecretCredential` / `ClientCertificateCredential` for explicit service principals.
**Source:** `maf-constraints.instructions.md` hard rule #4.

#### `MAF-AP-SEC-002` — Hard-coded API keys  (severity: error)
**Pattern:** String literal matching `(sk-|api[-_]?key)[A-Za-z0-9_-]{16,}` anywhere in .cs files.
**Why:** Keys in source land in git history forever.
**Fix:** Read from `IConfiguration` / `Azure.Identity` / `Microsoft.Extensions.Configuration.UserSecrets`.

#### `MAF-AP-SEC-003` — `EnableSensitiveData = true` outside development  (severity: error)
**Pattern:** `EnableSensitiveData = true` as an initializer entry, a statement, or the body of a configure lambda (`UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)`, the shape Microsoft documents), outside `#if DEBUG` / `#if DEVELOPMENT`.
**Why:** It records prompts, tool arguments and message contents in telemetry (traces and their exporters). It is off by default unless `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`.
**Fix:** Gate behind `#if DEBUG` or `builder.Environment.IsDevelopment()`. The auto-fix removes the assignment (a configure lambda becomes `c => {}`).
**Source:** `maf-constraints.instructions.md` hard rule #5.

### Concurrency / state

#### `MAF-AP-CONC-001` — Instance fields on `AIContextProvider`  (severity: error)
**Pattern:** A class deriving from `AIContextProvider` (or `ChatHistoryProvider`) that declares any non-readonly instance field.
**Why:** `AIContextProvider` instances are shared across sessions. Mutable instance state causes cross-session leakage, races, and non-deterministic responses.
**Fix:** Use `ProviderSessionState<T>` for per-session state, or make fields `readonly` and immutable.
**Source:** `maf-constraints.instructions.md` hard rule #2.

#### `MAF-AP-CONC-002` — `.Result` / `.Wait()` on async  (severity: warning)
**Pattern:** `\)\s*\.(?:Result|Wait)\b` — a `.Result` / `.Wait()` immediately following a method CALL (e.g. `SomeAsync().Result`). Deliberately anchored on the call's close-paren rather than the bare member name: matching `task.Result` on *any* member would false-fire on the many non-Task `.Result` / `.Wait` properties in real code, so the bare-member form is intentionally out of scope (pinned by a regression test).
**Why:** Sync-over-async deadlocks under SynchronizationContext. Agent runtimes pump async; mixing sync waits is a latent hang.
**Fix:** `await` the expression. Make the calling method `async`.

### Observability

#### `MAF-AP-OBS-001` — Missing `UseOpenTelemetry`  (severity: warning)
**Pattern:** A file builds an agent (`AIAgentBuilder` / `ChatClientAgent`) but never calls `UseOpenTelemetry(...)` in the same file.
**Why:** MAF emits no telemetry by default, so agent calls are invisible in production: token usage, latency and errors all silent.
**Fix:** `agent.AsBuilder().UseOpenTelemetry(sourceName).Build()` (it also turns on the chat-level spans), wired to your OTel exporter.
**Source:** Migration guide §13.

#### `MAF-AP-MID-001` — Middleware `Use()` without `runStreamingFunc`  (severity: info)
**Pattern:** `.Use(runFunc: …)` with no `runStreamingFunc:`, or `runStreamingFunc: null`.
**Why:** MAF uses `runFunc` for both paths ("if only one of the delegates is provided, it will be used for both methods", `AIAgentBuilder.Use`, 1.23). The middleware runs, but `RunStreamingAsync` returns `runFunc`'s output as one batch, so callers lose incremental streaming.
**Fix:** If callers stream, also provide `runStreamingFunc:`, or use the `sharedFunc:` overload.

### Agents and tool approval

#### `MAF-AP-AGENT-001` — `Instructions` at the top of `ChatClientAgentOptions`  (severity: error)
**Pattern:** `new ChatClientAgentOptions { Instructions = … }`.
**Why:** No MAF release from 1.0 on has that property (it was removed before 1.0 GA), so the code fails with CS0117.
**Fix:** `ChatOptions = new ChatOptions { Instructions = … }`.

#### `MAF-AP-APPROVAL-001` — Tool-approval response sent without the session  (severity: warning)
**Pattern:** `RunAsync` / `RunStreamingAsync` whose message carries `request.CreateResponse(…)` (directly, or through a local, an assignment or an `Add` call) but no session argument, or `null`.
**Why:** MAF only accepts a tool-approval response whose request was recorded in the current `AgentSession` (`ChatClientAgentOptions.DisableApprovalResponseBinding` docs, 1.23). Without a session the run starts a new one, the response is ignored, and the tool never runs.
**Fix:** `await agent.RunAsync(approvalMessage, session)` with the session that produced the request.

### Workflows

#### `MAF-AP-WF-001` — Executor with `[MessageHandler]` methods must be `partial`  (severity: error)
**Pattern:** A class deriving from `Executor` with `[MessageHandler]` methods and no `partial` modifier (abstract or not).
**Why:** The source generator cannot emit the handler wiring: the build fails (MAFGENWF003, or CS0534 without the generator package). `sealed` is not required (Workflows.Generators 1.23; `MessageHandlerAttribute`'s own example is `public partial class MyExecutor : Executor`).
**Fix:** Add `partial`. The auto-fix adds only `partial`.

#### `MAF-AP-WF-002` — Two `[MessageHandler]` methods for the same message type  (severity: error)
**Pattern:** Two handlers in one class whose first parameter is the same type (`string` and `System.String` count as the same).
**Why:** The generator accepts it, but running the workflow throws "A handler for message type … is already registered" (checked on Workflows 1.23).
**Fix:** One handler per message type: merge them, or give each its own message type.

#### `MAF-AP-WF-003` — `async void` `[MessageHandler]`  (severity: warning)
**Pattern:** A `[MessageHandler]` method declared `async void`.
**Why:** The generator accepts a `void` handler, so nothing flags it, but the workflow cannot await it: it counts as finished at its first `await`, and an exception after that escapes the workflow.
**Fix:** Return `ValueTask` (or `ValueTask<T>`).

#### `MAF-AP-WF-004` — `[MessageHandler]` sends or yields a type it does not declare  (severity: error)
**Pattern:** A handler calls `SendMessageAsync(...)` with no `Send = [...]` on its `[MessageHandler]` and no `[SendsMessage]` on its class, or `YieldOutputAsync(...)` with no `Yield = [...]` and no `[YieldsOutput]`. Only executors whose handlers all return no value are checked.
**Why:** Running the workflow fails: "Executor 'x' cannot send messages of type 'T'" / "Cannot output object of type T. Expecting one of []" (Workflows 1.23, checked by running one). Declared, a sent message reaches every target, fan-out edges included.
**Fix:** `[MessageHandler(Send = [typeof(T)], Yield = [typeof(U)])]`, or `[SendsMessage(typeof(T))]` / `[YieldsOutput(typeof(U))]` on the executor class.

#### `MAF-AP-EXEC-001` — Obsolete executor surface  (severity: warning)
**Pattern:** `[StreamsMessage]`, `[YieldsMessage]`, `ReflectingExecutor<…>`, or `IMessageHandler<…>` in a file that imports the MAF Workflows namespace.
**Why:** Obsolete, a CS0618 warning (still public in Workflows 1.23): `ReflectingExecutor` / `IMessageHandler` since 1.0, `[StreamsMessage]` ("does not do anything") and `[YieldsMessage]` (ignored) since 1.2.
**Fix:** A `partial` `Executor` with `[MessageHandler]` methods; delete `[StreamsMessage]`; replace `[YieldsMessage(typeof(T))]` with `[YieldsOutput(typeof(T))]`.

### Hosting

#### `MAF-AP-DEVUI-001` — DevUI enabled outside a development-only guard  (severity: warning)
**Pattern:** `AddDevUI(...)` / `MapDevUI(...)` outside `#if DEVUI_ENABLED` / `#if DEBUG` and outside an `if (… IsDevelopment())` block.
**Why:** DevUI "exposes agent metadata that is sensitive in production contexts: system instructions, tool definitions, model identifiers, and workflow structure" (`DevUIOptions`, 1.23-preview). It accepts only loopback requests by default. Hosting (`AddAIAgent`, A2A, AG-UI) is production hosting and is not flagged.
**Fix:** `if (app.Environment.IsDevelopment()) { app.MapDevUI(); }`, or `#if DEVUI_ENABLED`.

## Output format

The scanner produces a markdown report:

```
## MAF anti-pattern scan — <repoPath>

### ❌ <N> errors
| Rule | File | Line | Match |
| MAF-AP-SEC-001 | src/Auth.cs | 42 | new DefaultAzureCredential() |
...

### ⚠️ <N> warnings
...

### ℹ️ <N> info
...

### ✅ <N> rules passed
```

## How the MCP tool invokes this

The `MafScanAntiPatterns(repoPath)` tool walks all `*.cs` files under `repoPath`, applies the regex / Roslyn rules defined in this skill, and produces the report above. The rule IDs in this document are the source of truth; the tool's internal rule list mirrors them.

## Updating this skill

When the team discovers a new anti-pattern that should be caught at scan time:

1. Add a new `MAF-AP-<AREA>-<NNN>` entry above with rule + pattern + fix + source.
2. Add the matching rule constant in `src/maf-autopilot/Tools/AntiPatternScannerTool.cs`.
3. Add a test in `src/maf-autopilot.Tests/AntiPatternScannerToolTests.cs` proving the new rule fires.
4. Cross-reference from `maf-constraints.instructions.md` if it represents a hard rule.

The cross-reference matters: if a constraint says "NEVER do X" but the scanner doesn't enforce it, the constraint is aspirational, not actionable.
