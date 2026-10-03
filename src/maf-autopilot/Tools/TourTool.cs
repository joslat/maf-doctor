using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafTour
///
/// Guided "what can I do here?" catalogue. Returns a structured markdown report
/// of every capability the toolkit exposes: tools, agents, resources, prompts.
/// The primary agent (`@maf`) calls this on first-touch / "help" intent so users
/// don't have to read the README to discover what's available.
///
/// Content is hand-curated (drift-tested in <c>MafTourToolTests</c> so it stays
/// in lockstep with the live tool surface).
/// </summary>
[McpServerToolType]
public sealed class TourTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        List every maf-doctor capability — MCP tools, agents, resources, prompts —
        with a one-line "when to use" each. Use this when a new user asks "what can
        this thing do?". For the same content as a static resource, read maf://help.

        Input:
          - section: optional filter — "tools" | "agents" | "resources" | "prompts" | "all"
                     (default: "all").

        Returns markdown grouped by section with one-line "when to use" per item.
        """)]
    public string MafTour(
        [Description("Filter: \"tools\" | \"agents\" | \"resources\" | \"prompts\" | \"all\" (default: all).")]
        string section = "all")
    {
        var s = string.IsNullOrWhiteSpace(section) ? "all" : section.Trim().ToLowerInvariant();
        if (s is not ("all" or "tools" or "agents" or "resources" or "prompts"))
            return $"Error: section must be \"tools\", \"agents\", \"resources\", \"prompts\", or \"all\" (got \"{section}\").";

        return BuildCatalogue(s);
    }

    /// <summary>
    /// Full catalogue, all sections — what `maf://help` resource returns.
    /// Kept in lockstep with <see cref="MafTour"/> by routing through the same builder.
    /// </summary>
    public static string BuildFullCatalogue() => BuildCatalogue("all");

    private static string BuildCatalogue(string section)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 🗺️ maf-doctor — capability tour");
        sb.AppendLine();
        sb.AppendLine("> Single-page catalogue of what this MCP server exposes. Use this to answer \"what tool / agent fits my task?\".");
        sb.AppendLine();

        if (section is "all" or "tools") AppendTools(sb);
        if (section is "all" or "agents") AppendAgents(sb);
        if (section is "all" or "resources") AppendResources(sb);
        if (section is "all" or "prompts") AppendPrompts(sb);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("**Next:** invoke the `maf-help` prompt for a 3-question interactive flow, or run a prompt directly (`maf-migrate`, `maf-audit`, etc.) — no install needed. The `@`-mention specialists (`@maf-migration`, `@maf-auditor`, etc.) work the same way if `.github/agents/` was also copied in from the source repo (GitHub Copilot only).");

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Catalogue data — hand-curated, drift-tested
    // -------------------------------------------------------------------------

    /// <summary>
    /// The canonical capability table. Drift-tested: every `[McpServerTool]` in
    /// the assembly must appear in <see cref="ToolCatalogue"/>; CI fails otherwise.
    /// </summary>
    internal static readonly IReadOnlyList<ToolEntry> ToolCatalogue = new ToolEntry[]
    {
        // — Registry / lookup —
        new("MafApiSafety", "Registry lookup", "Is this MAF API call safe in the latest MAF version? Returns the registry entry if the symbol is known-obsolete; SAFE otherwise. Pure read."),
        new("MafRegistryLookup", "Registry lookup", "Pull the full registry entry by ID (e.g. `MAF130-THREAD-001`). Use after `MafApiSafety` finds a hit."),
        new("MafRegistryList", "Registry lookup", "Enumerate every registry entry ID. Useful for discovery and CI auditing."),

        // — Scanners —
        new("MafScanAntiPatterns", "Scanner", "Walk a repo for known anti-patterns (security / concurrency / observability / identity / topology). 11 rules. Supports `format: \"sarif\"` for CI."),
        new("MafValidateFanOut", "Scanner", "Find handlers that return `void` / non-generic `Task` (silent fan-in starvation risk). Supports `format: \"sarif\"`."),
        new("MafLintAgentPrompt", "Scanner", "Lint every agent `Instructions` literal. 4 rules: empty / token bloat / missing refusal / untrusted-input concat (prompt injection)."),
        new("MafEstimateCost", "Scanner", "Find every `RunAsync` / `RunStreamingAsync` site missing a `MaxOutputTokens` cap. Production cost governance."),

        // — Build-verified —
        new("MafRunCs0618Hunt", "Build-verified", "Shell `dotnet build`, parse CS0618/CS0246 diagnostics, join each to the obsolete-API registry. Returns deterministic fixes."),

        // — Workflow analysis —
        new("MafSimulateWorkflow", "Workflow", "Emit a Mermaid topology diagram + per-edge completion forecast. See your workflow without running it."),
        new("MafExplain", "Snippet analysis", "Annotate a MAF code snippet line-by-line. Cross-references the registry and guide sections."),

        // — Scaffolders —
        new("MafNewAgent", "Scaffolder", "Generate a clean `ChatClientAgent`-based class + hermetic xUnit test. Anti-pattern-clean by construction."),
        new("MafNewExecutor", "Scaffolder", "Generate a fan-out-safe `[MessageHandler]` executor with `ValueTask<T>` return + reflection-based shape test."),

        // — Auto-fix (Phase W.6/W.7/W.11) —
        new("MafAutoFix", "Auto-fix", "Deterministic per-rule Roslyn rewriter — the \"do\" half of the toolkit. Supports MAF-AP-SEC-001/MAF002 (DefaultAzureCredential), MAF-AP-SEC-003/MAF003 (EnableSensitiveData), MAF-AP-WF-001 (sealed Executor), MAF130-FAN-IN-001 (arg order), MAF-AP-CONC-002 (.Result/.Wait()). `dryRun: true` previews without writing."),
        new("MafAutoFixAll", "Auto-fix", "The \"fix everything fixable\" batch command. Runs every supported rewriter in dependency-safe order (sealed → EnableSensitiveData → DefaultAzureCredential → fan-in arg swap → sync-over-async). Returns aggregate JSON with per-rule breakdown + total distinct files changed."),
        new("MafBeforeAfter", "Auto-fix", "Phase W.7 preview tool. Given a comma-separated list of rule IDs, dry-runs each MafAutoFix rewriter across the codebase and renders one consolidated markdown report with `diff --git`-style unified diffs per file. Maintainer reviews ONE document before approving the full change-set."),
        new("MafScoreMigrationRisk", "Risk verdict", "Phase W.11 pre-migration risk score. Weighted sum of detected anti-patterns + silent-starvation risks + CS0246/0117/0618 source-text matches → HARD / MEDIUM / EASY verdict with per-category breakdown + recommended migration approach. Run BEFORE starting a migration to set expectations."),
        new("MafGenerateRegressionPlan", "Migration planning", "Phase W.12 multi-version migration roadmap. Given (fromVersion, toVersion), emits a Mermaid flowchart with one node per intermediate MAF version + the registry entries that fire at each step + per-step execution instructions. Killer demo for customers migrating across multiple MAF versions."),

        // — PR-scoped —
        new("MafAuditPullRequest", "PR-scoped", "Scope every scanner to the `.cs` files changed in this branch vs base. CI-comment-ready markdown."),

        // — Upgrade planning —
        new("MafPreUpgradeDryRun", "Upgrade", "\"What would break if I upgraded X to Y?\" — combines `MafDiffPackage` with repo grep. Run before any version bump."),
        new("MafDiffPackage", "Upgrade", "Wrap the supported `dotnet-inspect 0.9.1 diff` format for two NuGet versions. Surfaces every API change."),
        new("MafMigrationPath", "Upgrade", "Multi-step migration planner — walks version-keyed guide metadata, returns ordered intermediate-step sections."),
        new("MafDetectSourceFramework", "Cross-framework migration", "Inventory Semantic Kernel usage and tag each construct by migration strategy (🌉 bridgeable / 🔁 rewrite / 🏗 re-architect) + an EASY/MEDIUM/HARD verdict. The work-list for the `maf-migrate-from` flow. CLI: `maf-doctor migrate-scan`. Mappings: `maf://migrate-from?source=semantic-kernel`."),

        // — Health —
        new("MafDoctor", "Health", "Single-command A/B/C/F grade. Aggregates 4 scanners + reports the top 3 fixes (or every finding with `full: true`). Best triage signal in the toolkit."),
        new("MafDoctorStatus", "Health", "Check whether the installed maf-doctor package is current and whether this workspace needs only a fresh `maf-doctor init`. Returns exact commands when action is needed."),
        new("MafExplainFinding", "Health", "Deep-dive ONE doctor finding (file + line): the offending code in context plus the rule's why / fix / auto-fixability. Grounded substrate for explaining or fixing it — pair with the `maf-explain-finding` prompt."),

        // — Discovery (this very tool) —
        new("MafTour", "Discovery", "This catalogue. Returns every capability with one-line descriptions. Use as the first stop for new users."),

        // — Compatibility lookup —
        new("MafCompatibility", "Compatibility", "Compatibility row for a specific MAF version — .NET runtime, Extensions.AI, Azure.AI.OpenAI, Generators-package versions. Source of truth is `docs/compatibility-matrix.md`."),

        // — Feedback —
        new("MafDraftIssue", "Feedback", "Assemble a `microsoft/agent-framework` GitHub issue body — version + repro + suggested workaround. Output is markdown; user reviews and posts."),
    };

    internal static readonly IReadOnlyList<AgentEntry> AgentCatalogue = new AgentEntry[]
    {
        new("@maf", "Primary triage. Picks the right tool / specialist for the user's intent. NEVER auto-handoffs — always recommends. Start here.", IsPrimary: true),
        new("@maf-migration", "Build-verified task-by-task MAF version migration. Loads the plan, executes one row at a time, gates on `dotnet build` green."),
        new("@maf-cross-migration", "Cross-framework port: Semantic Kernel → MAF (NOT a version bump). Inventories SK usage, classifies each construct (bridge / rewrite / re-architect), scaffolds a new MAF project beside the original, and ports it construct-by-construct. Non-destructive."),
        new("@maf-auditor", "Pre-migration plan generator. Scans the codebase, cross-references the registry + constraints, produces `migration-plan.md`."),
        new("@maf-best-practice-reviewer", "Steady-state audit on a clean MAF codebase. Produces `audit-report.md`. Use post-migration."),
        new("@maf-incident-responder", "Production failure → MAF pattern responsible → deterministic fix. Maps symptom (silent exit / auth failure / cost spike) to known-bad pattern."),
        new("@maf-rollback", "Surgical 1.3.0 → 1.2.0 retreat. Inverse of migration. Preserves unrelated work that landed on top."),
        new("@maf-onboarding", "Personalised codebase tour for a new dev. Topology + top-touched files + dialect."),
    };

    internal static readonly IReadOnlyList<(string Uri, string Description)> ResourceCatalogue = new[]
    {
        ("maf://constraints", "Hard constraints + breaking-changes table. Always-loaded by every agent."),
        ("maf://guide", "Cumulative, version-keyed MAF migration guide through the current tracked release. Read on demand."),
        ("maf://registry", "Machine-readable obsolete-API registry YAML."),
        ("maf://rules", "Live rule catalogue — anti-pattern + prompt-lint + analyzer + cross-references. Auto-generated from runtime data."),
        ("maf://help", "Same content as `MafTour()` but as a resource — useful for static discovery without a tool call."),
        ("maf://migrate-from?source=semantic-kernel", "Semantic Kernel → MAF construct mapping + interop bridges. The work-list for `MafDetectSourceFramework` / the `maf-migrate-from` flow."),
        ("maf://skills?name=<X>", "Individual skill SKILL.md by name. 15 skills available."),
    };

    internal static readonly IReadOnlyList<(string Name, string Description)> PromptCatalogue = new[]
    {
        ("maf-audit", "Starter prompt for the auditor agent. Generates a migration plan."),
        ("maf-migrate", "Starter prompt for the migration agent. Executes specific tasks from an existing plan."),
        ("maf-remediate", "Fix-it-all conductor: grade + plan → MafAutoFixAll (mechanical) → verify each heuristic (possible false-positive) finding before fixing → build → re-grade after each pass until zero actionable findings remain (false positives / human-review items excepted); the grade alone is not the stop signal."),
        ("maf-migrate-from", "Migrate a Semantic Kernel codebase TO MAF, side-by-side: detect SK usage → classify each construct (bridge / rewrite / re-architect) → plan → scaffold a new MAF project beside the original → port construct-by-construct with build gates. Non-destructive."),
        ("maf-cs0618-hunt", "Starter prompt for finding + fixing CS0618 obsolete-API warnings."),
        ("maf-review", "Best-practices code review starter — day-to-day development, PR review, post-migration validation. Routes to MafDoctor / MafScanAntiPatterns / MafValidateFanOut."),
        ("maf-debug", "Reactive-debugging starter — paste an error or symptom, the prompt routes to the right diagnostic tool by symptom class."),
        ("maf-explain-finding", "Deep-dive + fix ONE doctor finding (file + line). Calls MafExplainFinding for grounded context, then explains + proposes the fix using maf://constraints + maf://guide."),
        ("maf-scaffold", "Boilerplate generation starter — routes to MafNewAgent or MafNewExecutor depending on what you want to build."),
        ("maf-onboarding", "Guided first-day tour for a developer new to this MAF codebase: what it does, agent topology (Mermaid), hot-path files, this codebase's MAF dialect, and first-day actions."),
        ("maf-help", "3-question interactive flow for new users. Routes to the right prompt / tool based on intent."),
    };

    // -------------------------------------------------------------------------
    // Formatting
    // -------------------------------------------------------------------------

    private static void AppendTools(StringBuilder sb)
    {
        sb.AppendLine($"## 🔧 MCP tools ({ToolCatalogue.Count})");
        sb.AppendLine();
        sb.AppendLine("| Tool | Category | When to use |");
        sb.AppendLine("|---|---|---|");
        foreach (var t in ToolCatalogue)
            sb.AppendLine($"| `{t.Name}` | {t.Category} | {t.WhenToUse} |");
        sb.AppendLine();
    }

    private static void AppendAgents(StringBuilder sb)
    {
        sb.AppendLine($"## 🤖 Copilot Chat agents ({AgentCatalogue.Count})");
        sb.AppendLine();
        sb.AppendLine("| Agent | Role |");
        sb.AppendLine("|---|---|");
        foreach (var a in AgentCatalogue)
        {
            var marker = a.IsPrimary ? " **(primary — start here)**" : "";
            sb.AppendLine($"| `{a.Name}` | {a.Role}{marker} |");
        }
        sb.AppendLine();
    }

    private static void AppendResources(StringBuilder sb)
    {
        sb.AppendLine($"## 📄 MCP resources ({ResourceCatalogue.Count})");
        sb.AppendLine();
        sb.AppendLine("| URI | Contents |");
        sb.AppendLine("|---|---|");
        foreach (var (uri, desc) in ResourceCatalogue)
            sb.AppendLine($"| `{uri}` | {desc} |");
        sb.AppendLine();
    }

    private static void AppendPrompts(StringBuilder sb)
    {
        sb.AppendLine($"## 💬 MCP prompts ({PromptCatalogue.Count})");
        sb.AppendLine();
        sb.AppendLine("| Prompt | Purpose |");
        sb.AppendLine("|---|---|");
        foreach (var (name, desc) in PromptCatalogue)
            sb.AppendLine($"| `{name}` | {desc} |");
        sb.AppendLine();
    }

    public sealed record ToolEntry(string Name, string Category, string WhenToUse);
    public sealed record AgentEntry(string Name, string Role, bool IsPrimary = false);
}
