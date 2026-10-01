using System.ComponentModel;
using System.Text;
using MafDoctor.Tools.Rewriters;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: <c>MafBeforeAfter</c>.
///
/// Given one or more registry rule IDs, render a single unified-diff
/// report showing every proposed change across the codebase. Lets a
/// maintainer review the whole change-set in one document before
/// approving the migration (or before invoking <see cref="AutoFixTool"/>
/// to apply it).
///
/// Conceptually the "preview" half of <see cref="AutoFixTool"/>:
/// it runs the SAME rewriters, but never writes to disk. Output is
/// a markdown document with per-file <c>```diff</c> blocks.
///
/// Phase W.7 (2026-05-13). Depends on W.6's rewriters.
/// </summary>
[McpServerToolType]
public sealed class BeforeAfterTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Preview the changes a set of MafAutoFix rules would make across a
        codebase — WITHOUT writing anything to disk.

        Input:
          - repoPath: absolute path to the repo or project.
          - ruleIds: comma-separated list of rule IDs to preview. Each must
            be one of the IDs supported by MafAutoFix (see that tool's docs).

        Output: a markdown document with one section per rule, listing every
        file that would change + a unified `diff --git`-style block showing
        the before/after for each change. Maintainer reads this ONE document
        and approves/rejects the whole change-set.

        No files are written.
        """)]
    public string MafBeforeAfter(
        [Description("Absolute path to the repo or project to preview.")] string repoPath,
        [Description("Comma-separated rule IDs, e.g. 'MAF-AP-SEC-001,MAF-AP-WF-001'.")] string ruleIds)
    {
        if (PathGuard.ValidateRepoPath(repoPath) is { } err)
            return $"❌ {err}";

        var ids = (ruleIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            return "❌ No ruleIds provided. Pass at least one comma-separated rule ID.";

        var sb = new StringBuilder();
        sb.AppendLine("# Migration Diff Preview");
        sb.AppendLine();
        sb.AppendLine($"**Repo:** `{repoPath}`");
        sb.AppendLine($"**Rules previewed:** `{string.Join("`, `", ids)}`");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        var totalFiles = 0;
        foreach (var ruleId in ids)
        {
            if (!AutoFixTool.TryCreateRewriter(ruleId, out var rewriter))
            {
                sb.AppendLine($"## ⚠️ Rule `{ruleId}` — unknown");
                sb.AppendLine();
                sb.AppendLine($"Not a recognised rule ID. Supported: `{string.Join("`, `", AutoFixTool.SupportedRuleIds)}`.");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"## Rule `{ruleId}` — {rewriter!.GetType().Name}");
            sb.AppendLine();

            var changedInThisRule = 0;
            foreach (var file in SourceFileWalker.EnumerateCsFiles(repoPath))
            {
                var orig = File.ReadAllText(file);
                var tree = SourceParsing.Parse(orig);
                var oldRoot = tree.GetRoot();
                var newRoot = rewriter.Visit(oldRoot);
                if (ReferenceEquals(newRoot, oldRoot)) continue;

                var rewritten = newRoot.ToFullString();
                if (orig == rewritten) continue;

                changedInThisRule++;
                totalFiles++;
                sb.AppendLine($"### File: `{LlmFencing.MdInline(SourceFileWalker.MakeRelative(repoPath, file))}`"); // SEC-02
                sb.AppendLine();
                sb.AppendLine("```diff");
                sb.Append(RenderUnifiedDiff(orig, rewritten, contextLines: 2));
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (changedInThisRule == 0)
            {
                sb.AppendLine("_No changes — no occurrences of this anti-pattern in the codebase._");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"**Summary:** {totalFiles} file change(s) across {ids.Length} rule(s).");
        sb.AppendLine();
        sb.AppendLine("To apply: call `MafAutoFix(repoPath, '<ruleId>')` for each rule (default `dryRun: false`).");

        return sb.ToString();
    }

    /// <summary>
    /// Minimal unified-diff renderer. Splits both inputs into lines, walks
    /// them in lock-step, and emits ±N lines of context around each hunk.
    /// Not a full Myers diff — it's a "show the lines that differ + a bit
    /// of context" renderer that suffices for review.
    ///
    /// Trade-off: if a hunk has interleaved adds and removes, we emit each
    /// changed line individually rather than aggregating. That's fine for
    /// the rewriter use case (each rewriter touches a small surface).
    /// </summary>
    internal static string RenderUnifiedDiff(string before, string after, int contextLines)
    {
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');

        // Find longest common prefix and suffix to bound the change region.
        var prefix = 0;
        while (prefix < beforeLines.Length && prefix < afterLines.Length
            && beforeLines[prefix] == afterLines[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < beforeLines.Length - prefix && suffix < afterLines.Length - prefix
            && beforeLines[beforeLines.Length - 1 - suffix] == afterLines[afterLines.Length - 1 - suffix])
            suffix++;

        if (prefix == beforeLines.Length && prefix == afterLines.Length)
            return string.Empty; // identical

        var ctxStart = Math.Max(0, prefix - contextLines);
        var ctxEndBefore = Math.Min(beforeLines.Length, beforeLines.Length - suffix + contextLines);
        var ctxEndAfter = Math.Min(afterLines.Length, afterLines.Length - suffix + contextLines);

        // Redact a diff line whose content looks like a secret (redact-on-doubt),
        // so the rendered diff doesn't echo a hard-coded key sitting near a change.
        static string Safe(string line)
        {
            // SEC-05: a source line of ``` (or context lines with a leading space) is a
            // valid CommonMark closing fence that would break out of the enclosing ```diff
            // block. NeutralizeFences inserts a zero-width space so it can't close the fence
            // (invisible to humans). Secret check runs first (redact-on-doubt).
            try
            {
                return AntiPatternScannerTool.LooksLikeSecret(line)
                    ? "(redacted — may contain a secret)"
                    : MafDoctor.Data.RegistryService.NeutralizeFences(line);
            }
            catch { return "(redacted — may contain a secret)"; }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"@@ -{ctxStart + 1},{ctxEndBefore - ctxStart} +{ctxStart + 1},{ctxEndAfter - ctxStart} @@");

        // Leading context (unchanged lines before the change).
        for (var i = ctxStart; i < prefix; i++)
            sb.AppendLine($" {Safe(beforeLines[i])}");

        // The change region: emit all removed lines, then all added lines.
        for (var i = prefix; i < beforeLines.Length - suffix; i++)
            sb.AppendLine($"-{Safe(beforeLines[i])}");
        for (var i = prefix; i < afterLines.Length - suffix; i++)
            sb.AppendLine($"+{Safe(afterLines[i])}");

        // Trailing context (unchanged lines after the change).
        for (var i = beforeLines.Length - suffix; i < ctxEndBefore; i++)
            sb.AppendLine($" {Safe(beforeLines[i])}");

        return sb.ToString();
    }
}
