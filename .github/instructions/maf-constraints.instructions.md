---
description: "Always-loaded, version-aware MAF migration constraints. Contains durable security rules, release-path invariants, and verification gates for every tracked Microsoft Agent Framework version."
applyTo: "**"
---

# MAF Migration Constraints

These rules apply across the tracked MAF release chain. Version-specific API
recipes belong in the ordered per-version guides and registry; do not promote a
single release's syntax into a timeless global rule.

## Hard Constraints (Never Violate)

- **ALWAYS identify the exact source and target package versions** and call
  `MafMigrationPath(currentVer, targetVer)` for a multi-version upgrade. Apply
  every returned step in ascending order.
- **NEVER store session-specific state in `AIContextProvider` or
  `ChatHistoryProvider` instance fields.** Use the framework's session-state
  mechanism for the selected version, such as `ProviderSessionState<T>`, and
  persist the session when continuity must outlive a process or top-level call.
- **NEVER use `DefaultAzureCredential` in production code.** Prefer an explicit
  production credential such as `ManagedIdentityCredential`; keep developer
  credential chains in development-only composition.
- **NEVER enable `EnableSensitiveData = true` outside a development-only,
  access-controlled diagnostic boundary.** Provider errors, prompts, tool
  arguments, session state, and model output can contain secrets or personal
  data.
- **NEVER treat model-generated code or shell commands as sandboxed.**
  `LocalCodeAct`, Python child processes, and `Tools.Shell` require a real
  container/VM boundary, minimal filesystem mounts, restricted credentials and
  egress, resource limits, and explicit tool/policy composition.
- **NEVER derive file-memory roots, shell paths, callback destinations, or
  tenant storage folders directly from untrusted input.** Validate and
  authorize them against an application-owned boundary.
- **NEVER expose raw hosted-provider failure detail by default.** Preserve
  server-side diagnostics in access-controlled telemetry; enable exception
  details only for trusted callers.
- **ALWAYS pin `dotnet-inspect` to exact v0.9.1** in this repository. Reject a
  missing, mismatched, truncated, or structurally invalid report. Use
  `MafRunCs0618Hunt` and the compiler as ground truth for overload resolution,
  transitive obsoletions, and project-local `[Obsolete]` attributes.
- **ALWAYS preserve the published maturity/version of each package.** Stable,
  preview, RC, and alpha packages in one train are not interchangeable. Do not
  invent an aligned version for an externalized or independently shipped
  package.
- **ALWAYS restore, build, and run focused behavior tests after migration.** An
  empty public API diff cannot detect persistence, streaming, checkpoint,
  redaction, tool-approval, or workflow-routing behavior changes.

## Version-Scoped Compatibility Checkpoints

| Target step | Mandatory review |
|---|---|
| 1.3.0 | Removed executor attributes/types, async sessions, response/streaming changes, source generation, and fan-out/fan-in behavior. |
| 1.4.0 / 1.5.0 | These per-version guides still contain unfilled human analysis; verify exact assemblies and official tags before applying their evidence. |
| 1.14.0 | Agent/session and approval lifecycle, Harness opt-in file/shell providers, AG-UI split, Copilot declarations, and `ShellPolicy` binary/deny-first semantics. |
| 1.15.0 | Preview Hosting `sessionStoreId` named arguments, abstract `DeleteSessionAsync`, session isolation, checkpoint ordering, and declarative/hosted behavior. |
| 1.16.0 | `Microsoft.Extensions.VectorData.Abstractions` 9.7→10.7 migration, provider rebuilds, history ownership, approval sessions, FileMemory scope, and code-execution boundaries. |
| 1.17.0 | Declarative top-level `ErrorContent` is terminal; Foundry raw failure payloads are redacted; Durable Task/Azure Functions packages follow an independent extension cadence. |
| 1.18.0 | Hosting session isolation: `SessionIsolationKeyProvider` is replaced by `AgentIsolationKeyProvider`, including the `IsolationKeyScopedAgentSessionStore` constructor. |
| 1.19.0 | `Microsoft.Agents.AI.Mcp` long-running tasks moved to the 2026-07-28 Tasks extension: `ListAgentToolsWithTasksAsync` rename, `McpTaskOptions.DefaultTimeToLive` removed. |
| 1.20.0 | Additive release; no registry changes. Review the release notes for behavior only. |
| 1.21.0 | `HostedWorkflowBuilderExtensions.AddAsAIAgent` overloads changed (rebuild). Behavior-only: `AgentFileStore` line numbering, A2A run modes, LocalCodeAct subprocess environment isolation, ZIP-only MCP skill archives. |
| 1.22.0 | Session stores promoted to `Microsoft.Agents.AI.AgentSessionStore` (Abstractions): namespace move, key-based `GetSessionAsync`/`SaveSessionAsync` returning `AgentSession?`, `DeleteSessionAsync` removed, rebuild for the base-type change. Foundry user identity is sticky on the session (`WithFoundryHostedAgentUserIdentity` removed). Behavior: approval binding, per-invocation MCP sessions, per-run tools. |
| 1.23.0 | Declarative `GetChatOptionsAsync`/`EvalAsync` are async; Foundry parameter types moved to `Azure.AI.Extensions.OpenAI`; `AIHostAgent` constructor binary change; Azure.AI.Projects 3.0.0-beta.3, OpenAI 2.14.0 and MEAI 10.10.1. Behavior: tool changes between runs, approval-response binding, configuration-key allow list, DevUI approval continuation. |

Read the exact target guide before changing code. Existing repositories may
need several rows, not only the final target row.

## Workflow Rules That Remain Active

For 1.3-and-later workflow patterns, fan-out handlers that produce a downstream
message must return that message from `ValueTask<T>` (or the exact supported
generic async shape for the selected version). A void/non-generic return can
starve fan-in without a build error. Validate the real topology with
`MafValidateFanOut` or `MafSimulateWorkflow` and a runtime completion test.

For versions where the registry marks the target-first
`AddFanInBarrierEdge(target, sources)` overload obsolete, use the applicable
sources-first recipe and confirm with `MafRunCs0618Hunt`. Do not rely on this
summary instead of the registry: overload sets can change between releases.

`[StreamsMessage]` and `[YieldsMessage]` were removed on the 1.3 path. Delete
them when that step applies; do not add them to a current target merely to make
old sample code look familiar.

## Security Boundaries Added by Later Releases

- MAF 1.14 Shell policies evaluate deny rules first, and a non-null allow list
  is exclusive. Rebuild binary consumers of the constructor and test denied,
  allowed, and unmatched commands.
- Harness file access and shell execution are explicit opt-ins. Do not register
  them merely because a sample does.
- File memory with an empty working folder can share the file-store root. Assign
  an authorized tenant/user folder or a unique per-session folder deliberately.
- A2A request configuration and metadata are untrusted request input. Server
  execution policy remains authoritative; validate any value before acting on
  it, especially push/callback destinations.
- Declarative/Foundry failures must remain failures. Do not turn an
  `ExecutorFailedEvent` or `ErrorContent` back into an empty success, and do not
  leak provider detail by bypassing the host's exception-detail policy.
- `LocalCodeAct` defense-in-depth checks are not a Python sandbox. External
  process, network, filesystem, and credential isolation remains mandatory.

## Verification Tools

| Concern | Verification |
|---|---|
| Ordered multi-version path | `MafMigrationPath(currentVer, targetVer)` |
| Exact package/framework compatibility | `MafCompatibility(targetVersion)` and `docs/compatibility-matrix.md` |
| Obsolete/removed API usage | `MafRunCs0618Hunt(projectPath)` plus `MafRegistryLookup` |
| Session state in provider fields | `MafScanAntiPatterns(repoPath)` → `MAF-AP-CONC-001` |
| `DefaultAzureCredential` in production | `MafScanAntiPatterns(repoPath)` → `MAF-AP-SEC-001` |
| Sensitive-data logging outside development | `MafScanAntiPatterns(repoPath)` → `MAF-AP-SEC-003` |
| Fan-out/fan-in topology | `MafValidateFanOut(repoPath)` and `MafSimulateWorkflow` |
| Public package API delta | `MafDiffPackage` with exact old/new artifact versions |
| End-to-end repository state | `MafDoctor(repoPath)`; surface `scan_truncated` as incomplete evidence |

Finish with the real solution's restore/build/test commands. Treat a successful
static scan as necessary evidence, not proof that behavior and security
boundaries are correct.
