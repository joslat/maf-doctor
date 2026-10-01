using MafDoctor.Data;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafApiSafety
///
/// Answers the question: "Is this MAF API safe to use in the latest MAF version?"
///
/// Historically motivated by richlander/dotnet-inspect#316:
/// dotnet-inspect &lt;= v0.7.7 did not surface [Obsolete] at the individual overload level.
/// As of dotnet-inspect v0.7.8 (PR #318), [Obsolete] is surfaced in member listings —
/// but this tool remains the canonical answer because the registry also encodes
/// fix patterns, runtime-only failure classes (e.g., fan-out silent starvation),
/// and project-local invariants that no static inspector can know.
/// </summary>
[McpServerToolType]
public sealed class ApiSafetyTool
{
    private readonly RegistryService _registry;

    public ApiSafetyTool(RegistryService registry)
    {
        _registry = registry;
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Check whether a single MAF API symbol is safe to use — returns SAFE or UNSAFE
        with the exact fix recipe from the curated registry.

        Use this tool when you have ONE symbol name (method, type, or partial call) and want
        to know if it's known-broken. For full-project compiler-driven scanning, use
        MafRunCs0618Hunt. For looking up a known registry entry by ID (e.g.,
        MAF130-FAN-IN-001), use MafRegistryLookup.

        Input can be:
          - A method name:              "AddFanInBarrierEdge"
          - A type name:                "AgentThread"
          - A partial call expression:  "agent.SerializeSession"
          - A full class.method:        "WorkflowBuilder.AddFanInBarrierEdge"

        Optional repoPath: the project's MAF package versions are compared with the
        registry's coverage horizon. If the project references a newer MAF than the
        registry covers, "no known issues" is returned as UNKNOWN, not SAFE.

        Returns SAFE (no known registry issues), UNKNOWN (no known issues, but the project
        is past the registry's coverage), or UNSAFE (with entry ID, fix description,
        before/after code, and guide section). Only covers known registry entries — also
        run the compiler for a complete check.
        """)]
    public string MafApiSafety(
        [Description("API name to check — method name, type name, or partial call expression.")] string apiName,
        [Description("Optional absolute path to the project or repository root. When given, the project's MAF package versions are checked against the registry's coverage horizon; past it, 'no known issues' is reported as UNKNOWN instead of SAFE.")]
        string? repoPath = null)
    {
        if (string.IsNullOrWhiteSpace(apiName))
            return "Error: apiName must not be empty.";

        CoverageGap? gap = null;
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            if (PathGuard.ValidateRepoPath(repoPath) is { } err)
                return err;
            gap = CoverageHorizon.Evaluate(repoPath, _registry.TargetVersion);
        }

        var matches = _registry.SearchByApiName(apiName.Trim());

        if (matches.Count == 0)
        {
            // U-01: "no known issues" is only a SAFE verdict inside the registry's coverage.
            if (gap is not null)
            {
                return $"""
                    ❔ UNKNOWN — No known issues for '{apiName}' up to MAF {_registry.TargetVersion}, but this project references MAF {gap.ProjectMafVersion}.

                    {gap.ToMarkdown()}

                    Until then, rely on the compiler:
                        dotnet build 2>&1 | Select-String "warning CS0618|error CS0246"
                    """;
            }

            return $"""
                ✅ SAFE — No known issues for '{apiName}' in MAF {_registry.TargetVersion}.

                The registry has {_registry.AllIds.Count} entries as of {_registry.LastUpdated}.
                This covers all CS0618/CS0246 patterns discovered in real migrations.
                Coverage ends at MAF {_registry.TargetVersion}: if your project references a newer MAF,
                treat this as UNKNOWN (pass repoPath to check automatically).

                Note: This checks the *known* registry only. Always run the compiler for a full check:
                    dotnet build 2>&1 | Select-String "warning CS0618|error CS0246"
                """;
        }

        var horizonNote = gap is null ? string.Empty : Environment.NewLine + gap.ToMarkdown() + Environment.NewLine;

        if (matches.Count == 1)
        {
            return $"""
                ❌ UNSAFE — '{apiName}' matches a known issue in MAF {_registry.TargetVersion}:

                {RegistryService.FormatEntry(matches[0])}
                """ + horizonNote;
        }

        // Multiple matches — show a summary list and let the caller drill in with MafRegistryLookup.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"⚠️ MULTIPLE MATCHES — '{apiName}' matches {matches.Count} registry entries in MAF {_registry.TargetVersion}:");
        sb.AppendLine();
        foreach (var entry in matches)
        {
            sb.AppendLine($"  • **{entry.Id}** — `{entry.ObsoleteSignature}` → `{entry.ReplacementSignature}`");
            sb.AppendLine($"    Warning: `{entry.CsWarning}` | Guide section: {entry.GuideSection}");
        }
        sb.AppendLine();
        sb.AppendLine("Use `MafRegistryLookup` with a specific entry ID for the full fix details.");
        if (gap is not null)
        {
            sb.AppendLine();
            sb.AppendLine(gap.ToMarkdown());
        }
        return sb.ToString();
    }
}
