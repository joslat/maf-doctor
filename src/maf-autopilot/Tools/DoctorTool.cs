using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafDoctor
///
/// Single-command repo health letter. Aggregates the results of every other
/// scanner — anti-pattern scan, fan-out validator — and produces an A/B/C/F
/// grade plus the top 3 fixes to address. The demo headline tool.
/// </summary>
[McpServerToolType]
public sealed class DoctorTool
{
    private readonly FanOutValidatorTool _fanOut = new();
    private readonly AntiPatternScannerTool _scanner = new();

    // U-01: the registry's coverage horizon (honors MAF_REGISTRY_PATH). Loaded lazily
    // so the parameterless constructor used by DI, the CLI and tests is unchanged; a
    // registry that cannot load disables the horizon check instead of failing doctor.
    private readonly Lazy<string?> _registryTarget = new(() =>
    {
        try { return new Data.RegistryService().TargetVersion; }
        catch { return null; }
    });

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Run every MAF anti-pattern scanner and fan-out validator across the repo
        and produce a single A/B/C/F health letter with the top 3 fixes to address
        (or every finding, grouped by rule, when full: true).

        Grading (errors = anti-pattern + prompt-lint errors):
          A — no errors, no silent-starvation risks, zero warnings/info notes/uncapped agent call sites
          B — no errors, no silent-starvation risks, one or more warnings/info notes/uncapped call sites
          C — 1–3 errors OR 1 silent-starvation risk
          F — 4+ errors OR 2+ silent-starvation risks

        Input:
          - repoPath: absolute path to the repository root.
          - format: output format — 'markdown' (default, human-readable), 'json'
            (machine-readable for CI/dashboards; schema in docs/output-schemas.md),
            'plan' (an ordered, checkboxed remediation plan covering every finding —
            Phase 1 batches the auto-fixable ones into one autofix-all command,
            Phase 2 lists the semantic fixes as impact-ordered tasks), or 'plan-json'
            (the same phased plan as a machine-readable manifest). An unrecognized
            format returns a structured error, not a silent markdown fallback.
          - full: when true, list EVERY finding (grouped by rule, ordered by
            impact) instead of only the top 3 — full triage / CI. (Ignored for
            format 'plan', which always covers everything.)

        Returns a markdown report (default) or JSON. Treat as advisory — fix the
        top items, re-run.
        """)]
    public string MafDoctor(
        [Description("Absolute path to the repository root.")] string repoPath,
        [Description("Output format: 'markdown' (default, human-readable), 'json' (machine-readable for CI/dashboards), 'plan' (an ordered, checkboxed remediation plan to drop into a GitHub issue), or 'plan-json' (the plan as a machine-readable manifest). Unknown values return a structured error.")]
        string format = "markdown",
        [Description("When true, list EVERY finding (grouped by rule, ordered by impact) instead of only the top 3 fixes. Use for full triage / CI.")]
        bool full = false)
        => RunCore(repoPath, format, excludes: null, full: full, out _);

    /// <summary>
    /// CLI entry point. Identical to <see cref="MafDoctor"/> but accepts
    /// repo-relative path substrings to exclude from the scan. Used by the
    /// drift detector to skip <c>samples/</c> and test fixtures so the grade
    /// reflects product code rather than the repo's intentional anti-pattern
    /// bait. Deliberately NOT an MCP tool — keeping it off the tool surface
    /// preserves the stable MafDoctor schema for clients.
    /// </summary>
    internal string Run(string repoPath, string format, IReadOnlyList<string>? excludes, bool full = false)
        => RunCore(repoPath, format, excludes, full, out _);

    /// <summary>
    /// Run and also expose the graded <see cref="DoctorSummary"/> (grade + scan
    /// completeness) so a CLI gate (<c>doctor --fail-on</c>, REP-09) can act on the
    /// real grade without re-scanning or scraping the headline. <paramref name="summary"/>
    /// is <see langword="null"/> when the path was invalid (no scan ran).
    /// </summary>
    internal string Run(string repoPath, string format, IReadOnlyList<string>? excludes, bool full, out DoctorSummary? summary)
        => RunCore(repoPath, format, excludes, full, out summary);

    private string RunCore(string repoPath, string format, IReadOnlyList<string>? excludes, bool full, out DoctorSummary? summary)
    {
        summary = null;

        // Render a validation failure in the REQUESTED format — a machine consumer
        // asking for --json must get JSON back, not a plain-text string that
        // breaks their parser.
        if (PathGuard.ValidateRepoPath(repoPath) is { } err)
        {
            // Any JSON-shaped format must get a JSON error, not a plain-text string.
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase)
                || format.Equals("plan-json", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(
                    new DoctorJsonError("1", err), DoctorJsonContext.Default.DoctorJsonError);
            if (format.Equals("plan", StringComparison.OrdinalIgnoreCase))
                return $"# 🩺 MAF remediation plan\n\n⚠️ Cannot scan: {err}\n";
            return err;
        }

        var s = AnalyzeRepo(repoPath, excludes);
        summary = s;
        var gap = _registryTarget.Value is { } target ? CoverageHorizon.Evaluate(repoPath, target) : null;

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var result = BuildJsonResult(repoPath, s, full, gap);
            return JsonSerializer.Serialize(result, DoctorJsonContext.Default.DoctorJsonResult);
        }

        // `--plan --json` — the machine-readable remediation manifest: the same phased
        // plan as `--plan`, but structured so an automated loop (maf-remediate) can
        // iterate it with per-finding confidence instead of parsing markdown.
        if (format.Equals("plan-json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(
                BuildPlanJson(repoPath, s), DoctorJsonContext.Default.DoctorPlanJson);

        if (format.Equals("plan", StringComparison.OrdinalIgnoreCase))
            return FormatPlan(repoPath, s, gap); // always covers every finding

        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            return FormatReport(repoPath, s, full, gap);

        // REP-27: an unrecognized format used to silently fall through to markdown,
        // hiding caller typos (e.g. "sarif"/"csv"). Fail loudly instead. Plain text is
        // fine — the caller's intended shape is unknowable, so we can't echo it as JSON.
        return $"Error: unknown format '{format}'. Supported: markdown | json | plan | plan-json.";
    }

    // -------------------------------------------------------------------------
    // Pure-function core (testable)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Aggregates per-scanner findings into a single grade. Pure: takes raw
    /// findings lists, no I/O. Supports either the 2-scanner shape (legacy
    /// callers + tests) or the 4-scanner shape (anti-pattern + fan-out + prompt + cost).
    /// </summary>
    public static DoctorSummary Grade(
        IReadOnlyList<AntiPatternFinding> antiPatterns,
        IReadOnlyList<MessageHandlerFinding> handlers,
        IReadOnlyList<PromptFinding>? promptFindings = null,
        IReadOnlyList<CostFinding>? costFindings = null)
    {
        promptFindings ??= [];
        costFindings ??= [];

        var errors = antiPatterns.Count(f => f.Severity == AntiPatternSeverity.Error);
        var warnings = antiPatterns.Count(f => f.Severity == AntiPatternSeverity.Warning);
        var infos = antiPatterns.Count(f => f.Severity == AntiPatternSeverity.Info);
        var starvationRisks = handlers.Count(h =>
            h.Verdict == FanOutVerdict.SilentStarvationRisk
            || h.Verdict == FanOutVerdict.LikelyInvalid);
        var promptErrors = promptFindings.Count(p => p.Severity == PromptSeverity.Error);
        var promptWarnings = promptFindings.Count(p => p.Severity == PromptSeverity.Warning);
        var unboundedCostSites = costFindings.Count(c => c.HasCapWarning);

        // Grade incorporates ALL signals — prompt-injection errors count as full errors.
        var totalErrors = errors + promptErrors;

        char grade;
        string reason;
        if (totalErrors >= 4 || starvationRisks >= 2)
        {
            grade = 'F';
            reason = $"{totalErrors} error(s) + {starvationRisks} silent-starvation risk(s)";
        }
        else if (totalErrors >= 1 || starvationRisks >= 1)
        {
            grade = 'C';
            reason = $"{totalErrors} error(s) + {starvationRisks} starvation risk(s) — fix before shipping";
        }
        else if (warnings + infos + promptWarnings + unboundedCostSites >= 5)
        {
            grade = 'B';
            reason = $"clean of errors; {warnings + promptWarnings} warnings + {unboundedCostSites} unbounded-cost site(s) + {infos} info note(s) to clean up";
        }
        else if (warnings + infos + promptWarnings + unboundedCostSites > 0)
        {
            grade = 'B';
            reason = $"clean of errors, {warnings + infos + promptWarnings + unboundedCostSites} advisor note(s)";
        }
        else
        {
            grade = 'A';
            reason = "no errors, no warnings, no silent-starvation risks, all agent call sites bounded";
        }

        // Top 3 fixes: prioritise silent-starvation risks, then errors (anti-pattern + prompt),
        // then warnings (anti-pattern + prompt + cost).
        var allFixes = handlers
            .Where(h => h.Verdict == FanOutVerdict.SilentStarvationRisk
                     || h.Verdict == FanOutVerdict.LikelyInvalid)
            .Select(h => new DoctorRecommendation(
                Priority: 1,
                Source: "MafValidateFanOut",
                Description: $"`{h.MethodName}` at {LlmFencing.MdInline(h.File)}:{h.Line} returns `{h.ReturnType}` — fan-out handler must return Task<T>", // SEC-02: neutralize untrusted path
                RuleId: "MAF001",
                File: h.File,
                Line: h.Line,
                Issue: $"`{h.MethodName}` returns `{h.ReturnType}` — fan-out handler must return Task<T>",
                FixDescription: Maf001Fix,
                AutoFixable: false,
                Why: "This handler produces no downstream message — it neither returns a value (Task<T> / ValueTask<T> / IAsyncEnumerable<T>) nor emits via context.SendMessageAsync/YieldOutputAsync. The fan-in barrier then starves: aggregation silently runs on partial data with no exception. (Handlers that DO emit via the context are not flagged.)"))
            .Concat(antiPatterns
                .Where(a => a.Severity == AntiPatternSeverity.Error)
                .Select(a => new DoctorRecommendation(
                    Priority: 2,
                    Source: "MafScanAntiPatterns",
                    Description: $"{a.RuleId} at {LlmFencing.MdInline(a.File)}:{a.Line} — {a.RuleName}", // SEC-02: neutralize untrusted path
                    RuleId: a.RuleId,
                    File: a.File,
                    Line: a.Line,
                    Issue: a.RuleName,
                    FixDescription: GetAntiPatternFix(a.RuleId),
                    AutoFixable: IsAutoFixable(a.RuleId),
                    Why: GetWhy(a.RuleId))))
            .Concat(promptFindings
                .Where(p => p.Severity == PromptSeverity.Error)
                .Select(p => new DoctorRecommendation(
                    Priority: 2,
                    Source: "MafLintAgentPrompt",
                    Description: $"{p.RuleId} at {LlmFencing.MdInline(p.File)}:{p.Line} — {p.Message}", // SEC-02: neutralize untrusted path
                    RuleId: p.RuleId,
                    File: p.File,
                    Line: p.Line,
                    Issue: p.Message,
                    FixDescription: GetPromptFix(p.RuleId),
                    AutoFixable: false,
                    Why: GetWhy(p.RuleId))))
            .Concat(costFindings
                .Where(c => c.HasCapWarning)
                .Select(c => new DoctorRecommendation(
                    Priority: 3,
                    Source: "MafEstimateCost",
                    Description: $"Unbounded `{c.CallSite}` at {LlmFencing.MdInline(c.File)}:{c.Line} — set MaxOutputTokens on the nearest ChatOptions", // SEC-02: neutralize untrusted path
                    RuleId: "COST-001",
                    File: c.File,
                    Line: c.Line,
                    Issue: $"Unbounded `{c.CallSite}`",
                    FixDescription: "Set `MaxOutputTokens` on the nearest `ChatOptions` to cap the per-call cost.",
                    AutoFixable: false,
                    Why: "An agent call with no `MaxOutputTokens` cap can emit an unbounded response — one runaway generation can dominate your bill and latency.")))
            .Concat(antiPatterns
                .Where(a => a.Severity == AntiPatternSeverity.Warning)
                .Select(a => new DoctorRecommendation(
                    Priority: 4,
                    Source: "MafScanAntiPatterns",
                    Description: $"{a.RuleId} at {LlmFencing.MdInline(a.File)}:{a.Line} — {a.RuleName}", // SEC-02: neutralize untrusted path
                    RuleId: a.RuleId,
                    File: a.File,
                    Line: a.Line,
                    Issue: a.RuleName,
                    FixDescription: GetAntiPatternFix(a.RuleId),
                    AutoFixable: IsAutoFixable(a.RuleId),
                    Why: GetWhy(a.RuleId))))
            .Concat(promptFindings
                // Prompt WARNINGS (PROMPT-001/002/003) are counted in the grade +
                // metrics; they must also be LISTED, or --all/--plan/--json would
                // contradict the metrics ("grade B, 2 warnings" but "0 findings").
                .Where(p => p.Severity == PromptSeverity.Warning)
                .Select(p => new DoctorRecommendation(
                    Priority: 4,
                    Source: "MafLintAgentPrompt",
                    Description: $"{p.RuleId} at {LlmFencing.MdInline(p.File)}:{p.Line} — {p.Message}", // SEC-02: neutralize untrusted path
                    RuleId: p.RuleId,
                    File: p.File,
                    Line: p.Line,
                    Issue: p.Message,
                    FixDescription: GetPromptFix(p.RuleId),
                    AutoFixable: false,
                    Why: GetWhy(p.RuleId))))
            .Concat(antiPatterns
                // Info-severity anti-patterns are counted in the grade too — list
                // them for the same reason (no rule emits Info today; kept consistent).
                .Where(a => a.Severity == AntiPatternSeverity.Info)
                .Select(a => new DoctorRecommendation(
                    Priority: 4,
                    Source: "MafScanAntiPatterns",
                    Description: $"{a.RuleId} at {LlmFencing.MdInline(a.File)}:{a.Line} — {a.RuleName}", // SEC-02: neutralize untrusted path
                    RuleId: a.RuleId,
                    File: a.File,
                    Line: a.Line,
                    Issue: a.RuleName,
                    FixDescription: GetAntiPatternFix(a.RuleId),
                    AutoFixable: IsAutoFixable(a.RuleId),
                    Why: GetWhy(a.RuleId))))
            // Stamp the detector trust level once, from the rule id (single source
            // of truth — the markdown tag, --plan, and --json all read this).
            .Select(r => r with { Confidence = ConfidenceFor(r.RuleId) })
            // Deterministic within a priority bucket (matches GroupByRule's order),
            // so the top-3 markdown headline + JSON top_fixes don't depend on
            // filesystem enumeration order.
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ToList();

        return new DoctorSummary(
            Grade: grade,
            Reason: reason,
            AntiPatternErrors: errors,
            AntiPatternWarnings: warnings,
            AntiPatternInfos: infos,
            SilentStarvationRisks: starvationRisks,
            HandlersChecked: handlers.Count,
            PromptErrors: promptErrors,
            PromptWarnings: promptWarnings,
            UnboundedCostSites: unboundedCostSites,
            AgentCallSitesChecked: costFindings.Count,
            TopFixes: allFixes.Take(3).ToList(),
            AllFixes: allFixes);
    }

    // -------------------------------------------------------------------------
    // JSON output support
    // -------------------------------------------------------------------------

    private static DoctorJsonResult BuildJsonResult(string repoPath, DoctorSummary s, bool full = false, CoverageGap? gap = null)
    {
        var markdownSummary = FormatReport(repoPath, s, full, gap);
        var lineCache = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        var findings = (full ? s.AllFixes : s.TopFixes)
            .Select(f => new DoctorJsonFinding(
                RuleId: f.RuleId,
                Severity: SeverityFor(f.Priority).Json,
                File: f.File,
                Line: f.Line,
                Issue: f.Issue,
                FixDescription: f.FixDescription,
                AutoFixable: f.AutoFixable,
                Why: f.Why,
                Confidence: f.Confidence,
                Fingerprint: Fingerprint(repoPath, f.RuleId, f.File, f.Line, lineCache)))
            .ToList();

        return new DoctorJsonResult(
            SchemaVersion: "1",
            Verdict: s.Grade.ToString(),
            ErrorsCount: s.AntiPatternErrors + s.PromptErrors,
            WarningsCount: s.AntiPatternWarnings + s.PromptWarnings + s.AntiPatternInfos,
            SilentStarvationRisks: s.SilentStarvationRisks,
            UnboundedCostSites: s.UnboundedCostSites,
            TopFixes: findings,
            SummaryMd: markdownSummary,
            ScanTruncated: s.ScanIncomplete,
            FilesScanned: s.FilesScanned,
            CoverageGap: gap is null
                ? null
                : new DoctorCoverageGap(
                    gap.RegistryMafVersion,
                    gap.ProjectMafVersion,
                    gap.NewerPackages.Select(p => $"{p.Id}@{p.Version}").ToList(),
                    CoverageGap.UpdateCommand));
    }

    /// <summary>
    /// True if MafAutoFix applies a deterministic, context-safe rewriter for this
    /// rule. The MAF002/MAF003 (analyzer aliases) and MAF130-FAN-IN-001 (autofix-only)
    /// arms are kept for 1:1 parity with AutoFixTool's factory map even though the
    /// doctor scan emits only canonical AntiPatternScannerTool.AllRules IDs.
    /// </summary>
    private static bool IsAutoFixable(string ruleId) => ruleId switch
    {
        "MAF-AP-SEC-001" => true,    // DefaultAzureCredential → ManagedIdentityCredential
        "MAF002" => true,            // analyzer-aligned alias
        "MAF-AP-SEC-003" => true,    // EnableSensitiveData = true → removed
        "MAF003" => true,            // analyzer-aligned alias
        "MAF-AP-WF-001" => true,     // sealed/partial modifiers on Executor
        "MAF130-FAN-IN-001" => true, // fan-in argument order swap
        // NOT MAF-AP-CONC-002: a safe `.Result`/`.Wait()` → `await` fix requires the
        // caller to be async (signature/caller changes), so it's a judgment call, not
        // a blanket auto-fix — the rewriter only rewrites already-async contexts and
        // declines the rest. The doctor lists CONC-002 as "needs your judgment".
        _ => false,
    };

    /// <summary>
    /// Detector trust level per rule — drives false-positive triage in `doctor`,
    /// `--plan`, `--json`, and the `maf-remediate` loop. Derived from the multi-agent
    /// detector audit (mechanism + calibrated FP risk):
    ///   • "certain"   — compiler ground-truth (CS0618 paths). No false positives by construction.
    ///   • "high"      — structural AST rules with low residual FP (post-hardening). Fix with light verification.
    ///   • "heuristic" — name-only / text / scope-limited rules. VERIFY each is real before changing code.
    /// Default is "heuristic" (verify-first) so a new/unclassified rule errs toward human review.
    /// </summary>
    internal static string ConfidenceFor(string ruleId) => ruleId switch
    {
        // Structural AST rules, low residual false-positive risk after the hardening batch.
        "MAF001"
            or "MAF-AP-SEC-001" or "MAF-AP-SEC-003"
            or "MAF-AP-CONC-001" or "MAF-AP-CONC-002"
            or "MAF-AP-WF-001" or "MAF-AP-AGENT-001"
            or "MAF-AP-DEVUI-001" or "MAF-AP-EXEC-001" => "high",
        // Compiler ground-truth, if ever surfaced through the doctor aggregate.
        _ when ruleId.StartsWith("CS06", StringComparison.Ordinal) => "certain",
        // COST-001, MAF-AP-SEC-002, MAF-AP-OBS-001, MAF-AP-MID-001, PROMPT-00x, …
        // — name-only / text / scope-limited. Verify before fixing.
        _ => "heuristic",
    };

    /// <summary>One-line actionable fix description per anti-pattern rule ID.</summary>
    /// <summary>
    /// REP-40: the canonical MAF001 (fan-out / silent-starvation) fix guidance — one
    /// source of truth shared by the doctor, the PR-audit comment, and the SARIF help
    /// pane so the three surfaces cannot drift into three different fix strings.
    /// </summary>
    internal const string Maf001Fix =
        "Return Task<T> / ValueTask<T> / IAsyncEnumerable<T> (the value is sent automatically), OR emit explicitly with `await context.SendMessageAsync(...)`. NOTE: an `AddFanOutEdge` source must use the return-value form — SendMessageAsync doesn't broadcast on that edge.";

    internal static string GetAntiPatternFix(string ruleId) => ruleId switch // internal: shared with SARIF help (REP-11)
    {
        "MAF-AP-SEC-001" => "Replace `DefaultAzureCredential` with `ManagedIdentityCredential` in production code.",
        "MAF-AP-SEC-002" => "Remove the hard-coded key; load it from configuration / Key Vault and rotate the leaked value.",
        "MAF-AP-SEC-003" => "Remove `EnableSensitiveData = true` from non-dev configurations.",
        "MAF-AP-WF-001" => "Add the missing `sealed` / `partial` modifier(s) so the Executor class is `sealed partial`.",
        "MAF130-FAN-IN-001" => "Swap `AddFanInBarrierEdge` argument order — sources first, target second.",
        "MAF-AP-CONC-002" => "Make the enclosing method `async` and `await` the call (or use a synchronous API) — `.Result` / `.Wait()` can deadlock.",
        "MAF-AP-OBS-001" => "Wire `UseOpenTelemetry` on the IChatClient pipeline.",
        "MAF-AP-CONC-001" => "Move session state out of `AIContextProvider` instance fields into `ProviderSessionState<T>`.",
        "MAF-AP-AGENT-001" => "Move `Instructions` inside `ChatClientAgentOptions.ChatOptions` (top-level placement is silently ignored).",
        "MAF-AP-EXEC-001" => "Migrate the legacy executor surface (`ReflectingExecutor` / `IMessageHandler` / `[StreamsMessage]` / `[YieldsMessage]`) per the MAF130 registry.",
        "MAF-AP-DEVUI-001" => "Guard the DevUI / Hosting reference with `#if DEVUI_ENABLED`.",
        "MAF-AP-MID-001" => "Provide BOTH `runFunc` and `runStreamingFunc` so the streaming path runs the middleware too.",
        _ => "See MafRegistryLookup for the canonical fix for this rule.",
    };

    /// <summary>One-line actionable fix description per prompt-lint rule ID.</summary>
    private static string GetPromptFix(string ruleId) => ruleId switch
    {
        "PROMPT-001" => "Fill in a meaningful Instructions string, or remove the empty assignment.",
        "PROMPT-002" => "Trim the Instructions literal — over ~2000 tokens of system prompt is bloat.",
        "PROMPT-003" => "Add a refusal clause to the Instructions (\"do not...\", \"refuse if...\").",
        "PROMPT-004" => "Stop concatenating untrusted input into Instructions — use ChatOptions or function tool args instead.",
        _ => "See MafRegistryLookup for the canonical fix.",
    };

    /// <summary>
    /// One-line consequence per rule ID — the "why it bites you" that turns a
    /// finding from a label into something a developer can act on with intent.
    /// Covers anti-pattern + prompt-lint rules; fan-out / cost carry their Why
    /// inline at construction. Empty string ⇒ no rationale shown.
    /// </summary>
    internal static string GetWhy(string ruleId) => ruleId switch
    {
        // Anti-pattern rules (AntiPatternScannerTool.AllRules)
        "MAF-AP-SEC-001" or "MAF002" => "`DefaultAzureCredential` probes many credential sources at runtime — slow, non-deterministic, and in production it can silently pick the wrong identity or fail closed.",
        "MAF-AP-SEC-002" => "A key committed to source is a leaked secret the moment it's pushed. Rotate it and load from configuration / Key Vault instead.",
        "MAF-AP-SEC-003" or "MAF003" => "Sensitive-data logging writes prompts and responses (often PII / secrets) to your telemetry sink — acceptable in dev, a data-leak in production.",
        "MAF-AP-CONC-001" => "`AIContextProvider` instances are shared across sessions; a mutable instance field leaks one user's state into another's. Use `ProviderSessionState<T>`.",
        "MAF-AP-CONC-002" => "Blocking on async with `.Result` / `.Wait()` can deadlock under a synchronization context and starves the thread pool under load.",
        "MAF-AP-OBS-001" => "With no `UseOpenTelemetry` on the chat pipeline you get no traces or metrics for agent calls — production failures become invisible.",
        "MAF-AP-AGENT-001" => "`Instructions` set at the top level of `ChatClientAgentOptions` is silently ignored — the agent runs with no system prompt and no error.",
        "MAF-AP-WF-001" => "Missing `partial` blocks the workflow source generator from emitting the dispatcher (a generator-time compile error); `sealed` is the canonical form the analyzer expects, though the build still succeeds without it.",
        "MAF-AP-DEVUI-001" => "DevUI / Hosting have no current-MAF equivalent — an unguarded reference breaks the production build. Guard it with `#if DEVUI_ENABLED`.",
        "MAF-AP-MID-001" => "Providing only `runFunc` means the streaming path (`RunStreamingAsync`) silently bypasses your middleware — auth / logging / guards don't run when streaming.",
        "MAF-AP-EXEC-001" => "These executor surfaces (`ReflectingExecutor` / `IMessageHandler` / `[StreamsMessage]` / `[YieldsMessage]`) were removed in 1.3.0 — code using them won't compile against current MAF.",
        "MAF130-FAN-IN-001" => "The legacy `AddFanInBarrierEdge(target, sources)` overload is obsolete; with the arguments swapped the barrier wires the wrong way and never fires.",

        // Prompt-lint rules (PromptLintTool)
        "PROMPT-001" => "An empty system prompt leaves the model with no role or guardrails — behavior is undefined and easily steered by user input.",
        "PROMPT-002" => "An oversized system prompt wastes tokens on every call and buries the directives that matter, degrading instruction-following.",
        "PROMPT-003" => "Without an explicit refusal clause the agent attempts out-of-scope or unsafe requests instead of declining.",
        "PROMPT-004" => "Concatenating untrusted input into `Instructions` is prompt injection by construction — the user's text becomes system-level instruction.",

        _ => "",
    };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Internal (not private) + the budget parameter is test-only: production
    /// code always calls this with <paramref name="budget"/> null (a fresh,
    /// default-capped <see cref="SourceFileWalker.ScanBudget"/>). Tests inject a
    /// tiny cap to exercise the truncation path without creating 50,000 files.
    ///
    /// Does NOT itself validate <paramref name="repoPath"/> — every caller
    /// (today, only <c>RunCore</c>) MUST run it through
    /// <see cref="PathGuard.ValidateRepoPath"/> first. A future caller that
    /// invokes this directly "for convenience" would skip that check entirely.
    /// </summary>
    internal static DoctorSummary AnalyzeRepo(
        string repoPath, IReadOnlyList<string>? excludes, SourceFileWalker.ScanBudget? budget = null)
    {
        var antiPatterns = new List<AntiPatternFinding>();
        var handlers = new List<MessageHandlerFinding>();
        var promptFindings = new List<PromptFinding>();
        var costFindings = new List<CostFinding>();

        budget ??= new SourceFileWalker.ScanBudget();

        // WM-19: keep the walk + budget accounting SEQUENTIAL (ScanBudget is not
        // thread-safe, and its FilesSeen / FilesSkippedOversized / Truncated state must
        // stay authoritative), then analyze the accepted files in PARALLEL — every
        // per-file scanner core is pure and reads only its own immutable syntax tree.
        // Output determinism is unaffected: Grade sorts every emitted fix by
        // (priority, file, line) and all counts are order-independent, so the
        // nondeterministic merge order never leaks into the DoctorSummary.
        var files = EnumerateScannableFiles(repoPath, excludes, budget).ToList();

        var gate = new object();
        Parallel.ForEach(
            files,
            () => (Anti: new List<AntiPatternFinding>(),
                   Handlers: new List<MessageHandlerFinding>(),
                   Prompts: new List<PromptFinding>(),
                   Costs: new List<CostFinding>()),
            (path, _, acc) =>
            {
                string source, rel;
                try
                {
                    // Guard ONLY the read + relative-path resolve: a file can be
                    // deleted/locked/raced between enumeration and read. Narrow catch
                    // so analyzer/parse bugs still propagate (the grade must never be
                    // silently degraded by swallowing a logic error). The oversized-file
                    // skip itself lives in SourceFileWalker.ScanBudget, shared by every
                    // caller of EnumerateCsFiles, not just this one.
                    source = File.ReadAllText(path);
                    rel = MakeRelative(repoPath, path);
                }
                catch (Exception ex) when (ex is IOException
                                            or UnauthorizedAccessException
                                            or System.Security.SecurityException
                                            or InvalidOperationException)
                {
                    return acc; // skip the unreadable/out-of-root file, don't crash the run
                }

                // WM-16: parse ONCE per file and share the tree across all four
                // per-file scanners (was 4× redundant CSharpSyntaxTree.ParseText).
                var root = SourceParsing.Parse(source).GetRoot();
                acc.Anti.AddRange(AntiPatternScannerTool.ScanFile(source, root, rel));
                acc.Handlers.AddRange(FanOutValidatorTool.AnalyzeSource(root, rel));
                acc.Prompts.AddRange(PromptLintTool.LintSource(root, rel));
                acc.Costs.AddRange(EstimateCostTool.AnalyzeSource(root, rel));
                return acc;
            },
            acc =>
            {
                lock (gate)
                {
                    antiPatterns.AddRange(acc.Anti);
                    handlers.AddRange(acc.Handlers);
                    promptFindings.AddRange(acc.Prompts);
                    costFindings.AddRange(acc.Costs);
                }
            });

        var summary = Grade(antiPatterns, handlers, promptFindings, costFindings);
        return summary with
        {
            FilesScanned = budget.FilesSeen,
            FilesSkippedOversized = budget.FilesSkippedOversized,
            ScanTruncated = budget.Truncated,
        };
    }

    private static IEnumerable<string> EnumerateScannableFiles(
        string repoRoot, IReadOnlyList<string>? excludes, SourceFileWalker.ScanBudget budget)
        => SourceFileWalker.EnumerateCsFiles(repoRoot, excludes, budget);

    private static string MakeRelative(string root, string file)
        => SourceFileWalker.MakeRelative(root, file);

    /// <summary>
    /// Shared by <see cref="FormatReport"/> and <see cref="FormatPlan"/> — #148
    /// finding 2. No-op when the scan was complete. <paramref name="tail"/> is
    /// the caller-specific closing sentence (report vs. plan read differently).
    /// </summary>
    private static void AppendScanIncompleteNote(StringBuilder sb, DoctorSummary s, string tail)
    {
        if (!s.ScanIncomplete) return;

        var clauses = new List<string>();
        if (s.ScanTruncated)
            clauses.Add($"stopped after {s.FilesScanned:N0} files (repo-size cap reached)");
        if (s.FilesSkippedOversized > 0)
            clauses.Add($"skipped {s.FilesSkippedOversized:N0} oversized file(s) (over the per-file size cap)");

        sb.AppendLine($"> ⚠️ **Scan incomplete** — {string.Join("; ", clauses)}. {tail}");
        sb.AppendLine();
    }

    /// <summary>
    /// REP-07 + SEC-02: a CLI command argument for the SCANNED repo — quoted (paths
    /// may contain spaces) and backtick/newline-neutralized (the command is rendered
    /// inside a markdown <c>`code span`</c>). repoPath is already validated absolute.
    /// Replaces the hard-coded <c>.</c> that targeted cwd rather than the scanned path.
    /// </summary>
    private static string CliRepoArg(string repoPath) => "\"" + LlmFencing.MdInline(repoPath) + "\"";

    internal static string FormatReport(string repoPath, DoctorSummary s, bool full = false, CoverageGap? gap = null)
    {
        var sb = new StringBuilder();
        var emoji = GradeEmoji(s.Grade);

        sb.AppendLine($"## {emoji} MAF health grade: **{s.Grade}**");
        sb.AppendLine();
        sb.AppendLine($"_{s.Reason}_");
        sb.AppendLine();
        sb.AppendLine($"**Repo:** `{LlmFencing.MdInline(repoPath)}`");
        sb.AppendLine();
        if (gap is not null)
        {
            // U-01: the grade only reflects what the registry knows about.
            sb.AppendLine(gap.ToMarkdown());
            sb.AppendLine();
        }
        AppendScanIncompleteNote(sb, s, "The grade and findings below reflect a partial scan, not the whole repo.");

        // REP-28: the read-only banner must match what the scan actually found — a
        // clean repo or an all-manual finding set must NOT suggest a no-op `autofix-all`.
        // REP-07: the suggested command targets the scanned repo, not cwd '.'.
        var totalFindings = s.AllFixes.Count;
        var autoFixable = s.AllFixes.Count(f => f.AutoFixable);
        if (totalFindings == 0)
            sb.AppendLine("> 🩺 This is a **read-only diagnosis** — `doctor` never edits your files. Nothing needs fixing.");
        else if (autoFixable == 0)
            sb.AppendLine($"> 🩺 This is a **read-only diagnosis** — `doctor` never edits your files. To fix: these findings need judgment — run `maf-doctor doctor {CliRepoArg(repoPath)} --plan` for an ordered plan, or hand them to the `@maf-migration` agent (nothing here is mechanically auto-fixable).");
        else
            sb.AppendLine($"> 🩺 This is a **read-only diagnosis** — `doctor` never edits your files. To fix: run `maf-doctor autofix-all {CliRepoArg(repoPath)} --apply` for the mechanical issues, then hand the rest to the `@maf-migration` agent.");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Anti-pattern errors | {s.AntiPatternErrors} |");
        sb.AppendLine($"| Anti-pattern warnings | {s.AntiPatternWarnings} |");
        sb.AppendLine($"| Anti-pattern info notes | {s.AntiPatternInfos} |");
        sb.AppendLine($"| Silent-starvation risks (fan-out handlers) | {s.SilentStarvationRisks} |");
        sb.AppendLine($"| `[MessageHandler]` methods inspected | {s.HandlersChecked} |");
        sb.AppendLine($"| Prompt-lint errors (injection / etc) | {s.PromptErrors} |");
        sb.AppendLine($"| Prompt-lint warnings (refusals / bloat) | {s.PromptWarnings} |");
        sb.AppendLine($"| Agent call sites without `MaxOutputTokens` cap | {s.UnboundedCostSites} |");
        sb.AppendLine($"| `RunAsync` / `RunStreamingAsync` sites inspected | {s.AgentCallSitesChecked} |");
        sb.AppendLine();

        var fixes = full ? s.AllFixes : s.TopFixes;
        var lineCache = new Dictionary<string, string[]?>(StringComparer.Ordinal);

        if (fixes.Count > 0)
        {
            if (full)
                AppendGroupedFindings(sb, repoPath, fixes, lineCache);
            else
                AppendTopFixes(sb, repoPath, s, lineCache);

            // The single most actionable line: how many of these the tool can fix
            // for you, deterministically, in one command. Computed over ALL findings
            // so it's the same headline regardless of --all.
            var autoCount = s.AllFixes.Count(f => f.AutoFixable);
            if (autoCount > 0)
            {
                sb.AppendLine($"💡 **{autoCount} of {s.AllFixes.Count} finding(s) are auto-fixable** — apply the deterministic Roslyn rewriters with `maf-doctor autofix-all {CliRepoArg(repoPath)} --apply` (CLI) or `MafAutoFixAll(repoPath, dryRun: false)` (MCP). The rest need your judgment (or hand them to the `@maf-migration` agent).");
                sb.AppendLine();
            }

            // Legend — what the per-finding tags mean, and how to act on each.
            // Spelled out because "needs your judgment" is easy to read as
            // "the tool gave up"; in fact it's the hand-off point to an LLM.
            var manualCount = s.AllFixes.Count(f => !f.AutoFixable);
            sb.AppendLine("**What the tags mean**");
            sb.AppendLine();
            sb.AppendLine($"- _auto-fixable_ — a deterministic **Roslyn rewriter** exists. Run `maf-doctor autofix-all {CliRepoArg(repoPath)} --apply` — no LLM involved. That is the **only** class of fix `autofix-all` can apply; it is purely mechanical/syntactic.");
            sb.AppendLine("- _needs your judgment_ — there is **no mechanical fix**: the right change depends on what your code is meant to do (e.g. which token cap, what message type, whether a `#if` guard belongs there). The ones tagged **⚠ heuristic** are name/text-based and may be **false positives** in your codebase — confirm each is real before changing it; the rest are high-confidence structural findings.");
            sb.AppendLine();
            if (manualCount > 0)
            {
                sb.AppendLine($"To work through the {manualCount} _needs-your-judgment_ finding(s), let an LLM drive maf-doctor: run `maf-doctor doctor {CliRepoArg(repoPath)} --plan` for an ordered, checkboxed remediation plan — or, in an MCP client (Copilot / Claude / Cursor), just ask **\"make me a plan to fix these issues\"** and the model will work through them using maf-doctor's tools and the `@maf-migration` agent.");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("✅ No fixes recommended — repo is clean against all configured rules.");
            sb.AppendLine();
        }

        sb.AppendLine("### Want more");
        sb.AppendLine();
        sb.AppendLine("- Every finding (full triage): `--all` (CLI) or `full: true` (MafDoctor MCP).");
        sb.AppendLine("- Machine-readable JSON (CI / dashboards): `--json` (CLI) or `format: \"json\"` (MafDoctor MCP).");
        sb.AppendLine("- Ordered, checkboxed remediation plan (drop into a GitHub issue): `--plan` (CLI) or `format: \"plan\"` (MafDoctor MCP).");
        sb.AppendLine("- Deep-dive ONE finding (offending code in context + why + fix) — **MCP clients only** (no CLI flag): `MafExplainFinding(repoPath, file, line)` or the `maf-explain-finding` prompt.");
        sb.AppendLine("- Deeper per-area analysis in your MCP client (Copilot / Claude / Cursor): `MafScanAntiPatterns`, `MafValidateFanOut`, `MafSimulateWorkflow`, `MafRunCs0618Hunt` — the anti-pattern + fan-out scanners also emit SARIF (`format: \"sarif\"`).");

        return sb.ToString();
    }

    /// <summary>
    /// The per-finding tag shown after a fix header: auto-fixable vs needs-judgment,
    /// and — for non-auto, heuristic findings — an explicit "verify it's real" nudge
    /// (these are the ones most likely to be false positives).
    /// </summary>
    private static string FixTag(DoctorRecommendation r) =>
        r.AutoFixable ? "auto-fixable"
        : r.Confidence == "heuristic" ? "needs your judgment · ⚠ heuristic — verify it's real first"
        : "needs your judgment";

    /// <summary>Default view — the top fixes, each enriched with Why / Fix / the offending source line.</summary>
    private static void AppendTopFixes(
        StringBuilder sb, string repoPath, DoctorSummary s, Dictionary<string, string[]?> lineCache)
    {
        sb.AppendLine("### Top fixes (ordered by impact)");
        sb.AppendLine();
        var i = 1;
        foreach (var fix in s.TopFixes)
        {
            // Readability: space each finding out and rule it off from the previous one
            // so the list is scannable (report-readability follow-up). Two markdown traps
            // dictate the exact shape:
            //   1. The header is bold *literal* text ("**N. [Source]** …"), not a real
            //      "N." ordered-list item, so the `---` rule between findings can't reset
            //      or terminate list numbering the way an <hr> does inside an <ol>.
            //   2. A `---` on the line directly under text is a setext-heading underline
            //      (it would promote that line to an <h2>). The blank line before every
            //      `---` keeps it an <hr>.
            if (i > 1)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
            // SEC-02: the untrusted file path inside Description is neutralized at its
            // construction in Grade() (so tool-authored markdown here — backticks around
            // method/type names — is preserved). The code-span echo below is neutralized too.
            sb.AppendLine($"**{i}. [{fix.Source}]** {fix.Description} · _{FixTag(fix)}_");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(fix.Why)) sb.AppendLine($"- **Why:** {fix.Why}");
            if (!string.IsNullOrEmpty(fix.FixDescription)) sb.AppendLine($"- **Fix:** {fix.FixDescription}");
            var (src, redacted) = TryReadSourceLine(repoPath, fix.File, fix.Line, lineCache);
            if (redacted)
                sb.AppendLine($"- `{LlmFencing.MdInline(fix.File)}:{fix.Line}` → _(source line redacted — may contain a secret)_");
            else if (src is not null)
                sb.AppendLine($"- `{LlmFencing.MdInline(fix.File)}:{fix.Line}` → `{src}`");
            i++;
        }
        sb.AppendLine();
        if (s.AllFixes.Count > s.TopFixes.Count)
        {
            sb.AppendLine($"_…and {s.AllFixes.Count - s.TopFixes.Count} more. Run with `--all` (CLI) or `full: true` (MafDoctor) for every finding._");
            sb.AppendLine();
        }
    }

    /// <summary>--all view — every finding, grouped by rule (so 8 hits of one rule
    /// read as one entry ×8), ordered by impact, each occurrence showing its line.</summary>
    private static void AppendGroupedFindings(
        StringBuilder sb, string repoPath, IReadOnlyList<DoctorRecommendation> fixes,
        Dictionary<string, string[]?> lineCache)
    {
        var groups = GroupByRule(fixes);

        sb.AppendLine($"### All findings ({fixes.Count} across {groups.Count} rule(s), grouped + ordered by impact)");
        sb.AppendLine();

        var firstGroup = true;
        foreach (var g in groups)
        {
            var rep = g.Rep;
            var tag = FixTag(rep);
            var count = g.Items.Count > 1 ? $" ×{g.Items.Count}" : "";
            // Rule each group off from the previous one, matching the default report's
            // rhythm. Each group already ends with a blank line, so the `---` here is
            // always preceded by one (no setext-heading promotion of the line above).
            if (!firstGroup)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }
            firstGroup = false;
            // Use a rule-generic title (not the representative's per-instance Issue),
            // so a group of distinct fan-out methods / cost sites isn't mislabeled
            // with one member's specifics under a ×N count.
            sb.AppendLine($"#### {PrioritySeverityLabel(rep.Priority)} `{rep.RuleId}` — {GroupHeaderTitle(rep)}{count} · _{tag}_");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(rep.Why)) sb.AppendLine($"- **Why:** {rep.Why}");
            if (!string.IsNullOrEmpty(rep.FixDescription)) sb.AppendLine($"- **Fix:** {rep.FixDescription}");
            sb.AppendLine();
            foreach (var it in g.Items)
            {
                var (src, redacted) = TryReadSourceLine(repoPath, it.File, it.Line, lineCache);
                var suffix = redacted ? " → _(redacted — may contain a secret)_"
                    : src is not null ? $" → `{src}`"
                    : "";
                sb.AppendLine($"  - `{LlmFencing.MdInline(it.File)}:{it.Line}`{suffix}");
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Single source of truth for severity presentation, keyed by the doctor's
    /// priority bucket, so the JSON string, markdown label, and emoji can never
    /// drift across the report / --all / --plan / JSON / MafExplainFinding surfaces.
    /// The JSON strings are part of the schema_version "1" contract — keep byte-identical.
    /// </summary>
    internal static (string Json, string Label, string Emoji) SeverityFor(int priority) => priority switch
    {
        1 => ("starvation_risk", "🔴 starvation", "🔴"),
        2 => ("error", "🔴 error", "🔴"),
        3 => ("cost", "🟠 cost", "🟠"),
        _ => ("warning", "🟡 warning", "🟡"),
    };

    /// <summary>Grade letter → status emoji (single source of truth).</summary>
    internal static string GradeEmoji(char grade) => grade switch
    {
        'A' => "🟢",
        'B' => "🟡",
        'C' => "🟠",
        _ => "🔴",
    };

    private static string PrioritySeverityLabel(int priority) => SeverityFor(priority).Label;

    /// <summary>
    /// A rule-generic title for a grouped header. Anti-pattern Issues are already
    /// the rule name (generic), but fan-out / cost Issues embed per-instance
    /// specifics (a method / call site) that would be misleading under a ×N count.
    /// </summary>
    private static string GroupHeaderTitle(DoctorRecommendation rep) => rep.RuleId switch
    {
        "MAF001" => "fan-out handler must return `Task<T>`",
        "COST-001" => "uncapped agent call — no `MaxOutputTokens`",
        "PROMPT-002" => "oversized Instructions (token bloat)",
        "PROMPT-004" => "untrusted input concatenated into `Instructions` (prompt injection)",
        _ => rep.Issue,
    };

    /// <summary>
    /// Groups recommendations by rule, ordered by impact (min priority), then by
    /// occurrence count, then rule id. Each group carries a representative (the
    /// highest-priority member) + every occurrence sorted by file:line. Shared by
    /// the grouped <c>--all</c> view and the <c>--plan</c> formatter.
    /// </summary>
    private static IReadOnlyList<(DoctorRecommendation Rep, List<DoctorRecommendation> Items)> GroupByRule(
        IEnumerable<DoctorRecommendation> fixes)
        => fixes
            .GroupBy(f => f.RuleId)
            .Select(g => (
                Rep: g.OrderBy(x => x.Priority).First(),
                Items: g.OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line).ToList(),
                MinPriority: g.Min(x => x.Priority)))
            .OrderBy(t => t.MinPriority)
            .ThenByDescending(t => t.Items.Count)
            .ThenBy(t => t.Rep.RuleId, StringComparer.Ordinal)
            .Select(t => (t.Rep, t.Items))
            .ToList();

    /// <summary>
    /// Tier 3 — the <c>--plan</c> / <c>format: "plan"</c> output: an ordered,
    /// checkboxed remediation plan you can drop straight into a GitHub issue.
    /// Phase 1 batches every auto-fixable finding into a single deterministic
    /// command; Phase 2 lists the semantic fixes as impact-ordered tasks. Always
    /// covers every finding (a partial plan would be misleading). No source
    /// snippets — file:line + why + fix only — so it never echoes a secret.
    /// </summary>
    internal static string FormatPlan(string repoPath, DoctorSummary s, CoverageGap? gap = null)
    {
        var sb = new StringBuilder();
        var emoji = GradeEmoji(s.Grade);
        sb.AppendLine($"# {emoji} MAF remediation plan — grade {s.Grade}");
        sb.AppendLine();
        sb.AppendLine($"_{s.Reason}_");
        sb.AppendLine();
        sb.AppendLine($"**Repo:** `{LlmFencing.MdInline(repoPath)}`");
        sb.AppendLine();
        if (gap is not null)
        {
            // U-01: a clean plan only means clean against what the registry knows.
            sb.AppendLine(gap.ToMarkdown());
            sb.AppendLine();
        }
        AppendScanIncompleteNote(sb, s, "This plan does NOT cover the whole repo.");

        var all = s.AllFixes;
        if (all.Count == 0)
        {
            sb.AppendLine("✅ Nothing to do — the repo is clean against all configured rules.");
            return sb.ToString();
        }

        var auto = all.Where(f => f.AutoFixable).ToList();
        var manual = all.Where(f => !f.AutoFixable).ToList();
        var heuristic = manual.Where(f => f.Confidence == "heuristic").ToList();
        sb.AppendLine($"{all.Count} finding(s): **{auto.Count} auto-fixable**, **{manual.Count} need your judgment**.");
        if (heuristic.Count > 0)
            sb.AppendLine($"⚠ **{heuristic.Count} of the {manual.Count}** are **heuristic** (marked below) — confirm each is a real issue in your code before changing it; some may be false positives.");
        sb.AppendLine();

        if (auto.Count > 0)
        {
            sb.AppendLine("## Phase 1 — Quick wins (deterministic, no LLM)");
            sb.AppendLine();
            sb.AppendLine($"One command clears {auto.Count} mechanical finding(s):");
            sb.AppendLine();
            sb.AppendLine($"- [ ] Run `maf-doctor autofix-all {CliRepoArg(repoPath)} --apply` (CLI) or `MafAutoFixAll(repoPath, dryRun: false)` (MCP), then rebuild.");
            sb.AppendLine();
            sb.AppendLine("It applies deterministic Roslyn rewriters for the rules below (and may also clear build-surfaced fixes like the fan-in arg-order swap that the scan-time grader doesn't see):");
            foreach (var (rep, items) in GroupByRule(auto))
                sb.AppendLine($"  - `{rep.RuleId}` — {GroupHeaderTitle(rep)}{(items.Count > 1 ? $" ×{items.Count}" : "")}");
            sb.AppendLine();
        }

        if (manual.Count > 0)
        {
            sb.AppendLine("## Phase 2 — Semantic fixes (need your judgment)");
            sb.AppendLine();
            sb.AppendLine("Ordered by impact — each is a separate, build-verified change (hand to `@maf-migration` for the agent loop):");
            sb.AppendLine();
            foreach (var (rep, items) in GroupByRule(manual))
            {
                var count = items.Count > 1 ? $" ×{items.Count}" : "";
                var heuristicTag = rep.Confidence == "heuristic" ? " · ⚠ _heuristic — verify first_" : "";
                sb.AppendLine($"- [ ] **{PrioritySeverityLabel(rep.Priority)} `{rep.RuleId}` — {GroupHeaderTitle(rep)}**{count}{heuristicTag}");
                if (!string.IsNullOrEmpty(rep.Why)) sb.AppendLine($"  - **Why:** {rep.Why}");
                if (!string.IsNullOrEmpty(rep.FixDescription)) sb.AppendLine($"  - **Fix:** {rep.FixDescription}");
                foreach (var it in items)
                    sb.AppendLine($"  - `{LlmFencing.MdInline(it.File)}:{it.Line}`");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## After");
        sb.AppendLine();
        sb.AppendLine($"Re-run `maf-doctor doctor {CliRepoArg(repoPath)}` to confirm the grade improved. In an MCP client (Copilot / Claude / Cursor), `MafExplainFinding(repoPath, file, line)` gives a grounded per-finding deep-dive + fix.");
        return sb.ToString();
    }

    /// <summary>
    /// `--plan --json` — the machine-readable remediation manifest. Same content as the
    /// markdown plan (Phase 1 = one mechanical command; Phase 2 = impact-ordered semantic
    /// findings grouped by rule), but structured + carrying each finding's `confidence`
    /// and a `verify_first` flag, so an automated loop (the maf-remediate prompt) can
    /// iterate it deterministically and triage false positives without parsing prose.
    /// </summary>
    private static DoctorPlanJson BuildPlanJson(string repoPath, DoctorSummary s)
    {
        var all = s.AllFixes;
        var auto = all.Where(f => f.AutoFixable).ToList();
        var manual = all.Where(f => !f.AutoFixable).ToList();
        var heuristicCount = manual.Count(f => f.Confidence == "heuristic");
        var lineCache = new Dictionary<string, string[]?>(StringComparer.Ordinal);

        var phase1 = auto.Count == 0 ? null : new PlanPhase1(
            // REP-07: target the scanned repo, not cwd '.'. This is JSON (not markdown),
            // so the serializer escapes the path — no MdInline neutralization needed here.
            Command: $"maf-doctor autofix-all \"{repoPath}\" --apply",
            FindingCount: auto.Count,
            ClearsRules: GroupByRule(auto).Select(g => g.Rep.RuleId).ToList());

        var phase2 = GroupByRule(manual).Select(g => new PlanFinding(
            RuleId: g.Rep.RuleId,
            Title: GroupHeaderTitle(g.Rep),
            Severity: SeverityFor(g.Rep.Priority).Json,
            Confidence: g.Rep.Confidence,
            VerifyFirst: g.Rep.Confidence == "heuristic",
            AutoFixable: false,
            Why: g.Rep.Why,
            Fix: g.Rep.FixDescription,
            Occurrences: g.Items.Select(it => new PlanOccurrence(
                it.File, it.Line, Fingerprint(repoPath, it.RuleId, it.File, it.Line, lineCache))).ToList()))
            .ToList();

        return new DoctorPlanJson(
            SchemaVersion: "1",
            Verdict: s.Grade.ToString(),
            Repo: repoPath,
            Counts: new PlanCounts(all.Count, auto.Count, manual.Count, heuristicCount),
            Phase1Autofix: phase1,
            Phase2Semantic: phase2,
            ScanTruncated: s.ScanIncomplete,
            FilesScanned: s.FilesScanned);
    }

    /// <summary>
    /// Reads the offending source line for a finding so the report shows the
    /// actual code, not just a location. Best-effort: returns null (no snippet)
    /// if the file is missing, the line is out of range, or anything throws.
    /// Files are cached per report. Defense-in-depth: routes through the audited
    /// <see cref="PathGuard.ValidateContainment"/> (the same helper the walker
    /// uses) rather than a hand-rolled prefix check — the path came from our own
    /// walker, but re-verifying closes the sibling-prefix + reparse-point lanes.
    /// </summary>
    /// <returns>
    /// <c>(Text, Redacted)</c>: <c>Text</c> is the trimmed source line (or null
    /// when unavailable); <c>Redacted</c> is true when the line contains a secret
    /// literal and was withheld. The secret text never leaves this method —
    /// redaction is <b>content-aware</b> (checked on the full untruncated line),
    /// so a secret co-located on a line surfaced by any rule is suppressed, not
    /// just findings from the hard-coded-key rule itself.
    /// </returns>
    private static (string? Text, bool Redacted) TryReadSourceLine(
        string repoPath, string relFile, int line, Dictionary<string, string[]?> cache)
    {
        if (line <= 0 || string.IsNullOrEmpty(relFile)) return (null, false);

        if (!cache.TryGetValue(relFile, out var lines))
        {
            lines = null;
            try
            {
                var full = PathGuard.ValidateContainment(repoPath, relFile, nameof(relFile));
                if (File.Exists(full))
                    lines = File.ReadAllLines(full);
            }
            catch { lines = null; } // containment violation / IO → no snippet
            cache[relFile] = lines;
        }

        if (lines is null || line > lines.Length) return (null, false);
        var raw = lines[line - 1].Trim();
        if (raw.Length == 0) return (null, false);
        // Redact BEFORE truncation so a secret past char 100 can't slip through.
        // Guard the regex: a RegexMatchTimeout (cold-start DFA build under load)
        // must degrade to redact-on-doubt, never abort the whole report.
        bool looksSecret;
        try { looksSecret = AntiPatternScannerTool.LooksLikeSecret(raw); }
        catch { return (null, true); }
        if (looksSecret) return (null, true);
        var text = raw.Replace('`', '\'');
        if (text.Length <= 100) return (text, false);
        // Don't slice through a surrogate pair (would emit U+FFFD mojibake).
        var cut = char.IsHighSurrogate(text[99]) ? 99 : 100;
        return (text[..cut] + "…", false);
    }

    /// <summary>
    /// REP-37: a short, drift-stable identifier for a finding, so an automated loop
    /// (the maf-remediate prompt) can track it across edits. Hashes
    /// <c>rule_id | file | whitespace-collapsed, secret-redacted source line</c> — the
    /// SAME text <see cref="TryReadSourceLine"/> produces, so a secret never enters the
    /// hash. Line movement leaves it stable; a content edit changes it (correct — the
    /// finding was touched). Falls back to hashing <c>rule_id | file | line</c> when the
    /// source line is unavailable or was secret-redacted — that fallback is present but
    /// NOT drift-stable (documented degradation, not a silent gap). First 12 hex of SHA-256.
    /// </summary>
    private static string Fingerprint(
        string repoPath, string ruleId, string file, int line, Dictionary<string, string[]?> lineCache)
    {
        var (src, _) = TryReadSourceLine(repoPath, file, line, lineCache);
        var basisTail = string.IsNullOrEmpty(src)
            ? line.ToString(System.Globalization.CultureInfo.InvariantCulture) // fallback: not drift-stable
            : string.Join(' ', src.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"{ruleId}|{file}|{basisTail}"));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant(); // 12 hex chars
    }
}

public sealed record DoctorSummary(
    char Grade,
    string Reason,
    int AntiPatternErrors,
    int AntiPatternWarnings,
    int AntiPatternInfos,
    int SilentStarvationRisks,
    int HandlersChecked,
    int PromptErrors,
    int PromptWarnings,
    int UnboundedCostSites,
    int AgentCallSitesChecked,
    IReadOnlyList<DoctorRecommendation> TopFixes,
    IReadOnlyList<DoctorRecommendation> AllFixes,
    // #148 finding 2 — set by AnalyzeRepo from the ScanBudget it walked with.
    // Trailing + defaulted so every existing Grade(...) call site (production
    // and tests) keeps compiling unchanged; AnalyzeRepo stamps the real values
    // via `with` after grading. ScanTruncated means the file-COUNT cap was hit
    // (the walk stopped early — there ARE unscanned files); FilesSkippedOversized
    // is a separate, additive signal (individual files skipped for exceeding the
    // per-file size cap — the walk still covered every other file).
    int FilesScanned = 0,
    bool ScanTruncated = false,
    int FilesSkippedOversized = 0)
{
    /// <summary>True if the scan did NOT cover every file in the repo, for any reason.</summary>
    public bool ScanIncomplete => ScanTruncated || FilesSkippedOversized > 0;
}

public sealed record DoctorRecommendation(
    int Priority,
    string Source,
    string Description,    // one-line header for markdown output
    string RuleId,         // for JSON
    string File,           // for JSON
    int Line,              // for JSON
    string Issue,          // short title for JSON
    string FixDescription, // actionable fix string
    bool AutoFixable,      // whether MafAutoFix can apply a deterministic rewriter
    string Why = "",       // Tier 1 — one-line consequence ("why it bites you"); "" = none known
    string Confidence = "high"); // detector trust: "certain" | "high" | "heuristic" (verify before fixing)

// -------------------------------------------------------------------------
// JSON output schema types (format: "json")
// -------------------------------------------------------------------------

/// <summary>
/// JSON output schema for MafDoctor (format: "json").
/// Schema version "1" — stable within 1.x. See docs/output-schemas.md.
/// </summary>
public sealed record DoctorJsonResult(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("errors_count")] int ErrorsCount,
    [property: JsonPropertyName("warnings_count")] int WarningsCount,
    [property: JsonPropertyName("silent_starvation_risks")] int SilentStarvationRisks,
    [property: JsonPropertyName("unbounded_cost_sites")] int UnboundedCostSites,
    [property: JsonPropertyName("top_fixes")] IReadOnlyList<DoctorJsonFinding> TopFixes,
    [property: JsonPropertyName("summary_md")] string SummaryMd,
    // #148 finding 2 — additive within schema_version "1" (consumers ignore
    // unknown fields, same convention as DoctorJsonFinding.Confidence). A
    // machine consumer that only reads typed fields (not summary_md) still
    // needs a structured way to know the scan didn't cover the whole repo.
    [property: JsonPropertyName("scan_truncated")] bool ScanTruncated = false,
    [property: JsonPropertyName("files_scanned")] int FilesScanned = 0,
    // U-01 — additive within schema_version "1": non-null when the repo references a
    // newer MAF than the registry covers (findings past that version are unknown).
    [property: JsonPropertyName("coverage_gap")] DoctorCoverageGap? CoverageGap = null);

/// <summary>Coverage-horizon gap in <see cref="DoctorJsonResult"/> (U-01).</summary>
public sealed record DoctorCoverageGap(
    [property: JsonPropertyName("registry_maf_version")] string RegistryMafVersion,
    [property: JsonPropertyName("project_maf_version")] string ProjectMafVersion,
    [property: JsonPropertyName("newer_packages")] IReadOnlyList<string> NewerPackages,
    [property: JsonPropertyName("update_command")] string UpdateCommand);

/// <summary>Error shape for format:"json" when the request fails validation (e.g. bad path).</summary>
public sealed record DoctorJsonError(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("error")] string Error);

public sealed record DoctorJsonFinding(
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("issue")] string Issue,
    [property: JsonPropertyName("fix_description")] string FixDescription,
    [property: JsonPropertyName("auto_fixable")] bool AutoFixable,
    [property: JsonPropertyName("why")] string Why = "",
    // Detector trust for false-positive triage: "certain" | "high" | "heuristic".
    // Additive within schema_version "1" (consumers ignore unknown fields).
    [property: JsonPropertyName("confidence")] string Confidence = "high",
    // REP-37: drift-stable finding id for automated remediation loops. Hash of
    // rule_id + file + redacted/collapsed source line; survives line moves.
    // Additive within schema_version "1".
    [property: JsonPropertyName("fingerprint")] string Fingerprint = "");

/// <summary>
/// `--plan --json` manifest schema. Schema version "1". A structured, phased
/// remediation plan with per-finding confidence — the machine-readable sibling of
/// the markdown `--plan`, built for the maf-remediate loop. See docs/output-schemas.md.
/// </summary>
public sealed record DoctorPlanJson(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("counts")] PlanCounts Counts,
    [property: JsonPropertyName("phase1_autofix")] PlanPhase1? Phase1Autofix,
    [property: JsonPropertyName("phase2_semantic")] IReadOnlyList<PlanFinding> Phase2Semantic,
    // #148 finding 2 — additive within schema_version "1". This manifest is
    // built specifically for automated consumption (the maf-remediate loop);
    // an automated consumer that only reads structured fields (not markdown)
    // must be able to tell a partial-repo plan apart from a complete one —
    // silently treating a truncated scan as "fully remediated" is worse for a
    // machine consumer than for a human reading the markdown --plan banner.
    [property: JsonPropertyName("scan_truncated")] bool ScanTruncated = false,
    [property: JsonPropertyName("files_scanned")] int FilesScanned = 0);

public sealed record PlanCounts(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("auto_fixable")] int AutoFixable,
    [property: JsonPropertyName("manual")] int Manual,
    [property: JsonPropertyName("heuristic")] int Heuristic);

/// <summary>Phase 1 — the single deterministic command that clears the mechanical findings.</summary>
public sealed record PlanPhase1(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("finding_count")] int FindingCount,
    [property: JsonPropertyName("clears_rules")] IReadOnlyList<string> ClearsRules);

/// <summary>One Phase 2 semantic finding (grouped by rule), with triage metadata.</summary>
public sealed record PlanFinding(
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("confidence")] string Confidence,
    // True when confidence == "heuristic": confirm the finding is real BEFORE editing.
    [property: JsonPropertyName("verify_first")] bool VerifyFirst,
    [property: JsonPropertyName("auto_fixable")] bool AutoFixable,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("fix")] string Fix,
    [property: JsonPropertyName("occurrences")] IReadOnlyList<PlanOccurrence> Occurrences);

public sealed record PlanOccurrence(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    // REP-37: drift-stable finding id (additive within schema_version "1").
    [property: JsonPropertyName("fingerprint")] string Fingerprint = "");

[JsonSerializable(typeof(DoctorJsonResult))]
[JsonSerializable(typeof(DoctorJsonFinding))]
[JsonSerializable(typeof(DoctorJsonError))]
[JsonSerializable(typeof(DoctorPlanJson))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class DoctorJsonContext : JsonSerializerContext { }
