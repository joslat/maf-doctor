# MAF Compatibility Matrix

<!-- auto-updated-by: maf-release-watcher | last-updated: 2026-10-01 -->

<!--
  Note on the Azure.AI.OpenAI column: MAF doesn't pin Azure.AI.OpenAI directly.
  It exposes IChatClient as the extension point, and Azure.AI.OpenAI (or any
  other backing implementation) is configured by the consumer at composition
  time. The 1.3.0 value (≥ 2.8.0-beta.1) reflects the version the MAF docs /
  samples used at the time of the 1.3.0 release — not a hard csproj constraint.
  For 1.4+, the chat-client extension point is unchanged; we don't have a
  hard MAF-enforced pin to record so the cell shows "(not pinned by MAF)".
-->

> This file is the single source of truth for MAF version ↔ dependency version compatibility.
> It is referenced by `maf-doctor` MCP server tools (`maf_compatibility`) and auto-updated by the `maf-release-watcher` skill and GitHub Actions workflow.

---

## Compatibility Table

| MAF Version | Microsoft.Extensions.AI | .NET | Azure.AI.OpenAI | Generators Package | Notes |
|-------------|------------------------|------|-----------------|--------------------|-------|
| **1.19.0** | `≥ 10.7.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.19.0` | **Breaking in the alpha `Microsoft.Agents.AI.Mcp` package**: MCP long-running tasks moved to the 2026-07-28 Tasks extension (`ListAgentToolsWithTaskSupportAsync` → `ListAgentToolsWithTasksAsync`, `McpTaskOptions.DefaultTimeToLive` removed); MAF now builds against `ModelContextProtocol` 2.1.0. **Additive** in Core and Workflows. See `guides/maf-1.19.0-migration-guide.md`. |
| **1.18.0** | `≥ 10.7.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.18.0` | **Breaking in the preview Hosting package** — `SessionIsolationKeyProvider` was renamed to `AgentIsolationKeyProvider`, which also changes the `IsolationKeyScopedAgentSessionStore` constructor's `keyProvider` parameter type. **Additive** in Core: `AllowConcurrentInvocation`, `EnableInvocableFunctionBypassing` / `UseInvocableFunctionBypassing`, bounded tool-approval auto-approval (`MaxAutoApprovalIterations`), and `BackgroundAgentsProvider.ReleaseSessionAsync`. Transitive pins carried from 1.17.0. See `guides/maf-1.18.0-migration-guide.md` and the registry entries. |
| **1.17.0** | `≥ 10.7.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.17.0` | **Maintenance release: no public-API or dependency-floor break.** All eight validated in-repository surfaces have empty API diffs, and the tracked .NET/`Microsoft.Extensions.AI` floors remain unchanged from 1.16.0. Runtime behavior does change in the separately shipped Declarative packages: a top-level agent `ErrorContent` now fails the workflow before completion or downstream actions instead of looking like an empty success; hosted error detail remains governed by the host's exception-detail policy. `Microsoft.Agents.AI.DurableTask` and `Microsoft.Agents.AI.Hosting.AzureFunctions` moved to `microsoft/agent-framework-durable-extension` without changing package IDs and now follow that repository's independent release cadence, so the absence of a 1.17-aligned package is not a removal. See `guides/maf-1.17.0-migration-guide.md` and the 1.17 registry entries. |
| **1.16.0** | `≥ 10.7.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.16.0` | **First-party MAF APIs are additive; transitive migration may be required.** Workflows adds Magentic prompt/language configuration. Core raises `Microsoft.Extensions.AI` and `Microsoft.Extensions.VectorData.Abstractions` to 10.7.0; VectorData 9.7→10.7 contains source/API breaks in vector dimensions, filters, generic record constraints, and provider extension points. Behavioral fixes clarify chat-history ownership, tool-approval sessions, FileMemory storage scope, declarative table state, Foundry port binding, and A2A configuration forwarding. GitHub Copilot graduates to stable and LocalCodeAct is a new preview package. See `guides/maf-1.16.0-migration-guide.md` and the 1.16 registry entries. |
| **1.15.0** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.15.0` | **Breaking in the preview Hosting contract** — ten source-only named-argument changes rename `conversationId:` to `sessionStoreId:` across `AgentSessionStore` and its built-in stores; positional and existing binary calls remain compatible. Custom direct subclasses must implement the new abstract `DeleteSessionAsync`. Custom stores must return independent session snapshots and checkpoint indexes in oldest-to-newest commit order. Stable Core has no public API change. See `guides/maf-1.15.0-migration-guide.md` and the 1.15 registry entries. Transitive pins are carried from 1.14.0. |
| **1.14.0** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.14.0` | **Breaking** — migration required. The ten validated release-critical surfaces contain 30 API breaks: agent-mode terminology and async session APIs; approval-rule context changes; default-on approval-not-required bypassing and approval-response binding; async message injection; non-null todo sessions; opt-in Harness file access and explicit shell composition; `AddAGUIServer` / `MapAGUIServer`; Copilot function-declaration collections; and a binary-breaking `ShellPolicy` constructor with deny-first semantics. The legacy `Microsoft.Agents.AI.AGUI` package split into `AGUI.Client`, `AGUI.Server`, `AGUI.Abstractions`, `AGUI.Protobuf`, and `AGUI.Formatting`. See `guides/maf-1.14.0-migration-guide.md` and the 1.14 registry entries. Transitive pins are carried from 1.13.0. |
| **1.13.0** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.13.0` | **Breaking** (.NET): file-store API renamed across `AgentFileStore`/`FileSystemAgentFileStore`/`InMemoryAgentFileStore` — `WriteFileAsync`→`WriteAsync`, `ReadFileAsync`→`ReadAsync`, `DeleteFileAsync`→`DeleteAsync`, `ListFilesAsync`/`ListDirectoriesAsync`→`ListChildrenAsync`, `SearchFilesAsync`→`SearchAsync`; `FileListEntry.FileName`→`Name`; `FileAccessProvider` file-tool-name constants replaced (`SaveFileToolName`→`WriteToolName`, `ListFilesToolName`/`ListSubdirectoriesToolName`→`LsToolName`, `SearchFilesToolName`→`GrepToolName`). **Additive**: new composable skills-source types (`CachingAgentSkillsSource`, `AggregatingAgentSkillsSource`, `DeduplicatingAgentSkillsSource`, `DelegatingAgentSkillsSource`, `FilteringAgentSkillsSource`, `AgentInMemorySkillsSource`); `IDisposable` on `AgentSkillsProvider`/`AgentSkillsSource`/`FileAccessProvider`. Transitive pins carried from 1.12.0. |
| **1.12.0** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.12.0` | **Breaking** (.NET): the skills-source API gained a required `AgentSkillsSourceContext` — `GetSkillsAsync` (on `AgentSkillsSource` / `AgentFileSkillsSource`) and `AgentSkillsProviderBuilder.UseFilter`'s predicate now receive it (positional call sites / overrides break); `AgentSkillsProviderOptions.DisableCaching` was **removed** (caching moved to the new `CachingAgentSkillsSourceOptions`). **Additive**: `AgentSkillsSourceContext` / `CachingAgentSkillsSourceOptions` types. Transitive pins carried from 1.11.1. |
| **1.11.1** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.11.1` | **Breaking** (.NET): `AgentSkillsProvider` tools now require approval by default (#6729) — the opt-in `AgentSkillsProviderBuilder.UseScriptApproval()` method and `AgentSkillsProviderOptions.ScriptApproval` property were **removed** (CS0246 for old call sites); `AgentMcpSkillsSource` updated to support archive-type skills (#6631). **Additive**: `AgentSkillsProviderBuilder.UseSource`; new `ReadSkillResourceToolName` / `RunSkillScriptToolName` tool-name constants. Transitive pins unchanged from 1.11.0. |
| **1.11.0** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.11.0` | **Breaking** (positional call sites only): `SearchFilesAsync` inserts a `recursive` parameter before `cancellationToken` on `AgentFileStore` / `FileSystemAgentFileStore` / `InMemoryAgentFileStore`. **Additive** (trailing optional params — source-compatible): `AgentInlineSkill` constructors add `argumentMarshaler`; `TodoProvider` `GetAllTodosAsync` / `GetRemainingTodosAsync` add a trailing `CancellationToken`. |
| **1.10.0** | `≥ 10.6.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.10.0` | Breaking: skill `Content`/`Resources`/`Scripts` properties removed; `ToolApprovalAgent` constructor takes `ToolApprovalAgentOptions?` instead of `JsonSerializerOptions?`; `SubAgentsProvider` / `SubTaskInfo` / `SubTaskStatus` removed; `GroupChatWorkflowBuilder`/`MagenticWorkflowBuilder` `.WithName`/`.WithDescription` removed. |
| **1.6.1** | `≥ 10.5.1` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.6.1` | Additive release; adds `expectedOutput` parameter to `WorkflowEvaluationExtensions.EvaluateAsync` for ground-truth evaluation. |
| **1.5.0** | `≥ 10.5.1` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.5.0` | Additive release; new `ToolApprovalAgent`. |
| **1.4.0** | `≥ 10.5.0` | `≥ 8.0` | _(not pinned by MAF — BYO via IChatClient)_ | `1.4.0` | `AgentSkillScript` surface changed: `AIFunctionArguments` → `JsonElement?` + `IServiceProvider?`. |
| **1.3.0** | `≥ 10.5.0` | `≥ 8.0` | `≥ 2.8.0-beta.1` | `1.3.0` **(required)** | Major breaking release. Generators package mandatory for executors. |
| 1.2.0 | `≥ 10.5.0` | `≥ 8.0` | `≥ 2.8.0-beta.1` | N/A | No source generator requirement. _Pins corrected 2026-05-13 from earlier stale "≥ 10.3.0 / ≥ 2.6.0" claim — see [Phase W.A finding](../samples/maf-1.2-sample/README.md#phase-wa-finding--maf-1213-public-surface-is-essentially-the-same)._ |
| 1.1.0 | `≥ 10.5.0` | `≥ 8.0` | `≥ 2.8.0-beta.1` | N/A | Same transitive pins as 1.2.0. _Pins corrected 2026-05-13 from earlier stale "≥ 9.4.0 / ≥ 2.4.0" claim._ |
| 1.0.0 | `≥ 10.5.0` | `≥ 8.0` | `≥ 2.8.0-beta.1` | N/A | Initial release. Same transitive pins as 1.2.0 (verified via `dotnet restore` against `Microsoft.Agents.AI 1.0.0`). _Pins corrected 2026-05-13 from earlier stale "≥ 9.0.0 / ≥ 2.0.0" claim._ |

---

## Removed Packages by Version

| Package | Removed In | Replacement |
|---------|-----------|-------------|
| `Microsoft.Agents.AI.DevUI` | _NOT removed — see correction below_ | _(see note)_ |
| `Microsoft.Agents.AI.Hosting` | _NOT removed — see correction below_ | _(see note)_ |

> **Correction (2026-05-13):** the original "Removed in 1.3.0" claim was wrong.
> Both packages are still on nuget.org and continue to publish previews tracking
> the main MAF release stream (latest at time of correction:
> `1.5.0-preview.260507.1` for both). They're preview-only — not promoted to a
> stable release alongside the main `Microsoft.Agents.AI` package — but they
> have NEVER been removed. Customers who relied on these in earlier previews
> should continue using preview-channel versions.

---

## NuGet Package IDs

| Logical name | NuGet Package ID |
|-------------|-----------------|
| Core framework | `Microsoft.Agents.AI` |
| Workflows | `Microsoft.Agents.AI.Workflows` |
| Source generator | `Microsoft.Agents.AI.Workflows.Generators` |
| Extensions.AI | `Microsoft.Extensions.AI` |
| Azure OpenAI | `Azure.AI.OpenAI` |

---

## Version Tracking

The `.maf-version` file at the repository root records the latest MAF version this toolkit's data covers. The `maf-release-watcher` GitHub Actions workflow compares this against the NuGet feed to detect new releases.

Current tracked version: **`1.19.0`** (see `.maf-version`)

---

## Update Process

When a new MAF version ships:

1. `maf-release-watcher` GitHub Actions workflow detects the new version (weekly schedule or manual dispatch)
2. Runs `nuget-diff-analyzer` skill for the version delta
3. Updates this file with a new row
4. Updates `maf-obsolete-api-registry/registry.yaml` with new CS0618 entries
5. Adds a new section to `guides/maf-1.3.0-migration-guide.md` (or creates a new guide for the new version)
6. Creates a PR for human review

---

*Maintained by maf-doctor | See `.github/skills/maf-release-watcher/SKILL.md` for update process*
