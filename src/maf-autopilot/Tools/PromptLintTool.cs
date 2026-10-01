using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafLintAgentPrompt
///
/// Scans agent <c>Instructions</c> strings across a repo for prompt-quality
/// issues — the class of bugs that no compiler can catch and no anti-pattern
/// scanner addresses today. Agent quality is mostly prompt quality; this is
/// the canonical place to enforce that.
///
/// Rules shipped in v1 (each has a stable rule ID):
///   PROMPT-001 — Empty Instructions (likely accidental).
///   PROMPT-002 — Token-bloated Instructions (likely unstructured / hard to maintain).
///   PROMPT-003 — Missing refusal pattern on a substantive system prompt.
///   PROMPT-004 — Untrusted-input concatenation — prompt-injection risk.
/// </summary>
[McpServerToolType]
public sealed class PromptLintTool
{
    /// <summary>Token-bloat threshold (~2000 tokens at the rule-of-thumb 4 chars/token).</summary>
    public const int BloatCharThreshold = 8000;
    /// <summary>Minimum length above which the refusal-pattern check applies.</summary>
    public const int RefusalCheckMinLength = 500;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Scan every agent `Instructions` string in the repo for prompt-quality
        issues. Surfaces: empty prompts, token bloat (> ~2000 tokens of system
        prompt), missing refusal patterns on substantive prompts, and
        untrusted-input concatenation (prompt-injection risk).

        Input:
          - repoPath: absolute path to the repository root.

        Returns a markdown report with per-finding severity (error / warning),
        the matching rule ID (PROMPT-001..004), the file:line location, and the
        canonical fix description.
        """)]
    public string MafLintAgentPrompt(
        [Description("Absolute path to the repository root.")] string repoPath)
    {
        if (PathGuard.ValidateRepoPath(repoPath) is { } err) return err;

        var findings = new List<PromptFinding>();
        var budget = new SourceFileWalker.ScanBudget();
        var skippedUnreadable = 0;
        foreach (var path in EnumerateScannableFiles(repoPath, budget))
        {
            string source, rel;
            try
            {
                // WM-18: a locked/raced/unreadable file must not abort the whole scan.
                source = File.ReadAllText(path);
                rel = MakeRelative(repoPath, path);
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or InvalidOperationException)
            {
                skippedUnreadable++;
                continue;
            }
            findings.AddRange(LintSource(source, rel));
        }
        return FormatReport(findings, budget, skippedUnreadable);
    }

    // -------------------------------------------------------------------------
    // Pure analysis core (testable)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs every prompt-lint rule against the given source string. Pure: no I/O.
    /// </summary>
    public static IReadOnlyList<PromptFinding> LintSource(string source, string fileName = "<inline>")
        => LintSource(SourceParsing.Parse(source).GetRoot(), fileName);

    /// <summary>
    /// WM-16 overload: lint an already-parsed <paramref name="root"/> so
    /// <c>DoctorTool</c> can parse each file once and share the tree across all four
    /// per-file scanners. The string overload above parses and delegates here.
    /// </summary>
    public static IReadOnlyList<PromptFinding> LintSource(
        Microsoft.CodeAnalysis.SyntaxNode root, string fileName = "<inline>")
    {
        var findings = new List<PromptFinding>();

        // Find every assignment to a member or property named `Instructions`,
        // both at object-initializer position and at regular assignment position.
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var leftText = assignment.Left.ToString();
            var simpleName = leftText.Contains('.') ? leftText[(leftText.LastIndexOf('.') + 1)..] : leftText;
            if (simpleName != "Instructions") continue;

            var line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            findings.AddRange(LintAssignment(assignment.Right, fileName, line));
        }

        // Also consider named arguments — e.g. `new ChatClientAgent(client, instructions: "...")`.
        foreach (var arg in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            var name = arg.NameColon?.Name.Identifier.ValueText;
            if (name != "Instructions" && name != "instructions") continue;
            var line = arg.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            findings.AddRange(LintAssignment(arg.Expression, fileName, line));
        }

        return findings;
    }

    private static IEnumerable<PromptFinding> LintAssignment(ExpressionSyntax expr, string file, int line)
    {
        // PROMPT-004 — Untrusted-input concatenation (string + non-literal OR
        // interpolated string with non-literal parts). This is the prompt-injection
        // risk: user-controlled data flowed into the system prompt without templating.
        if (expr is BinaryExpressionSyntax bin && bin.OperatorToken.IsKind(SyntaxKind.PlusToken))
        {
            // Walk the concatenation tree and check for any non-literal operand.
            var hasNonLiteral = HasNonLiteralOperand(bin);
            if (hasNonLiteral)
            {
                yield return new PromptFinding(
                    RuleId: "PROMPT-004",
                    Severity: PromptSeverity.Error,
                    Message: "Instructions assigned via string concatenation that includes a non-literal operand — prompt-injection risk. Use a structured templating function (e.g., string.Format with sanitized inputs, or a dedicated prompt-template library).",
                    File: file,
                    Line: line);
            }
        }

        if (expr is InterpolatedStringExpressionSyntax interp)
        {
            // Any interpolation hole `{var}` with a non-constant value is a risk.
            var nonConstantHole = interp.Contents
                .OfType<InterpolationSyntax>()
                .Any(i => !IsCompileTimeConstant(i.Expression));
            if (nonConstantHole)
            {
                yield return new PromptFinding(
                    RuleId: "PROMPT-004",
                    Severity: PromptSeverity.Error,
                    Message: "Instructions assigned via interpolated string with non-constant holes — prompt-injection risk. Sanitize the hole values or use a structured prompt template.",
                    File: file,
                    Line: line);
            }
        }

        // Extract a literal string for length / refusal checks. If the expression
        // is not a single literal we cannot reason about its content here; skip
        // those checks (they're false-positive-prone on dynamic prompts).
        var literal = TryExtractLiteral(expr);
        if (literal is null) yield break;

        // PROMPT-001 — Empty Instructions.
        if (string.IsNullOrWhiteSpace(literal))
        {
            yield return new PromptFinding(
                RuleId: "PROMPT-001",
                Severity: PromptSeverity.Warning,
                Message: "Instructions is empty or whitespace-only. Either provide a system prompt or remove the assignment.",
                File: file,
                Line: line);
            yield break;
        }

        // PROMPT-002 — Token bloat.
        if (literal.Length > BloatCharThreshold)
        {
            var approxTokens = literal.Length / 4;
            yield return new PromptFinding(
                RuleId: "PROMPT-002",
                Severity: PromptSeverity.Warning,
                Message: $"Instructions is {literal.Length} chars (~{approxTokens} tokens) — consider splitting into smaller composable prompts or moving long context out into tool calls.",
                File: file,
                Line: line);
        }

        // PROMPT-003 — Missing refusal pattern on a substantive prompt.
        if (literal.Length >= RefusalCheckMinLength && !ContainsRefusalPattern(literal))
        {
            yield return new PromptFinding(
                RuleId: "PROMPT-003",
                Severity: PromptSeverity.Warning,
                Message: "Instructions is substantive but contains no refusal pattern (do not / refuse / cannot / must not / decline / won't). Add explicit refusal guidance to harden the agent against off-topic or unsafe requests.",
                File: file,
                Line: line);
        }
    }

    // -------------------------------------------------------------------------
    // Heuristics
    // -------------------------------------------------------------------------

    // Phase 7.G fixup — regex hygiene. Bounded alternation is structurally
    // backtracking-safe; explicit NonBacktracking + 2s timeout matches the
    // project-wide invariant.
    private static readonly Regex RefusalPatternRegex = new(
        @"\b(?:do not|don't|refuse|cannot|can't|must not|mustn't|decline|won't|never|forbidden|prohibited|not allowed|out of scope)\b",
        RegexOptions.Compiled | RegexOptions.NonBacktracking | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    internal static bool ContainsRefusalPattern(string s) => RefusalPatternRegex.IsMatch(s);

    private static bool HasNonLiteralOperand(BinaryExpressionSyntax bin)
    {
        bool Walk(ExpressionSyntax e) => e switch
        {
            LiteralExpressionSyntax => false,
            BinaryExpressionSyntax b when b.OperatorToken.IsKind(SyntaxKind.PlusToken)
                => Walk(b.Left) || Walk(b.Right),
            // Anything else (identifier, member-access, invocation) is non-literal.
            _ => true,
        };
        return Walk(bin.Left) || Walk(bin.Right);
    }

    private static bool IsCompileTimeConstant(ExpressionSyntax expr) => expr switch
    {
        LiteralExpressionSyntax => true,
        BinaryExpressionSyntax b when b.OperatorToken.IsKind(SyntaxKind.PlusToken)
            => IsCompileTimeConstant(b.Left) && IsCompileTimeConstant(b.Right),
        _ => false,
    };

    /// <summary>
    /// Best-effort: extracts the string value if the expression is a single
    /// literal (regular OR raw OR verbatim OR interpolated-no-holes). Returns
    /// null for anything dynamic so the length checks don't fire on unknown content.
    /// </summary>
    private static string? TryExtractLiteral(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax lit when lit.Kind() == SyntaxKind.StringLiteralExpression:
                return lit.Token.ValueText;
            case InterpolatedStringExpressionSyntax interp
                when interp.Contents.All(c => c is InterpolatedStringTextSyntax):
                return string.Concat(interp.Contents
                    .OfType<InterpolatedStringTextSyntax>()
                    .Select(t => t.TextToken.ValueText));
            case BinaryExpressionSyntax bin when bin.OperatorToken.IsKind(SyntaxKind.PlusToken):
                var left = TryExtractLiteral(bin.Left);
                var right = TryExtractLiteral(bin.Right);
                return (left, right) switch
                {
                    (not null, not null) => left + right,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static IEnumerable<string> EnumerateScannableFiles(string repoRoot, SourceFileWalker.ScanBudget budget)
        => SourceFileWalker.EnumerateCsFiles(repoRoot, excludes: null, budget);

    private static string MakeRelative(string root, string file)
        => SourceFileWalker.MakeRelative(root, file);

    // -------------------------------------------------------------------------
    // Report formatting
    // -------------------------------------------------------------------------

    private static string FormatReport(
        IReadOnlyList<PromptFinding> findings,
        SourceFileWalker.ScanBudget? budget = null,
        int skippedUnreadable = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🪶 MAF prompt-lint report");
        sb.AppendLine();

        // SEC-04: never present a capped / partial walk as a complete scan.
        if (budget?.IncompleteNote(skippedUnreadable) is { } note)
        {
            sb.AppendLine($"> ⚠️ **Scan incomplete** — {note}. Findings below reflect a partial scan, not the whole repo.");
            sb.AppendLine();
        }

        if (findings.Count == 0)
        {
            sb.AppendLine("✅ No prompt-quality issues detected across the scanned codebase.");
            return sb.ToString();
        }

        var errors = findings.Where(f => f.Severity == PromptSeverity.Error).ToList();
        var warnings = findings.Where(f => f.Severity == PromptSeverity.Warning).ToList();

        sb.AppendLine($"| Severity | Count |");
        sb.AppendLine($"|---|---:|");
        sb.AppendLine($"| ❌ Error | {errors.Count} |");
        sb.AppendLine($"| ⚠️ Warning | {warnings.Count} |");
        sb.AppendLine();

        foreach (var group in new[] { ("❌ Errors", errors), ("⚠️ Warnings", warnings) })
        {
            if (group.Item2.Count == 0) continue;
            sb.AppendLine($"### {group.Item1}");
            sb.AppendLine();
            sb.AppendLine("| Rule | File | Line | Message |");
            sb.AppendLine("|---|---|---:|---|");
            foreach (var f in group.Item2)
                sb.AppendLine($"| `{f.RuleId}` | `{f.File}` | {f.Line} | {f.Message} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

public enum PromptSeverity { Warning, Error }

public sealed record PromptFinding(
    string RuleId,
    PromptSeverity Severity,
    string Message,
    string File,
    int Line);
