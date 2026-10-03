namespace MafDoctor.Tools;

/// <summary>
/// SARIF v2.1.0 emitter wrappers around the existing finding lists.
///
/// 2026-05-12 (Phase F.2, post-MCP-review): the two former MCP tools
/// `MafScanAntiPatternsSarif` and `MafValidateFanOutSarif` were removed from
/// the public tool surface. SARIF output is now reached by passing
/// <c>format: "sarif"</c> to <c>MafScanAntiPatterns</c> / <c>MafValidateFanOut</c>.
/// This class survives as the internal emitter shared by both tools.
/// </summary>
internal static class SarifExportTool
{
    // REP-20: SARIF provenance now tracks the real installed version (was frozen at the
    // retired "1.3.0-alpha-3", a per-release manual-update trap). Single source: ToolVersionInfo.
    private static string ToolVersion => ToolVersionInfo.Current;

    /// <summary>
    /// Builds a SARIF v2.1.0 document from a list of anti-pattern findings.
    /// </summary>
    public static string EmitAntiPatternsSarif(IReadOnlyList<AntiPatternFinding> findings)
    {
        var sarifFindings = findings.Select(f => new SarifFinding(
            RuleId: f.RuleId,
            Severity: MapSeverity(f.Severity),
            // Redact a secret-bearing Match (e.g. the SEC-002 key literal) so the
            // SARIF surface matches the markdown surface's no-secret-leak posture.
            Message: f.RuleName + " — " + (AntiPatternScannerTool.LooksLikeSecret(f.Match) ? "(redacted)" : f.Match),
            File: f.File,
            Line: f.Line,
            // REP-12: fingerprint basis. Uses the raw match (one-way hash, never emitted)
            // so the identity is drift-stable across line moves.
            LineText: f.Match));

        return SarifEmitter.Emit(ToolVersion, sarifFindings, BuildAntiPatternRuleCatalog());
    }

    /// <summary>
    /// REP-22: a valid empty SARIF v2.1.0 envelope that carries <paramref name="error"/>
    /// through SARIF's own invocation channel (<c>executionSuccessful:false</c> +
    /// a <c>toolExecutionNotification</c>). Lets <c>MafScanAntiPatterns(format:"sarif")</c>
    /// return parseable SARIF on invalid input instead of a plain-text error string
    /// that would break a machine consumer's parser.
    /// </summary>
    public static string EmitAntiPatternsError(string error)
        => SarifEmitter.Emit(ToolVersion, [], BuildAntiPatternRuleCatalog(), invocationError: error);

    /// <summary>
    /// Builds a SARIF v2.1.0 document from fan-out handler findings. Only the
    /// risk verdicts are surfaced (<c>SilentStarvationRisk</c>, <c>LikelyInvalid</c>);
    /// <c>Ok</c> handlers are dropped from SARIF reports.
    /// </summary>
    public static string EmitFanOutSarif(IReadOnlyList<MessageHandlerFinding> findings)
    {
        var sarifFindings = findings
            .Where(f => f.Verdict != FanOutVerdict.Ok)
            .Select(f => new SarifFinding(
                RuleId: "MAF001",
                Severity: SarifSeverity.Error,
                Message: $"`{f.MethodName}` returns `{f.ReturnType}` — fan-out handler must return ValueTask<T>",
                File: f.File,
                Line: f.Line));

        return SarifEmitter.Emit(ToolVersion, sarifFindings, BuildFanOutRuleCatalog());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SarifSeverity MapSeverity(AntiPatternSeverity s) => s switch
    {
        AntiPatternSeverity.Error => SarifSeverity.Error,
        AntiPatternSeverity.Warning => SarifSeverity.Warning,
        AntiPatternSeverity.Info => SarifSeverity.Note,
        _ => SarifSeverity.Note,
    };

    private static IEnumerable<SarifRule> BuildAntiPatternRuleCatalog()
    {
        const string skillUri = "https://github.com/joslat/maf-doctor/blob/main/.github/skills/maf-anti-pattern-scanner/SKILL.md";
        foreach (var rule in AntiPatternScannerTool.AllRules)
        {
            // REP-11: surface the same Why/Fix guidance the CLI prints. fullDescription
            // carries the Why (was a duplicate of the name); help.markdown carries both.
            var why = DoctorTool.GetWhy(rule.Id);
            var whyText = string.IsNullOrEmpty(why) ? rule.Name : why;
            yield return new SarifRule(
                Id: rule.Id,
                Name: rule.Name,
                Severity: MapSeverity(rule.Severity),
                FullDescription: whyText,
                HelpUri: skillUri,
                HelpMarkdown: $"**Why:** {whyText}\n\n**Fix:** {DoctorTool.GetAntiPatternFix(rule.Id)}");
        }
    }

    private static IEnumerable<SarifRule> BuildFanOutRuleCatalog()
    {
        const string full = "A [MessageHandler] method that returns void, Task, or ValueTask (non-generic) produces no downstream message and silently starves the fan-in barrier.";
        yield return new SarifRule(
            Id: "MAF001",
            Name: "Fan-out handler must return ValueTask<T>",
            Severity: SarifSeverity.Error,
            FullDescription: full,
            HelpUri: "https://github.com/joslat/maf-doctor/blob/main/.github/skills/maf-fan-out-validator/SKILL.md",
            // REP-11: MAF001 already had a good fullDescription but no help pane content.
            // REP-40: reuse the doctor's canonical fix so doctor / SARIF / PR-audit agree.
            HelpMarkdown: $"**Why:** {full}\n\n**Fix:** {DoctorTool.Maf001Fix}");
    }
}
