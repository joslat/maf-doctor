using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MafDoctor.Tools.Rewriters;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: <c>MafAutoFix</c>.
///
/// **The missing "do" half of the toolkit.** Today the detection tools
/// (<see cref="AntiPatternScannerTool"/>, <see cref="FanOutValidatorTool"/>,
/// <see cref="Cs0618HuntTool"/>) report findings; the migration agent (in
/// Copilot Chat) applies the fixes. <c>MafAutoFix</c> closes that loop for
/// the mechanical rules — no LLM call, no judgement, just deterministic
/// Roslyn syntax rewriting.
///
/// **Supported rules** (by rule ID, with aliases):
/// <list type="bullet">
///   <item><c>MAF-AP-SEC-001</c> / <c>MAF002</c> — DefaultAzureCredential → ManagedIdentityCredential</item>
///   <item><c>MAF-AP-SEC-003</c> / <c>MAF003</c> — drop <c>EnableSensitiveData = true</c></item>
///   <item><c>MAF-AP-WF-001</c> — add missing <c>sealed</c> to Executor classes</item>
///   <item><c>MAF130-FAN-IN-001</c> — swap <c>AddFanInBarrierEdge</c> arg order</item>
///   <item><c>MAF-AP-CONC-002</c> — <c>.Result</c> → <c>(await ...)</c>; <c>.Wait()</c> → <c>await ...</c></item>
/// </list>
///
/// See <c>src/maf-autopilot/Tools/Rewriters/IRuleRewriter.cs</c> for the
/// rewriter contract; each rule is one <see cref="CSharpSyntaxRewriter"/>
/// in <c>Rewriters/</c>.
/// </summary>
[McpServerToolType]
public sealed class AutoFixTool
{
    // ruleId -> factory. Two IDs may share a rewriter (e.g. MAF-AP-SEC-001 ≡ MAF002).
    private static readonly IReadOnlyDictionary<string, Func<IRuleRewriter>> _factories =
        new Dictionary<string, Func<IRuleRewriter>>(StringComparer.OrdinalIgnoreCase)
        {
            ["MAF-AP-SEC-001"]      = () => new DefaultAzureCredentialRewriter(),
            ["MAF002"]              = () => new DefaultAzureCredentialRewriter(),
            ["MAF-AP-SEC-003"]      = () => new EnableSensitiveDataRewriter(),
            ["MAF003"]              = () => new EnableSensitiveDataRewriter(),
            ["MAF-AP-WF-001"]       = () => new ExecutorSealedRewriter(),
            ["MAF130-FAN-IN-001"]   = () => new FanInArgOrderRewriter(),
            ["MAF-AP-CONC-002"]     = () => new SyncOverAsyncRewriter(),
        };

    /// <summary>The set of rule IDs this tool can auto-fix. Read-only.</summary>
    public static IReadOnlyCollection<string> SupportedRuleIds => _factories.Keys.ToList();

    /// <summary>
    /// **Dependency-safe execution order** for <see cref="MafAutoFixAll"/> /
    /// <see cref="RunAll"/>. Each rewriter runs over the output of the previous.
    /// Rationale per rule:
    ///   1. ExecutorSealedRewriter      — purely additive modifier; never affects other rewrites.
    ///   2. EnableSensitiveDataRewriter — removes an initializer-list entry; doesn't affect rules 3-5.
    ///   3. DefaultAzureCredentialRewriter — type-name swap; independent of other rules.
    ///   4. FanInArgOrderRewriter       — argument-position swap; runs AFTER 1-3 in case any altered context.
    ///   5. SyncOverAsyncRewriter       — wraps an invocation in await; runs LAST (order-safety > order-speed).
    /// Aliases (MAF002 / MAF003) are deliberately omitted — each rewriter runs EXACTLY ONCE per pass.
    /// </summary>
    internal static readonly IReadOnlyList<string> OrderedRuleIds = new[]
    {
        "MAF-AP-WF-001",
        "MAF-AP-SEC-003",
        "MAF-AP-SEC-001",
        "MAF130-FAN-IN-001",
        "MAF-AP-CONC-002",
    };

    /// <summary>
    /// One-line, human-readable description per auto-fixable rule. Single source
    /// of truth for the CLI's human-readable output (shared with the JSON path
    /// so the two never drift).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RuleDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MAF-AP-WF-001"]     = "add missing `partial` to Executor classes",
            ["MAF-AP-SEC-003"]    = "drop `EnableSensitiveData = true`",
            ["MAF-AP-SEC-001"]    = "DefaultAzureCredential → ManagedIdentityCredential",
            ["MAF130-FAN-IN-001"] = "fix fan-in barrier-edge argument order",
            ["MAF-AP-CONC-002"]   = ".Result / .Wait() → await",
        };

    /// <summary>Structured result of an <see cref="RunAll"/> pass. Rendered to
    /// JSON by <see cref="MafAutoFixAll"/> (MCP) and to human-readable text by
    /// the CLI — one computation, two presentations.
    /// <para><see cref="Errors"/> is additive (defaults to empty): per-file
    /// read/parse/write failures that previously vanished. A non-empty list
    /// means the pass did NOT fully succeed.</para></summary>
    internal sealed record AutoFixAllReport(
        bool DryRun,
        IReadOnlyList<string> OrderOfExecution,
        int TotalDistinctFilesChanged,
        IReadOnlyList<string> AffectedFiles,
        IReadOnlyList<AutoFixRuleReport> PerRule,
        IReadOnlyList<AutoFixError>? Errors = null,
        IReadOnlyList<AutoFixFileDiff>? Diffs = null);

    /// <summary>Per-rule slice of an <see cref="AutoFixAllReport"/>.</summary>
    internal sealed record AutoFixRuleReport(
        string RuleId,
        int FilesChanged,
        IReadOnlyList<string> ChangedFiles);

    /// <summary>A single per-file failure. <see cref="File"/> is repo-relative
    /// and <see cref="Message"/> is scrubbed of absolute host paths.</summary>
    internal sealed record AutoFixError(string File, string Message);

    /// <summary>A per-file unified diff for the preview (dry-run) surface — so a
    /// human (or an MCP agent) can review the actual change before applying it,
    /// not just a bare file name. <see cref="Diff"/> is secret-redacted and
    /// line-capped. WM-24.</summary>
    internal sealed record AutoFixFileDiff(string File, IReadOnlyList<string> RuleIds, string Diff);

    /// <summary>
    /// Internal helper for sibling tools (e.g. <see cref="BeforeAfterTool"/>)
    /// to share the rewriter registry without re-declaring it.
    /// </summary>
    internal static bool TryCreateRewriter(string ruleId, out IRuleRewriter? rewriter)
    {
        if (_factories.TryGetValue(ruleId, out var factory))
        {
            rewriter = factory();
            return true;
        }
        rewriter = null;
        return false;
    }

    [McpServerTool(Destructive = true, OpenWorld = false)]
    [Description("""
        Deterministically apply the fix for a single registry rule across a codebase.

        Supported ruleIds: MAF-AP-SEC-001 / MAF002 (DefaultAzureCredential),
        MAF-AP-SEC-003 / MAF003 (EnableSensitiveData=true), MAF-AP-WF-001 (sealed
        modifier on Executor), MAF130-FAN-IN-001 (fan-in arg order),
        MAF-AP-CONC-002 (.Result / .Wait()).

        Input:
          - repoPath: absolute path to the repo or project to fix.
          - ruleId: which rule's auto-fixer to run.
          - specificFile: optional. Limit fixes to a single file (path relative to repoPath).
            Must exist and be a .cs file, else an error JSON is returned.
          - dryRun: if true, no files are written — just returns the JSON summary
            of what WOULD change. Default true (preview-only) — pass dryRun: false
            to actually write.

        Returns a JSON document with `ruleId`, `dryRun`, `filesChanged` count,
        `changedFiles` list, and any per-file `errors` (each `{file, error}`).

        Bad ruleId, empty repoPath, or a missing/non-.cs specificFile returns an error JSON.
        """)]
    public string MafAutoFix(
        [Description("Absolute path to the repo or project to fix.")] string repoPath,
        [Description("Rule ID, e.g. 'MAF-AP-SEC-001' or 'MAF130-FAN-IN-001'.")] string ruleId,
        [Description("Optional: relative path to limit fixes to a single existing .cs file.")] string? specificFile = null,
        [Description("If true (the default), no files written — preview only. Pass false to write.")] bool dryRun = true)
    {
        if (PathGuard.ValidateRepoPath(repoPath) is { } err)
            return JsonSerializer.Serialize(new { schema_version = "1", error = err });

        // C1 mitigation: `specificFile` was previously joined to repoPath with a
        // `Path.IsPathRooted` short-circuit that accepted absolute paths and
        // unblocked `..` segments — letting an LLM-supplied value redirect the
        // rewriter to arbitrary host files. ValidateContainment forces the
        // resolved path inside the repo root and rejects symlink escapes.
        if (!string.IsNullOrEmpty(specificFile))
        {
            try { PathGuard.ValidateContainment(repoPath, specificFile, nameof(specificFile)); }
            catch (ArgumentException ex)
            {
                return JsonSerializer.Serialize(new { schema_version = "1", error = $"Error: {ex.Message}" });
            }
            // WM-41: a missing or non-.cs specificFile previously yielded a silent
            // "0 changes" success. Surface it as an error instead.
            if (ValidateSpecificFile(repoPath, specificFile) is { } fileErr)
                return JsonSerializer.Serialize(new { schema_version = "1", error = fileErr });
        }

        if (!_factories.TryGetValue(ruleId, out var factory))
            return JsonSerializer.Serialize(new
            {
                schema_version = "1",
                error = $"Unknown ruleId '{ruleId}'. Supported: {string.Join(", ", SupportedRuleIds)}",
            });

        // WM-44: serialise concurrent applies on the same repo (apply mode only).
        IDisposable? applyLock = null;
        if (!dryRun)
        {
            applyLock = TryAcquireApplyLock(repoPath, out var lockErr);
            if (applyLock is null) return JsonSerializer.Serialize(new { schema_version = "1", error = lockErr });
        }
        using var _lock = applyLock;

        var outcomes = RunPipeline(new[] { (ruleId, factory()) }, repoPath, specificFile, dryRun);
        var changedFiles = outcomes.Where(o => o.ChangedByRules.Count > 0).Select(o => o.Relative).ToList();
        var errors = outcomes.Where(o => o.Error is not null).Select(o => o.Error!).ToList();

        return JsonSerializer.Serialize(new
        {
            schema_version = "1", // REP-24: version every machine-readable surface
            ruleId,
            dryRun,
            filesChanged = changedFiles.Count,
            changedFiles,
            errors = errors.Select(e => new { file = e.File, error = e.Message }).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Destructive = true, OpenWorld = false)]
    [Description("""
        Apply EVERY supported rewriter across the codebase in dependency-safe
        order — the "fix everything fixable" command. Use this when running
        a clean migration pass instead of cherry-picking individual rules.

        Order of execution (each runs over the output of the previous):
          1. MAF-AP-WF-001       — add `sealed` modifier (purely additive)
          2. MAF-AP-SEC-003      — drop EnableSensitiveData = true
          3. MAF-AP-SEC-001      — DefaultAzureCredential → ManagedIdentityCredential
          4. MAF130-FAN-IN-001   — swap fan-in arg order
          5. MAF-AP-CONC-002     — .Result / .Wait() → await

        Input:
          - repoPath: absolute path to the repo or project to fix.
          - specificFile: optional, relative path to limit fixes to a single existing .cs file.
          - dryRun: if true, no files are written — just returns the JSON
            summary of what WOULD change. Default true (preview-only) — pass
            dryRun: false to actually write.

        Returns aggregate JSON with the ordered rule list + total distinct files
        changed (union across all rules) + per-rule breakdown + any per-file
        `errors` (each `{file, error}`).
        """)]
    public string MafAutoFixAll(
        [Description("Absolute path to the repo or project to fix.")] string repoPath,
        [Description("Optional: relative path to limit fixes to a single existing .cs file.")] string? specificFile = null,
        [Description("If true (the default), no files written — preview only. Pass false to write.")] bool dryRun = true)
    {
        var report = RunAll(repoPath, specificFile, dryRun, out var error);
        return report is null
            ? JsonSerializer.Serialize(new { schema_version = "1", error })
            : SerializeReport(report);
    }

    /// <summary>
    /// Projects an <see cref="AutoFixAllReport"/> onto the EXACT anonymous JSON
    /// shape the MCP contract (and the tests) depend on. Shared by
    /// <see cref="MafAutoFixAll"/> and the CLI so the machine output — and the
    /// exit-code decision derived from <see cref="AutoFixAllReport.Errors"/> —
    /// come from one computed report, never a re-run.
    /// </summary>
    internal static string SerializeReport(AutoFixAllReport report)
    {
        // The dictionary preserves insertion order, so `perRule`'s key order
        // matches OrderOfExecution.
        var perRule = new Dictionary<string, object>();
        foreach (var r in report.PerRule)
            perRule[r.RuleId] = new { filesChanged = r.FilesChanged, changedFiles = r.ChangedFiles };

        return JsonSerializer.Serialize(new
        {
            schema_version = "1", // REP-24: version every machine-readable surface
            dryRun = report.DryRun,
            orderOfExecution = report.OrderOfExecution,
            totalDistinctFilesChanged = report.TotalDistinctFilesChanged,
            affectedFiles = report.AffectedFiles,
            perRule,
            // Additive: previously-swallowed per-file failures.
            errors = (report.Errors ?? Array.Empty<AutoFixError>())
                .Select(e => new { file = e.File, error = e.Message }).ToList(),
            // Additive (WM-24): per-file preview diffs for agent review.
            diffs = (report.Diffs ?? Array.Empty<AutoFixFileDiff>())
                .Select(d => new { file = d.File, ruleIds = d.RuleIds, diff = d.Diff }).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Computation core shared by the MCP JSON path (<see cref="MafAutoFixAll"/>)
    /// and the CLI's human-readable path. Runs every supported rewriter in
    /// <see cref="OrderedRuleIds"/> order — as a SINGLE per-file pass (each file
    /// is enumerated, read, and parsed exactly once; the rewriters are folded
    /// over the one syntax tree in order) — and returns a structured report.
    /// Presentation (JSON vs. human text) is the caller's concern.
    /// <para>Because the fold composes rewriters in memory, <c>dryRun</c> now
    /// reports exactly what an apply would produce (previously dry-run ran each
    /// rule against the ORIGINAL file, so preview could diverge from apply).</para>
    /// </summary>
    /// <param name="error">On failure (invalid repo path / path-escape / bad
    /// specificFile), the error message; <see langword="null"/> on success. When
    /// non-null the return value is <see langword="null"/>.</param>
    internal AutoFixAllReport? RunAll(string repoPath, string? specificFile, bool dryRun, out string? error)
    {
        error = null;

        if (PathGuard.ValidateRepoPath(repoPath) is { } repoErr)
        {
            error = repoErr;
            return null;
        }

        // C1 mitigation: see MafAutoFix for the rationale. Same containment check
        // applies — RunAll executes every rewriter, so a path escape here is more
        // severe (multiplies blast radius across rewriters).
        if (!string.IsNullOrEmpty(specificFile))
        {
            try { PathGuard.ValidateContainment(repoPath, specificFile, nameof(specificFile)); }
            catch (ArgumentException ex)
            {
                error = $"Error: {ex.Message}";
                return null;
            }
            if (ValidateSpecificFile(repoPath, specificFile) is { } fileErr) // WM-41
            {
                error = fileErr;
                return null;
            }
        }

        var rewriters = OrderedRuleIds
            .Where(_factories.ContainsKey)
            .Select(id => (RuleId: id, Rewriter: _factories[id]()))
            .ToList();

        // WM-44: serialise concurrent applies on the same repo (apply mode only —
        // a dry-run writes nothing and needs no lock).
        IDisposable? applyLock = null;
        if (!dryRun)
        {
            applyLock = TryAcquireApplyLock(repoPath, out var lockErr);
            if (applyLock is null) { error = lockErr; return null; }
        }
        using var _lock = applyLock;

        var outcomes = RunPipeline(rewriters, repoPath, specificFile, dryRun);

        var perRule = rewriters
            .Select(r =>
            {
                var files = outcomes.Where(o => o.ChangedByRules.Contains(r.RuleId))
                                    .Select(o => o.Relative).ToList();
                return new AutoFixRuleReport(r.RuleId, files.Count, files);
            })
            .ToList();

        var allChanged = new SortedSet<string>(
            outcomes.Where(o => o.ChangedByRules.Count > 0).Select(o => o.Relative),
            StringComparer.Ordinal);

        var errors = outcomes.Where(o => o.Error is not null).Select(o => o.Error!).ToList();

        // WM-24: a per-file unified diff so the preview is actually reviewable.
        // Reuses BeforeAfterTool's secret-redacting renderer; each diff is line-capped.
        var diffs = outcomes
            .Where(o => o.ChangedByRules.Count > 0 && o.OldText is not null && o.NewText is not null)
            .Select(o => new AutoFixFileDiff(
                o.Relative,
                o.ChangedByRules,
                CapLines(BeforeAfterTool.RenderUnifiedDiff(o.OldText!, o.NewText!, contextLines: 2), maxLines: 200)))
            .ToList();

        return new AutoFixAllReport(
            DryRun: dryRun,
            OrderOfExecution: OrderedRuleIds,
            TotalDistinctFilesChanged: allChanged.Count,
            AffectedFiles: allChanged.ToList(),
            PerRule: perRule,
            Errors: errors,
            Diffs: diffs);
    }

    /// <summary>Truncates a rendered diff to <paramref name="maxLines"/> lines with
    /// a pointer to the full form, so one huge file cannot flood the terminal.</summary>
    private static string CapLines(string text, int maxLines)
    {
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return text;
        return string.Join('\n', lines.Take(maxLines))
            + $"\n… (+{lines.Length - maxLines} more diff lines — re-run with --json for the full diff)";
    }

    /// <summary>Per-file result of one pipeline pass. <see cref="OldText"/> /
    /// <see cref="NewText"/> are captured only for changed files, for the
    /// preview diff (WM-24).</summary>
    private sealed record FileOutcome(
        string Relative,
        IReadOnlyList<string> ChangedByRules,
        AutoFixError? Error,
        string? OldText = null,
        string? NewText = null);

    /// <summary>
    /// The single per-file engine shared by <see cref="MafAutoFix"/> (one rewriter)
    /// and <see cref="RunAll"/> (all rewriters). Enumerates every candidate file
    /// ONCE, reads + parses it ONCE, folds the <paramref name="rewriters"/> over the
    /// one tree in order (attributing each change to its rule via a reference check),
    /// and writes ONCE at the end preserving the file's original encoding. Per-file
    /// failures are recorded, never thrown — so one unreadable/locked file can no
    /// longer abort the whole pass.
    /// </summary>
    private static IReadOnlyList<FileOutcome> RunPipeline(
        IReadOnlyList<(string RuleId, IRuleRewriter Rewriter)> rewriters,
        string repoPath, string? specificFile, bool dryRun)
    {
        var outcomes = new List<FileOutcome>();

        foreach (var file in EnumerateFiles(repoPath, specificFile))
        {
            // Compute the relative path up-front so the failure path never calls
            // MakeRelative again (an OS symlink race between enumeration and the
            // catch could otherwise double-fault). Falls back to the raw path.
            string relative;
            try { relative = SourceFileWalker.MakeRelative(repoPath, file); }
            catch (InvalidOperationException) { relative = file; }

            // WM-20: read with a strict UTF-8 decoder + BOM detection so an
            // undecodable file is SKIPPED-with-error rather than corrupted to
            // U+FFFD, and the detected encoding is preserved on write-back.
            if (!TryReadSourcePreservingEncoding(file, out var src, out var encoding, out var readError))
            {
                outcomes.Add(new FileOutcome(relative, Array.Empty<string>(),
                    new AutoFixError(relative, readError!)));
                continue;
            }

            try
            {
                var root = SourceParsing.Parse(src).GetRoot();
                var changedBy = new List<string>();
                foreach (var (ruleId, rewriter) in rewriters)
                {
                    var next = rewriter.Visit(root);
                    if (!ReferenceEquals(next, root))
                    {
                        changedBy.Add(ruleId);
                        root = next;
                    }
                }

                if (changedBy.Count == 0)
                {
                    outcomes.Add(new FileOutcome(relative, changedBy, null));
                    continue;
                }

                var newText = root.ToFullString();
                if (!dryRun)
                    SafeWorkspaceWriter.WriteAtomic(repoPath, file, newText, encoding);

                outcomes.Add(new FileOutcome(relative, changedBy, null, OldText: src, NewText: newText));
            }
            catch (Exception ex)
            {
                outcomes.Add(new FileOutcome(relative, Array.Empty<string>(),
                    new AutoFixError(relative, Scrub(repoPath, file, relative, ex.Message))));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Reads <paramref name="file"/> as text while (a) detecting a BOM and
    /// preserving the resulting encoding for a faithful write-back, and (b)
    /// rejecting invalid UTF-8 rather than silently substituting U+FFFD. Returns
    /// <see langword="false"/> with a message on an undecodable file.
    /// </summary>
    private static bool TryReadSourcePreservingEncoding(
        string file, out string text, out Encoding encoding, out string? error)
    {
        text = string.Empty;
        encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        error = null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A locked / permission-denied / vanished file must be a per-file error,
            // NOT a whole-pass abort — the strict-decode catch below only covers
            // invalid bytes. Message carries no absolute path (the caller adds the
            // repo-relative name); FileNotFound/DirectoryNotFound are IOExceptions.
            error = $"could not be read ({ex.GetType().Name})";
            return false;
        }

        // Detect a BOM BY HAND and pick the encoding to PRESERVE on write-back.
        // Doing it manually (rather than via StreamReader's
        // detectEncodingFromByteOrderMarks) is what keeps the strict, throw-on-
        // invalid-bytes decode in force even for a BOM'd file: StreamReader silently
        // swaps in the detected encoding's REPLACEMENT-fallback decoder, so invalid
        // bytes after a BOM would become U+FFFD instead of being rejected.
        // UTF-32 BOMs must be checked BEFORE UTF-16: a UTF-32 LE file starts
        // `FF FE 00 00`, whose first two bytes also match the UTF-16 LE BOM — sniffing
        // UTF-16 first would strip 2 bytes and decode the UTF-32 payload as UTF-16
        // (silent garbage, no throw). Real UTF-16 files never begin with a U+0000, so
        // the trailing `00 00` disambiguates.
        Encoding detected;
        int bomLength;
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        { detected = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true); bomLength = 4; }
        else if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        { detected = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true); bomLength = 4; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { detected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true); bomLength = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { detected = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true); bomLength = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { detected = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true); bomLength = 2; }
        else
        { detected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true); bomLength = 0; }

        try
        {
            text = detected.GetString(bytes, bomLength, bytes.Length - bomLength);
            encoding = detected;
            return true;
        }
        catch (DecoderFallbackException)
        {
            error = "not valid text (invalid byte sequence); skipped to avoid corruption";
            return false;
        }
    }

    /// <summary>Best-effort removal of absolute host paths from an exception
    /// message before it is surfaced to the caller (IOException messages embed
    /// the full path). Keeps the repo-relative form the rest of the output uses.</summary>
    private static string Scrub(string repoPath, string file, string relative, string message) =>
        message.Replace(file, relative, StringComparison.OrdinalIgnoreCase)
               .Replace(repoPath, "<repo>", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WM-41 guard: a <paramref name="specificFile"/> that does not exist, or is
    /// not a .cs file, must be an explicit error — not a silent 0-change success.
    /// Returns the error message, or <see langword="null"/> when the file is valid.
    /// Callers MUST have already run <see cref="PathGuard.ValidateContainment"/>.
    /// </summary>
    private static string? ValidateSpecificFile(string repoPath, string specificFile)
    {
        var resolved = Path.IsPathRooted(specificFile)
            ? specificFile
            : Path.Combine(repoPath, specificFile);

        if (!File.Exists(resolved))
            return $"Error: specificFile not found under repoPath: {specificFile}";
        if (!resolved.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return $"Error: specificFile must be a .cs file: {specificFile}";
        return null;
    }

    /// <summary>
    /// WM-44: cross-process apply lock. Two concurrent apply runs on the same repo
    /// could interleave their read → rewrite → write cycles and silently revert
    /// each other's fixes. An exclusive OS file lock (keyed by the repo's canonical
    /// path, kept in the temp dir so the repo itself is never polluted) serialises
    /// applies; a second concurrent apply fails fast. Returns the held lock —
    /// dispose to release — or <see langword="null"/> with an error message.
    /// </summary>
    private static IDisposable? TryAcquireApplyLock(string repoPath, out string? error)
    {
        error = null;
        try
        {
            return new FileStream(ApplyLockPath(repoPath), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A held lock surfaces as a sharing violation (IOException) OR, on the
            // Windows delete-pending race when a peer is releasing its DeleteOnClose
            // lock, as UnauthorizedAccessException. An unwritable temp dir also lands
            // here. Either way, fail gracefully instead of throwing out of the tool.
            error = "Error: another maf-doctor autofix is already applying changes to this repository (or the temp directory is not writable). Re-run once it finishes.";
            return null;
        }
    }

    /// <summary>Deterministic per-repo apply-lock path (temp dir; canonical-path hash,
    /// case-folded on Windows). Internal for test coordination.</summary>
    internal static string ApplyLockPath(string repoPath)
    {
        // TrimEndingDirectorySeparator so `C:\repo` and `C:\repo\` (which
        // Path.GetFullPath keeps distinct) collapse to ONE lock key — else two
        // callers spelling the path differently would take different locks and
        // fail to serialise, defeating the point of WM-44.
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)), 0, 8);
        return Path.Combine(Path.GetTempPath(), $"maf-doctor-autofix-{hash}.lock");
    }

    /// <summary>
    /// Files to scan. If <paramref name="specificFile"/> is set, restrict to
    /// that single file (resolved relative to <paramref name="repoPath"/>).
    ///
    /// Callers MUST have validated containment of <paramref name="specificFile"/>
    /// via <see cref="PathGuard.ValidateContainment"/> upstream — this helper
    /// does not re-validate; it assumes the input has already been jail-checked
    /// against <paramref name="repoPath"/>. The public <c>MafAutoFix</c> and
    /// <c>MafAutoFixAll</c> entry points do this guard before reaching here.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string repoPath, string? specificFile)
    {
        if (string.IsNullOrEmpty(specificFile))
            return SourceFileWalker.EnumerateCsFiles(repoPath);

        // Containment already verified by the public entry point. Path.Combine
        // handles both relative-segment and (already-validated) absolute forms.
        var resolved = Path.IsPathRooted(specificFile)
            ? specificFile
            : Path.Combine(repoPath, specificFile);

        return File.Exists(resolved) ? new[] { resolved } : Array.Empty<string>();
    }

}
