using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: <c>MafScoreMigrationRisk</c>.
///
/// **Pre-migration risk verdict.** Sets expectations BEFORE a migration
/// starts so the maintainer doesn't sleep-walk into a 2-day rewrite
/// thinking they're 30 minutes from green.
///
/// **How the score is computed.** A weighted count of detected findings:
///
/// <code>
///   silent_starvation_risks × 3   ← runtime-only bugs, hardest to fix
///   cs0246_findings         × 2   ← removed types; structural rewrites
///   cs0117_findings         × 2   ← removed members; structural rewrites
///   cs0618_findings         × 1   ← obsolete-but-still-there
///   anti_pattern_errors     × 1   ← config / pattern fixes
/// </code>
///
/// Thresholds:
/// <list type="bullet">
///   <item><c>score &lt; 5</c>  → <b>EASY</b> (1-2 hr migration; low risk)</item>
///   <item><c>5 ≤ score &lt; 15</c> → <b>MEDIUM</b> (½ day; watch tests carefully)</item>
///   <item><c>score ≥ 15</c> → <b>HARD</b> (1+ day; expect rework, plan for rollback)</item>
/// </list>
///
/// The weights are deliberately conservative — calibration against more
/// real-world migrations is a Phase V+ refinement. Each verdict ships
/// with the per-category breakdown so the maintainer can sanity-check.
///
/// Phase W.11 (2026-05-13).
/// </summary>
[McpServerToolType]
public sealed class MigrationRiskScoreTool
{
    /// <summary>The score below which a codebase is rated EASY.</summary>
    internal const int EasyThreshold = 5;

    /// <summary>The score at which a codebase becomes HARD.</summary>
    internal const int HardThreshold = 15;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Score the migration-risk of a codebase BEFORE migration starts.
        Returns HARD / MEDIUM / EASY plus the per-category breakdown that
        produced the score.

        Note: the cs0246/cs0618 counts are SOURCE-TEXT heuristics (substring proxies
        for obsolete-API touches) — NOT compiler diagnostics — so treat them as a rough
        signal, not a guaranteed build outcome. The cs0117 count is the AST-precise
        MAF-AP-AGENT-001 finding (top-level Instructions), not a substring guess. See the
        `counts_method` field in the returned JSON.

        Input:
          - repoPath: absolute path to the codebase.

        Returns a JSON document:
          {
            "verdict": "MEDIUM",
            "score": 12,
            "thresholds": { "easyBelow": 5, "hardAtOrAbove": 15 },
            "breakdown": {
              "antiPatternErrors": 6,
              "antiPatternErrorsWeight": 1,
              "silentStarvationRisks": 2,
              "silentStarvationRisksWeight": 3,
              ...
            },
            "recommendation": "..."
          }
        """)]
    public string MafScoreMigrationRisk(
        [Description("Absolute path to the repo or project to score.")] string repoPath)
    {
        if (PathGuard.ValidateRepoPath(repoPath) is { } err)
            return JsonSerializer.Serialize(new { error = err });

        // WM-17: ONE walk, ONE read + parse per file. This method previously
        // enumerated the repo 4× (once here + 3× inside CountSourceMatches) and read +
        // scanned every file's text 4 times. Now the anti-pattern / fan-out scan AND the
        // CS-needle proxy counts share a single pass over each file. WM-16: parse once
        // and share the tree across both scanners. SEC-04: track truncation / skips so
        // the score is never silently computed on a partial repo.
        var antiPatternFindings = new List<AntiPatternFinding>();
        var handlers = new List<MessageHandlerFinding>();
        var cs0246Count = 0;
        var cs0618Count = 0;
        var cs0117Count = 0;
        var skippedUnreadable = 0;

        var budget = new SourceFileWalker.ScanBudget();
        foreach (var file in SourceFileWalker.EnumerateCsFiles(repoPath, excludes: null, budget))
        {
            string source, rel;
            try
            {
                source = File.ReadAllText(file);
                rel = SourceFileWalker.MakeRelative(repoPath, file);
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or InvalidOperationException)
            {
                skippedUnreadable++; // WM-18: one locked/raced file must not abort the whole score
                continue;
            }

            var root = SourceParsing.Parse(source).GetRoot();
            antiPatternFindings.AddRange(AntiPatternScannerTool.ScanFile(source, root, rel));
            handlers.AddRange(FanOutValidatorTool.AnalyzeSource(root, rel));

            // CS warning counts as a source-text proxy — not a build-perfect signal,
            // but a reasonable "how many obsolete-API touches" measure without paying
            // for a full dotnet build. Counted against the already-in-memory source.
            cs0246Count += CountMatches(source, "AgentThread", "MapA2A", "InProcessExecution", "AgentRunUpdateEvent");
            cs0618Count += CountMatches(source, "AddFanInBarrierEdge(", "SerializeSession(");
        }

        // REP-18: the old `.Instructions` needle scored the CANONICAL CORRECT idiom
        // (`ChatClientAgentOptions.Instructions` inside ChatOptions) as risk. Replace it
        // with the AST-precise MAF-AP-AGENT-001 finding (already computed above), which
        // matches ONLY the genuinely-broken top-level Instructions placement.
        cs0117Count = antiPatternFindings.Count(f => f.RuleId == "MAF-AP-AGENT-001");

        var antiPatternErrors = antiPatternFindings.Count(f => f.Severity == AntiPatternSeverity.Error);
        var silentStarvation = handlers.Count(h =>
            h.Verdict == FanOutVerdict.SilentStarvationRisk
            || h.Verdict == FanOutVerdict.LikelyInvalid);

        var score = (silentStarvation * 3)
                  + (cs0246Count * 2)
                  + (cs0117Count * 2)
                  + (cs0618Count * 1)
                  + (antiPatternErrors * 1);

        var verdict = score < EasyThreshold ? "EASY"
                    : score < HardThreshold ? "MEDIUM"
                                            : "HARD";

        var recommendation = verdict switch
        {
            "EASY"   => "1-2 hour migration. Apply MafAutoFix for each finding; one round of dotnet build should be green.",
            "MEDIUM" => "Half-day migration. Migrate in order: silent-starvation first (highest behavioural risk), then CS0246 (structural removals), then anti-patterns. Watch the test suite carefully after each batch.",
            "HARD"   => "1+ day migration. Plan for rework. Prepare a rollback branch BEFORE starting (@maf-rollback). Migrate file-by-file with `dotnet build` gates between each. Consider running the workshop on a parallel branch to validate the approach.",
            _ => "(unknown verdict)",
        };

        return JsonSerializer.Serialize(new
        {
            schema_version = "1",
            verdict,
            score,
            // REP-18: state the evidence class. cs0246/cs0618 are SOURCE-TEXT heuristics
            // (substring proxies for obsolete-API touches, NOT compiler diagnostics); a
            // hit does not guarantee a real CS0246/CS0618. cs0117 is now the AST-precise
            // MAF-AP-AGENT-001 count (top-level Instructions), not a substring guess.
            counts_method = "cs0246Count/cs0618Count = source-text-heuristic proxies (not compiler diagnostics); cs0117Count = MAF-AP-AGENT-001 AST finding",
            thresholds = new
            {
                easyBelow = EasyThreshold,
                hardAtOrAbove = HardThreshold,
            },
            breakdown = new
            {
                antiPatternErrors,
                antiPatternErrorsWeight = 1,
                silentStarvationRisks = silentStarvation,
                silentStarvationRisksWeight = 3,
                cs0246Count,
                cs0246Weight = 2,
                cs0117Count,
                cs0117Weight = 2,
                cs0618Count,
                cs0618Weight = 1,
            },
            // SEC-04: make an incomplete scan machine-visible so a consumer never treats
            // a partial score as authoritative. Mirrors DoctorTool's truncation surfacing.
            scan_truncated = budget.Truncated,
            files_skipped_oversized = budget.FilesSkippedOversized,
            files_skipped_unreadable = skippedUnreadable,
            recommendation,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Count source-text occurrences of any of the given needles across
    /// all .cs files in <paramref name="repoPath"/>. A needle appearing
    /// multiple times in a single file counts once per occurrence (which
    /// matches how the compiler would emit warnings).
    /// </summary>
    internal static int CountSourceMatches(string repoPath, params string[] needles)
    {
        // Thin wrapper over the pure per-source CountMatches so there is ONE counting
        // implementation. The production scorer no longer calls this (it counts against
        // the already-in-memory source inside its single walk — WM-17); it remains for
        // direct unit-testing of the counting semantics against real files.
        var count = 0;
        foreach (var file in SourceFileWalker.EnumerateCsFiles(repoPath))
            count += CountMatches(File.ReadAllText(file), needles);
        return count;
    }

    /// <summary>
    /// Pure: total occurrences of any of <paramref name="needles"/> in a single
    /// in-memory source string. A needle appearing N times counts N (matching how the
    /// compiler emits one warning per touch).
    /// </summary>
    internal static int CountMatches(string src, params string[] needles)
    {
        var count = 0;
        foreach (var needle in needles)
            count += CountOccurrences(src, needle);
        return count;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
