using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafCompatibility
///
/// Returns the compatibility row for a specific MAF version — which .NET runtime,
/// `Microsoft.Extensions.AI`, Azure.AI.OpenAI, and Generators-package versions are
/// required / compatible. Data mirrors `docs/compatibility-matrix.md`, which is
/// the canonical doc; this tool just exposes it programmatically for LLM consumption.
///
/// Salvaged from the May-6 pre-Phase-O sketch (tag: `pre-phase-o-may6-sketch`),
/// rewritten in local PascalCase style + drift-tested against the doc.
/// </summary>
[McpServerToolType]
public sealed class CompatibilityTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Look up compatibility requirements for a specific MAF version: required .NET
        runtime, Azure SDK, Microsoft.Extensions.AI, and Generators-package versions.

        Use this BEFORE bumping MAF in your csproj to confirm your runtime/SDK can host
        the target version. For migration steps between versions, use MafMigrationPath.

        Input:
          - mafVersion: SemVer string, e.g. "1.3.0" / "1.2.0" / "1.1.0" / "1.0.0".

        Returns markdown with: .NET runtime, Azure SDK, Extensions.AI, and Generators
        package version requirements. If the version isn't known, returns the list of
        known versions + a pointer to docs/compatibility-matrix.md.
        """)]
    public string MafCompatibility(
        [Description("MAF version to query, e.g. '1.3.0'.")] string mafVersion)
    {
        if (string.IsNullOrWhiteSpace(mafVersion))
            return "Error: mafVersion must not be empty.";

        var key = mafVersion.Trim();
        if (Matrix.TryGetValue(key, out var info))
            return info;

        return $"""
            No compatibility data for MAF version '{mafVersion}'.

            Known versions: {string.Join(", ", Matrix.Keys.OrderBy(k => k, StringComparer.Ordinal))}

            For up-to-date data:
            - `docs/compatibility-matrix.md` in the maf-doctor repo (canonical source).
            - The `maf-release-watcher` GitHub Actions workflow auto-adds a new row when MAF
              ships a new version; if your version is missing, fire the watcher manually
              (`gh workflow run maf-release-watcher.yml -f maf_version={mafVersion}`).
            """;
    }

    /// <summary>
    /// Static compatibility matrix — exposed `internal static` so the drift test
    /// (in <c>CompatibilityToolTests</c>) can verify each row stays in lockstep with
    /// `docs/compatibility-matrix.md`.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Matrix =
        new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1.18.0"] = """
                ## MAF 1.18.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `≥ 8.0` | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `≥ 10.7.0` | Carried from 1.17.0 |
                | Azure.AI.OpenAI                           | _(not pinned by MAF — BYO via IChatClient)_ | |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.18.0` | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                **Breaking** — review required. API summary: 2 breaking. See `guides/maf-1.18.0-migration-guide.md` and the registry entries. Transitive pins carried from 1.17.0 (verify).
                """,

            ["1.17.0"] = """
                ## MAF 1.17.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `≥ 8.0` | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `≥ 10.7.0` | Carried from 1.16.0 |
                | Azure.AI.OpenAI                           | _(not pinned by MAF — BYO via IChatClient)_ | |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.17.0` | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                **Maintenance release: no public-API or dependency-floor break.** All eight validated in-repository surfaces have empty API diffs, and the tracked .NET/`Microsoft.Extensions.AI` floors remain unchanged from 1.16.0. Runtime behavior does change in the separately shipped Declarative packages: a top-level agent `ErrorContent` now fails the workflow before completion or downstream actions instead of looking like an empty success; hosted error detail remains governed by the host's exception-detail policy. `Microsoft.Agents.AI.DurableTask` and `Microsoft.Agents.AI.Hosting.AzureFunctions` moved to `microsoft/agent-framework-durable-extension` without changing package IDs and now follow that repository's independent release cadence, so the absence of a 1.17-aligned package is not a removal. See `guides/maf-1.17.0-migration-guide.md` and the 1.17 registry entries.
                """,

            ["1.16.0"] = """
                ## MAF 1.16.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `≥ 8.0` | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `≥ 10.7.0` | Raised from 10.6.0 |
                | Azure.AI.OpenAI                           | _(not pinned by MAF — BYO via IChatClient)_ | |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.16.0` | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                **First-party MAF APIs are additive; transitive migration may be required.** Workflows adds Magentic prompt/language configuration. Core raises `Microsoft.Extensions.AI` and `Microsoft.Extensions.VectorData.Abstractions` to 10.7.0; VectorData 9.7→10.7 contains source/API breaks in vector dimensions, filters, generic record constraints, and provider extension points. Behavioral fixes clarify chat-history ownership, tool-approval sessions, FileMemory storage scope, declarative table state, Foundry port binding, and A2A configuration forwarding. GitHub Copilot graduates to stable and LocalCodeAct is a new preview package. See `guides/maf-1.16.0-migration-guide.md` and the 1.16 registry entries.
                """,

            ["1.15.0"] = """
                ## MAF 1.15.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `≥ 8.0` | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `≥ 10.6.0` | Carried from 1.14.0 |
                | Azure.AI.OpenAI                           | _(not pinned by MAF — BYO via IChatClient)_ | |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.15.0` | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                **Breaking in the preview Hosting contract** — ten source-only named-argument changes rename `conversationId:` to `sessionStoreId:` across `AgentSessionStore` and its built-in stores; positional and existing binary calls remain compatible. Custom direct subclasses must implement the new abstract `DeleteSessionAsync`. Custom stores must return independent session snapshots and checkpoint indexes in oldest-to-newest commit order. Stable Core has no public API change. See `guides/maf-1.15.0-migration-guide.md` and the 1.15 registry entries. Transitive pins are carried from 1.14.0.
                """,

            ["1.14.0"] = """
                ## MAF 1.14.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `≥ 8.0` | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `≥ 10.6.0` | Carried from 1.13.0 |
                | Azure.AI.OpenAI                           | _(not pinned by MAF — BYO via IChatClient)_ | |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.14.0` | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                **Breaking** — migration required. The ten validated release-critical surfaces contain 30 API breaks: agent-mode terminology and async session APIs; approval-rule context changes; default-on approval-not-required bypassing and approval-response binding; async message injection; non-null todo sessions; opt-in Harness file access and explicit shell composition; `AddAGUIServer` / `MapAGUIServer`; Copilot function-declaration collections; and a binary-breaking `ShellPolicy` constructor with deny-first semantics. The legacy `Microsoft.Agents.AI.AGUI` package split into `AGUI.Client`, `AGUI.Server`, `AGUI.Abstractions`, `AGUI.Protobuf`, and `AGUI.Formatting`. See `guides/maf-1.14.0-migration-guide.md` and the 1.14 registry entries. Transitive pins are carried from 1.13.0.
                """,

            ["1.13.0"] = """
                ## MAF 1.13.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.6.0`      | Same transitive pin as 1.12.0 |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.13.0`         | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                Breaking (.NET): file-store API renamed — *FileAsync methods removed from AgentFileStore/FileSystemAgentFileStore/InMemoryAgentFileStore; use WriteAsync/ReadAsync/DeleteAsync/ListChildrenAsync/SearchAsync. FileListEntry.FileName→Name. FileAccessProvider tool-name constants renamed. Additive: composable skills-source types; IDisposable on AgentSkillsProvider/AgentSkillsSource/FileAccessProvider. Transitive pins carried from 1.12.0.
                """,

            ["1.12.0"] = """
                ## MAF 1.12.0 Compatibility
                
                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.6.0`      | Same transitive pin as 1.11.1 |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.12.0`         | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |
                
                Breaking (.NET): the skills-source API gained a required `AgentSkillsSourceContext` — `GetSkillsAsync` (on `AgentSkillsSource` / `AgentFileSkillsSource`) and `AgentSkillsProviderBuilder.UseFilter`'s predicate now receive it (positional call sites / overrides break); `AgentSkillsProviderOptions.DisableCaching` was removed (caching moved to the new `CachingAgentSkillsSourceOptions`). Additive: `AgentSkillsSourceContext` / `CachingAgentSkillsSourceOptions` types. Transitive pins carried from 1.11.1.
                """,

            ["1.11.1"] = """
                ## MAF 1.11.1 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.6.0`      | Same transitive pin as 1.11.0 |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.11.1`         | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                Breaking (.NET): `AgentSkillsProvider` tools now require approval by default (#6729) — the opt-in `AgentSkillsProviderBuilder.UseScriptApproval()` and `AgentSkillsProviderOptions.ScriptApproval` were removed (CS0246 for old call sites); `AgentMcpSkillsSource` supports archive-type skills (#6631). Additive: `AgentSkillsProviderBuilder.UseSource`; new `ReadSkillResourceToolName` / `RunSkillScriptToolName` constants.
                """,

            ["1.11.0"] = """
                ## MAF 1.11.0 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.6.0`      | |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.11.0`         | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                Breaking (positional call sites only): `SearchFilesAsync` inserts a `recursive` parameter before `cancellationToken` on `AgentFileStore` / `FileSystemAgentFileStore` / `InMemoryAgentFileStore`. Additive (trailing optional params, source-compatible): `AgentInlineSkill` constructors add `argumentMarshaler`; `TodoProvider` `GetAllTodosAsync` / `GetRemainingTodosAsync` add a trailing `CancellationToken`.
                """,

            ["1.10.0"] = """
                ## MAF 1.10.0 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.6.0`      | |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.10.0`         | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                Breaking: skill `Content`/`Resources`/`Scripts` properties removed; `ToolApprovalAgent` constructor takes `ToolApprovalAgentOptions?` instead of `JsonSerializerOptions?`; `SubAgentsProvider` / `SubTaskInfo` / `SubTaskStatus` removed; `GroupChatWorkflowBuilder`/`MagenticWorkflowBuilder` `.WithName`/`.WithDescription` removed.
                """,

            ["1.6.1"] = """
                ## MAF 1.6.1 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.5.1`      | |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.6.1`          | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                Additive release; adds `expectedOutput` parameter to `WorkflowEvaluationExtensions.EvaluateAsync` for ground-truth evaluation.
                """,

            ["1.5.0"] = """
                ## MAF 1.5.0 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.5.1`      | |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` — consumer chooses the backing implementation |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.5.0`          | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                Additive release; new `ToolApprovalAgent` for human-in-the-loop tool approval flows.
                """,

            ["1.4.0"] = """
                ## MAF 1.4.0 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0, net9.0, net10.0 TFMs all supported |
                | Microsoft.Extensions.AI                   | `>= 10.5.0`      | |
                | Azure.AI.OpenAI                           | _not pinned by MAF_ | BYO via `IChatClient` |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.4.0`          | Source-gen package |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                Breaking: `AgentSkillScript.RunAsync` + `AgentFileSkillScriptRunner.Invoke/BeginInvoke` signatures changed —
                `AIFunctionArguments arguments` → `JsonElement? arguments, IServiceProvider? serviceProvider`.
                """,

            ["1.3.0"] = """
                ## MAF 1.3.0 Compatibility

                | Dependency                                | Version          | Notes |
                |-------------------------------------------|------------------|-------|
                | .NET runtime                              | `>= 8.0`         | net8.0 and net9.0 TFMs both supported |
                | Microsoft.Extensions.AI                   | `>= 10.5.0`      | |
                | Azure.AI.OpenAI                           | `>= 2.8.0-beta.1`| |
                | Microsoft.Agents.AI.Workflows.Generators  | `1.3.0` (required) | Source-gen package — executor partial classes need it |
                | Identity                                  | `ManagedIdentityCredential` | NEVER `DefaultAzureCredential` in prod (analyzer rule MAF002) |

                **Note on optional packages**: `Microsoft.Agents.AI.DevUI` and
                `Microsoft.Agents.AI.Hosting` remain on NuGet as preview-only
                channels (latest `1.5.0-preview.260507.1`). The earlier claim
                "Removed in 1.3.0" was incorrect.
                """,

            ["1.2.0"] = """
                ## MAF 1.2.0 Compatibility

                | Dependency                | Version          | Notes |
                |---------------------------|------------------|-------|
                | .NET runtime              | `>= 8.0`         | |
                | Microsoft.Extensions.AI   | `>= 10.5.0`      | _Pins corrected 2026-05-13 from earlier stale "≥ 10.3.0" claim — see [Phase W.A finding](../../samples/maf-1.2-sample/README.md)._ |
                | Azure.AI.OpenAI           | `>= 2.8.0-beta.1`| 2.6.0/2.7.0 stable were never published. |
                | Generators package        | N/A              | Not required at this version. |
                """,

            ["1.1.0"] = """
                ## MAF 1.1.0 Compatibility

                | Dependency                | Version          | Notes |
                |---------------------------|------------------|-------|
                | .NET runtime              | `>= 8.0`         | |
                | Microsoft.Extensions.AI   | `>= 10.5.0`      | Same transitive pin as 1.2.0 (pins corrected 2026-05-13). |
                | Azure.AI.OpenAI           | `>= 2.8.0-beta.1`| |
                | Generators package        | N/A              | Not required at this version. |
                """,

            ["1.0.0"] = """
                ## MAF 1.0.0 Compatibility

                | Dependency                | Version          | Notes |
                |---------------------------|------------------|-------|
                | .NET runtime              | `>= 8.0`         | |
                | Microsoft.Extensions.AI   | `>= 10.5.0`      | Same transitive pin as 1.2.0 (pins corrected 2026-05-13). |
                | Azure.AI.OpenAI           | `>= 2.8.0-beta.1`| |
                | Sessions                  | `AgentSession` (`ChatClientAgentSession`) | Public surface confirms `AgentThread` was **never** in the 1.0.0 public NuGet — see Phase W.A registry chronology finding. |
                """,
        };
}
