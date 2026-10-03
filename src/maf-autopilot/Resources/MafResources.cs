using System.ComponentModel;
using System.Reflection;
using System.Text;
using MafDoctor.Tools;
using ModelContextProtocol.Server;

namespace MafDoctor.Resources;

/// <summary>
/// MCP Resources — expose all MAF knowledge files at stable maf:// URIs.
///
/// Static resources (no parameters):
///   maf://constraints  — hard constraints, always read before any migration task
///   maf://registry     — CS0618 obsolete API registry (YAML)
///   maf://guide        — cumulative, version-keyed MAF migration guide
///
/// Template resource (one per skill):
///   maf://skills?name=&lt;skillName&gt;  — any of the 15 skill SKILL.md files
///</summary>
[McpServerResourceType]
public static class MafResources
{
    private static readonly Assembly _asm = typeof(MafResources).Assembly;

    // ------------------------------------------------------------------ //
    //  Static resources                                                    //
    // ------------------------------------------------------------------ //

    [McpServerResource(UriTemplate = "maf://constraints",
        MimeType = "text/markdown",
        Name = "maf-constraints",
        Title = "MAF Hard Constraints")]
    [Description("The hard constraints that must never be violated during a MAF migration. Always read this before starting any migration task.")]
    public static string GetConstraints() => ReadEmbedded("constraints.md");

    [McpServerResource(UriTemplate = "maf://registry",
        MimeType = "text/yaml",
        Name = "maf-registry",
        Title = "MAF Obsolete API Registry")]
    [Description("Machine-readable registry of known CS0618 obsolete API patterns with deterministic fix patterns.")]
    public static string GetRegistry() => ReadEmbedded("registry.yaml");

    [McpServerResource(UriTemplate = "maf://guide",
        MimeType = "text/markdown",
        Name = "maf-guide",
        Title = "MAF Cumulative Migration Guide")]
    [Description("Cumulative, version-keyed MAF migration reference with API signatures, before/after code patterns, and every reviewed upgrade step through the current tracked release.")]
    public static string GetGuide() => ReadEmbedded("guide.md");

    [McpServerResource(UriTemplate = "maf://migrate-from{?source}",
        MimeType = "text/markdown",
        Name = "maf-migrate-from",
        Title = "MAF cross-framework migration guide")]
    [Description("Migration mapping FROM another agent framework TO MAF (construct-by-construct, with the interop bridges and the bridgeable/rewrite/re-architect strategy per construct). Pass source= one of: semantic-kernel (alias: sk).")]
    public static string GetMigrationFrom(string? source)
        => MigrationSources.Canonicalize(source) switch
        {
            MigrationSources.SemanticKernel => ReadEmbedded("migrate-from/semantic-kernel.md"),
            _ => throw new ArgumentException(
                $"Unknown migration source '{source}'. Available: {MigrationSources.SupportedDescription}"),
        };

    [McpServerResource(UriTemplate = "maf://rules",
        MimeType = "text/markdown",
        Name = "maf-rules",
        Title = "Live catalog of every scanner + analyzer rule")]
    [Description("Machine-readable catalog of every rule the toolkit can enforce. Generated at runtime from the actual code — no drift between docs and implementation. Includes anti-pattern scanner rules (MAF-AP-*), prompt-lint rules (PROMPT-*), and Roslyn analyzer rules (MAF001/002/003).")]
    public static string GetRules() => BuildRulesCatalog();

    [McpServerResource(UriTemplate = "maf://help",
        MimeType = "text/markdown",
        Name = "maf-help",
        Title = "Capability catalogue — what every tool / agent / resource does")]
    [Description("Same content as the `MafTour` tool but as a static resource. Useful when a caller wants the capability inventory without spinning up a tool call. Auto-generated from the same hand-curated catalogue, so the two surfaces never drift.")]
    public static string GetHelp() => Tools.TourTool.BuildFullCatalogue();

    private static string BuildRulesCatalog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MAF rule catalog");
        sb.AppendLine();
        sb.AppendLine("Auto-generated from the live tool surface. If a rule appears here, it ships in the current `maf-doctor` binary.");
        sb.AppendLine();
        sb.AppendLine("## Anti-pattern scanner rules (`MafScanAntiPatterns`)");
        sb.AppendLine();
        sb.AppendLine("| Rule ID | Name | Severity | Skip in test files? |");
        sb.AppendLine("|---|---|---|:---:|");
        foreach (var rule in AntiPatternScannerTool.AllRules)
            sb.AppendLine($"| `{rule.Id}` | {rule.Name} | {rule.Severity} | {(rule.SkipInTestFiles ? "yes" : "no")} |");
        sb.AppendLine();

        sb.AppendLine("## Fan-out validator (`MafValidateFanOut`)");
        sb.AppendLine();
        sb.AppendLine("Built-in single rule: every `[MessageHandler]` method must return `ValueTask<T>` or a synchronous `T` (the source generator rejects `Task<T>` (MAFGENWF002), and `IAsyncEnumerable<T>` fails at run time). `void` / non-generic `Task` / non-generic `ValueTask` → `SilentStarvationRisk`. Raw types → `LikelyInvalid`. See the SARIF emitter for the analyzer-aligned `MAF001` ID.");
        sb.AppendLine();

        sb.AppendLine("## Prompt-lint rules (`MafLintAgentPrompt`)");
        sb.AppendLine();
        sb.AppendLine("| Rule ID | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `PROMPT-001` | Empty `Instructions` (likely accidental). Warning. |");
        sb.AppendLine($"| `PROMPT-002` | Token bloat — `Instructions` literal longer than {PromptLintTool.BloatCharThreshold} chars (~{PromptLintTool.BloatCharThreshold / 4} tokens). Warning. |");
        sb.AppendLine($"| `PROMPT-003` | Substantive prompt (≥{PromptLintTool.RefusalCheckMinLength} chars) with no refusal pattern. Warning. |");
        sb.AppendLine("| `PROMPT-004` | Untrusted-input concatenation — prompt-injection risk. **Error.** |");
        sb.AppendLine();

        sb.AppendLine("## Roslyn analyzer rules (ships as `maf-doctor.Analyzers` NuGet)");
        sb.AppendLine();
        sb.AppendLine("| Rule ID | Description | Same intent as scanner |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| `MAF001` | Fan-out handler must return `ValueTask<T>` or a synchronous `T`. **Warning** (raise to error in `.editorconfig` to fail CI). | `MafValidateFanOut` (runtime/post-hoc) |");
        sb.AppendLine("| `MAF002` | Avoid `DefaultAzureCredential` in production code. Warning. | `MAF-AP-SEC-001` via `MafScanAntiPatterns` |");
        sb.AppendLine("| `MAF003` | `EnableSensitiveData = true` outside test code. Warning. | `MAF-AP-SEC-003` via `MafScanAntiPatterns` |");
        sb.AppendLine();
        sb.AppendLine("_The analyzer rules fire at **write time** in the IDE. The scanner rules fire at **scan time** when you invoke an MCP tool or run a CI check. They detect the same patterns — keep both._");
        sb.AppendLine();

        sb.AppendLine("## How to act on a finding");
        sb.AppendLine();
        sb.AppendLine("- Call `MafRegistryLookup(<entry-id>)` for any `MAF130-*` registry ID surfaced in a finding.");
        sb.AppendLine("- Call `MafApiSafety(<symbol>)` to check whether a specific API is known-unsafe.");
        sb.AppendLine("- Run `MafDoctor(repoPath)` for the aggregated A/B/C/F + top 3 fixes view.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ //
    //  Template resource: individual skill documents                       //
    // ------------------------------------------------------------------ //

    [McpServerResource(UriTemplate = "maf://skills{?name}",
        MimeType = "text/markdown",
        Name = "maf-skill",
        Title = "MAF Skill Document")]
    [Description(
        "A specific MAF skill document. Pass name= one of: " +
        "cs0618-hunter, dotnet-inspect, maf-anti-pattern-scanner, " +
        "maf-fan-out-validator, maf-from-semantic-kernel, maf-issue-reporter, " +
        "maf-migration-guide, maf-migration-plan-creator, maf-migration-retrospective, " +
        "maf-obsolete-api-registry, maf-release-watcher, " +
        "maf-doctor-self-evolution, " +
        "maf-remediation-playbook, maf-workflow-smoke-tester, nuget-diff-analyzer")]
    public static string GetSkill(string name)
    {
        if (!_allowedSkillNames.TryGetValue(name ?? string.Empty, out var canonical))
            throw new ArgumentException(
                // Sort so the "Available:" list is deterministic (HashSet order is not).
                $"Unknown skill '{name}'. Available: {string.Join(", ", _allowedSkillNames.OrderBy(n => n, StringComparer.Ordinal))}");

        // Embedded-resource names are case-sensitive; use the canonical allowlist form
        // so the public API can remain case-insensitive without leaking that asymmetry.
        return ReadEmbedded($"skills/{canonical}.md");
    }

    // ------------------------------------------------------------------ //
    //  Internal helpers                                                    //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The skill allowlist — the security boundary for <c>maf://skills?name=...</c>. Kept
    /// PRIVATE and mutable only here; exposed to the assembly (drift tests) as an immutable
    /// view via <see cref="AllowedSkillNames"/>, so nothing else can add a name to it.
    /// </summary>
    private static readonly HashSet<string> _allowedSkillNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cs0618-hunter",
            "dotnet-inspect",
            "maf-anti-pattern-scanner",
            "maf-fan-out-validator",
            "maf-from-semantic-kernel",
            "maf-issue-reporter",
            "maf-migration-guide",
            "maf-migration-plan-creator",
            "maf-migration-retrospective",
            "maf-obsolete-api-registry",
            "maf-release-watcher",
            "maf-doctor-self-evolution",
            "maf-remediation-playbook",
            "maf-workflow-smoke-tester",
            "nuget-diff-analyzer",
        };

    /// <summary>
    /// A defensive SNAPSHOT of the skill allowlist (for drift tests). A fresh array each
    /// call, so a caller can never reach the backing <see cref="_allowedSkillNames"/> by
    /// down-casting the returned reference — the security boundary stays intact.
    /// </summary>
    internal static IReadOnlyCollection<string> AllowedSkillNames => _allowedSkillNames.ToArray();

    private static string ReadEmbedded(string logicalName)
    {
        using var stream = _asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' not found. " +
                $"Available: {string.Join(", ", _asm.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
