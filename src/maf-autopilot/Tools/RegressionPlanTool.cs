using System.ComponentModel;
using System.Text;
using MafDoctor.Data;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: <c>MafGenerateRegressionPlan</c>.
///
/// Given <c>(fromVersion, toVersion)</c>, produces a Mermaid-formatted
/// migration roadmap covering every intermediate version step + the
/// registry entries that fire at each step.
///
/// **The killer multi-version-migration demo piece.** A customer pinned
/// to MAF 1.0.0 wanting to land at 1.5.0 sees a 5-step roadmap with
/// per-step counts of changes, not a single intimidating diff. Each
/// node is clickable in renderers that support Mermaid `click` directives,
/// jumping to the corresponding per-version migration guide.
///
/// Phase W.12 (2026-05-13). Depends on:
///   - W.1   (`applies_to_codebases` field) for per-codebase filtering.
///   - W.8/W.10 (1.2 + 1.0 samples) — used in tests as known fixtures.
/// </summary>
[McpServerToolType]
public sealed class RegressionPlanTool
{
    /// <summary>
    /// The version ladder, derived from the single maintained source
    /// (<see cref="CompatibilityTool.Matrix"/>) instead of a second hard-coded list.
    /// REP-01: the old frozen list stopped at 1.5.0 while MAF is at 1.13.0, so roadmaps
    /// silently omitted every released version above 1.5.0 (including the 1.6.1 / 1.11.1
    /// patch steps). The Matrix keys are exactly the released versions the release-watcher
    /// keeps current; a drift test pins the two together. Sorted by real version order.
    /// </summary>
    internal static string[] KnownVersions =>
        CompatibilityTool.Matrix.Keys.OrderBy(v => Version.Parse(v)).ToArray();

    /// <summary>Per-step migration guides exist from this version on; below it, no guide file ships.</summary>
    private static readonly Version FirstGuidedVersion = new(1, 3, 0);

    private readonly RegistryService _registry;

    public RegressionPlanTool(RegistryService registry) => _registry = registry;

    public RegressionPlanTool() : this(new RegistryService()) { }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Generate an ordered Mermaid migration roadmap from `fromVersion` to
        `toVersion`. Walks every intermediate version, lists the registry
        entries that fire at each step, and emits both a Mermaid diagram
        + a per-step markdown summary table.

        Input:
          - fromVersion: SemVer string (e.g. "1.0.0").
          - toVersion:   SemVer string (e.g. "1.5.0").

        Returns a markdown document. If `fromVersion == toVersion`, an
        empty-plan note. If `fromVersion > toVersion`, an error.
        """)]
    public string MafGenerateRegressionPlan(
        [Description("Source version pinned by the customer codebase (e.g. '1.0.0').")] string fromVersion,
        [Description("Target version they want to migrate TO (e.g. '1.5.0').")] string toVersion)
    {
        if (!TryParseVersion(fromVersion, out var fromV))
            return $"❌ fromVersion `{fromVersion}` is not a valid SemVer.";
        if (!TryParseVersion(toVersion, out var toV))
            return $"❌ toVersion `{toVersion}` is not a valid SemVer.";
        if (fromV == toV)
            return "_Empty plan: fromVersion and toVersion are identical._";
        if (fromV > toV)
            return $"❌ fromVersion `{fromVersion}` is HIGHER than toVersion `{toVersion}`. " +
                   "This tool generates forward migrations only; use `@maf-rollback` for downgrades.";

        // Build the ordered list of version steps between from and to.
        var steps = KnownVersions
            .Where(v => TryParseVersion(v, out var parsed) && parsed > fromV && parsed <= toV)
            .ToList();
        if (steps.Count == 0)
            return $"_No known intermediate versions between {fromVersion} and {toVersion}. " +
                   "The KnownVersions ladder in `RegressionPlanTool` may need updating._";

        var sb = new StringBuilder();
        sb.AppendLine($"# Migration roadmap: MAF {fromVersion} → {toVersion}");
        sb.AppendLine();
        sb.AppendLine($"**{steps.Count} version step(s)** between source and target. " +
                      "Each step is independently testable. Migrate one step at a time, " +
                      "running `dotnet build` + the test suite after each.");
        sb.AppendLine();

        // -------- Mermaid diagram --------
        sb.AppendLine("## Migration graph");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine("flowchart LR");

        var nodeId = SanitizeNodeId(fromVersion);
        sb.AppendLine($"    {nodeId}([{fromVersion}<br/><i>source</i>])");
        var prevId = nodeId;

        var stepSource = fromVersion;
        foreach (var step in steps)
        {
            var stepId = SanitizeNodeId(step);
            var entriesAtStep = EntriesForStep(stepSource, step).Count;
            stepSource = step;
            var label = entriesAtStep > 0
                ? $"{step}<br/>{entriesAtStep} change(s)"
                : $"{step}<br/>(no registry changes)";
            sb.AppendLine($"    {stepId}([{label}])");
            sb.AppendLine($"    {prevId} --> {stepId}");
            prevId = stepId;
        }
        sb.AppendLine("```");
        sb.AppendLine();

        // -------- Per-step detail table --------
        sb.AppendLine("## Per-step changes");
        sb.AppendLine();
        sb.AppendLine("| Step | Target version | Registry IDs that fire | Guide section |");
        sb.AppendLine("|---|---|---|---|");

        stepSource = fromVersion;
        foreach (var step in steps)
        {
            var entriesAtStep = EntriesForStep(stepSource, step);
            stepSource = step;
            var ids = entriesAtStep.Count > 0
                ? string.Join(", ", entriesAtStep.Select(e => $"`{e.Id}`"))
                : "_(none — minor release or polish)_";
            // REP-01: guides ship only from 1.3.0 on; below that, emit no dead link.
            var guide = Version.TryParse(step, out var sv) && sv >= FirstGuidedVersion
                ? $"[maf-{step}-migration-guide.md](../guides/maf-{step}-migration-guide.md)"
                : "_(no per-step guide)_";
            sb.AppendLine($"| {steps.IndexOf(step) + 1} | **{step}** | {ids} | {guide} |");
        }
        sb.AppendLine();

        // -------- Footer --------
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("**To execute this plan:**");
        sb.AppendLine();
        sb.AppendLine("1. Bump the MAF pin in your csproj to the FIRST intermediate version above (not the target).");
        sb.AppendLine("2. Run `maf-doctor doctor .` against your codebase. Apply `MafAutoFix` for each rule the doctor flags.");
        sb.AppendLine("3. `dotnet build` + run your test suite.");
        sb.AppendLine("4. Repeat 1-3 for each subsequent version step in the table above.");
        sb.AppendLine("5. After the last step, you should be on the target version with a clean build + green tests.");

        return sb.ToString();
    }

    /// <summary>
    /// Strips any prerelease tag (-alpha-N, -beta, -rc, -preview) and parses
    /// the SemVer core. Returns false if the input doesn't parse — caller is
    /// responsible for surfacing an error.
    /// </summary>
    internal static bool TryParseVersion(string raw, out Version version)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            version = new Version(0, 0);
            return false;
        }
        var dashIdx = raw.IndexOf('-');
        var core = dashIdx > 0 ? raw.Substring(0, dashIdx) : raw;
        return Version.TryParse(core, out version!);
    }

    /// <summary>
    /// Make a SemVer string safe to use as a Mermaid node ID:
    /// "1.3.0" → "v130". Dots aren't valid in node IDs.
    /// </summary>
    /// <summary>
    /// Registry entries a codebase meets when it moves from <paramref name="source"/>
    /// to <paramref name="step"/>: introduced at the step, and applicable to a
    /// codebase still on the source version. Migration entries are marked
    /// <c>pre-STEP</c> (they describe code written before the change), so
    /// evaluating them against the TARGET version dropped every one of them.
    /// </summary>
    private List<RegistryEntry> EntriesForStep(string source, string step) =>
        _registry.GetEntriesForCodebaseVersion(source)
            .Where(e => e.VersionIntroduced == step)
            .ToList();

    internal static string SanitizeNodeId(string version)
    {
        var dashIdx = version.IndexOf('-');
        var core = dashIdx > 0 ? version.Substring(0, dashIdx) : version;
        return "v" + core.Replace(".", "");
    }
}
