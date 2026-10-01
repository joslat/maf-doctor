using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafEstimateCost
///
/// Token-cost auditor. Walks every <c>RunAsync</c> / <c>RunStreamingAsync</c>
/// call site, attributes it to the nearest <c>ChatOptions</c> initializer in
/// the same method/class, and flags the bug class that gets shipped most often
/// in production: missing <c>MaxOutputTokens</c> (cost runaway), missing
/// caching, no rate limit.
///
/// This is the "unsexy enterprise differentiator" feature — production teams
/// pay for prompt bloat in ways no LLM SaaS tool catches.
/// </summary>
[McpServerToolType]
public sealed class EstimateCostTool
{
    /// <summary>Rule-of-thumb chars per token.</summary>
    private const int CharsPerToken = 4;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("""
        Static token-cost audit of every `RunAsync` / `RunStreamingAsync` call
        site. Reports per-call-site:
          - Whether `MaxOutputTokens` is set on the nearest `ChatOptions` (cost cap)
          - Estimated input-token cost (sum of nearest `Instructions` literal length / 4)
          - Estimated output-token cap (MaxOutputTokens value, or "unbounded" if missing)
          - Flags any call site without a `MaxOutputTokens` cap as unbounded-cost risk

        Input:
          - repoPath: absolute path to the repository root.

        Returns a markdown report with per-call-site rows and aggregate stats.
        """)]
    public string MafEstimateCost(
        [Description("Absolute path to the repository root.")] string repoPath)
    {
        if (PathGuard.ValidateRepoPath(repoPath) is { } err) return err;

        var findings = new List<CostFinding>();
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
            findings.AddRange(AnalyzeSource(source, rel));
        }

        return FormatReport(repoPath, findings, budget, skippedUnreadable);
    }

    // -------------------------------------------------------------------------
    // Pure analysis core (testable)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walks the source tree, locates every <c>RunAsync</c> / <c>RunStreamingAsync</c>
    /// invocation, and attributes each to the nearest <c>ChatOptions</c> initializer
    /// in the same compilation unit. Returns per-call-site findings. Pure: no I/O.
    /// </summary>
    public static IReadOnlyList<CostFinding> AnalyzeSource(string source, string fileName = "<inline>")
        => AnalyzeSource(SourceParsing.Parse(source).GetRoot(), fileName);

    /// <summary>
    /// WM-16 overload: analyze an already-parsed <paramref name="root"/> so
    /// <c>DoctorTool</c> can parse each file once and share the tree across all four
    /// per-file scanners. The string overload above parses and delegates here.
    /// </summary>
    public static IReadOnlyList<CostFinding> AnalyzeSource(
        Microsoft.CodeAnalysis.SyntaxNode root, string fileName = "<inline>")
    {
        var findings = new List<CostFinding>();

        // Build a view of ChatOptions initializers in this file — each captures its
        // syntax node (for REP-17 member-span scoping) plus file-position, MaxOutputTokens
        // value (if set), and the Instructions literal length.
        var chatOptions = root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(IsChatOptionsCreation)
            .Select(n => (Node: n, Info: ExtractChatOptionsInfo(n)))
            .ToList();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
            var name = member.Name.Identifier.ValueText;
            if (name is not ("RunAsync" or "RunStreamingAsync")) continue;

            // Skip Task.Run, ValueTask.Run, etc. — only agent-style invocations.
            // The heuristic: the receiver expression isn't a static type Task / ValueTask.
            var receiver = member.Expression.ToString();
            if (receiver is "Task" or "ValueTask"
                || receiver.EndsWith(".Task", StringComparison.Ordinal)
                || receiver.EndsWith(".ValueTask", StringComparison.Ordinal))
                continue;

            // Skip well-known NON-agent `RunAsync`/`RunStreamingAsync` receivers. Without a
            // SemanticModel the rule can't resolve types, and matching by method name alone
            // flagged the ASP.NET host run loop (`app.RunAsync()`) and the workflow runners
            // (`InProcessExecution.RunStreamingAsync(workflow, …)`, `workflow.RunAsync(…)`)
            // as uncapped *agent* calls — neither takes ChatOptions/MaxOutputTokens. A
            // genuine agent named exactly one of these is vanishingly unlikely. (A future
            // SemanticModel pass should replace this denylist with an AIAgent-type check.)
            if (IsNonAgentRunReceiver(member.Expression))
                continue;

            var loc = invocation.GetLocation().GetLineSpan();
            var line = loc.StartLinePosition.Line + 1;

            // Merge every ChatOptions/ChatClientAgentOptions creation above this
            // invocation, taking the most-recent NON-NULL value for each field.
            // This handles the common pattern:
            //   var opts = new ChatOptions { MaxOutputTokens = 512 };          // line N
            //   var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            //   {                                                              // line N+1
            //       ChatOptions = opts,   // references the var; no MaxTokens directly
            //   });
            //   await agent.RunAsync(...);                                     // line N+2
            // Without merge, the wrapper at line N+1 would shadow the real options at line N.
            // REP-17: scope the merge to the invocation's enclosing member so a ChatOptions
            // in an UNRELATED method/property/field above can't be inherited — that produced
            // false negatives (an uncapped call reported as capped). Top-level statements
            // (no enclosing member) fall back to file scope, preserving that pattern.
            var enclosingMember = invocation.Ancestors().FirstOrDefault(a =>
                a is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax);
            var memberSpan = enclosingMember?.Span;

            int? maxTokens = null;
            int? estimatedInputTokens = null;
            foreach (var co in chatOptions
                .Where(o => o.Info.Line <= line
                         && (memberSpan is null || memberSpan.Value.Contains(o.Node.Span)))
                .OrderBy(o => o.Info.Line))
            {
                if (co.Info.MaxOutputTokens is not null) maxTokens = co.Info.MaxOutputTokens;
                if (co.Info.InstructionsLiteralChars > 0)
                    estimatedInputTokens = co.Info.InstructionsLiteralChars / CharsPerToken;
            }

            findings.Add(new CostFinding(
                File: fileName,
                Line: line,
                CallSite: name,
                ReceiverExpression: receiver,
                MaxOutputTokens: maxTokens,
                EstimatedInputTokens: estimatedInputTokens,
                HasCapWarning: maxTokens is null,
                HasInputUnknownWarning: estimatedInputTokens is null));
        }

        return findings;
    }

    /// <summary>
    /// Well-known non-agent receivers of a <c>RunAsync</c>/<c>RunStreamingAsync</c> call:
    /// the ASP.NET host run loop and the MAF workflow runners. Matched by the receiver's
    /// rightmost identifier, so it covers `app.RunAsync()`, `host.RunAsync()`,
    /// `webApp.RunAsync()`, command entry points such as `InitCommand.RunAsync(…)`,
    /// host builders such as `builder.Build().RunAsync()`, and
    /// `InProcessExecution.RunStreamingAsync(…)`.
    /// Syntactic denylist (no SemanticModel) — see call site.
    /// </summary>
    private static readonly HashSet<string> NonAgentRunReceivers = new(StringComparer.Ordinal)
    {
        "app", "host", "webApp", "webApplication", "application",   // ASP.NET / generic host
        "WebApplication", "IHost", "Host",                          // host types (static-ish refs)
        "InProcessExecution",                                       // MAF workflow runner (the real one)
        // NOTE: deliberately NOT "workflow"/"Workflow" — a workflow-as-agent
        // (`var workflow = chatClient.AsAIAgent(...); await workflow.RunAsync(...)`)
        // IS a real AIAgent whose uncapped call we WANT to flag. The actual workflow
        // RUNNER is `InProcessExecution.RunStreamingAsync(workflow, …)` (receiver above),
        // so excluding the variable name would only drop true positives.
    };

    /// <summary>True if the call's receiver is a known non-agent host/workflow runner.</summary>
    private static bool IsNonAgentRunReceiver(ExpressionSyntax receiver)
    {
        // Generic/MCP host startup. The receiver is the result of a parameterless
        // Build() call, not an AIAgent. Keeping this structural avoids denylisting
        // the variable name `builder`, which can legitimately build agents too.
        if (receiver is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Build",
                },
                ArgumentList.Arguments.Count: 0,
            }
            && receiver.Parent?.Parent is InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
            })
            return true;

        var simple = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            _ => null,
        };
        // CLI command types expose their own RunAsync entry points. They are static
        // orchestration methods and never consume ChatOptions/MaxOutputTokens.
        return simple is not null
            && (NonAgentRunReceivers.Contains(simple)
                || simple.EndsWith("Command", StringComparison.Ordinal));
    }

    private static bool IsChatOptionsCreation(ObjectCreationExpressionSyntax oce)
    {
        var typeName = oce.Type.ToString();
        var simpleName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        return simpleName is "ChatOptions" or "ChatClientAgentOptions";
    }

    private static ChatOptionsInfo ExtractChatOptionsInfo(ObjectCreationExpressionSyntax oce)
    {
        var line = oce.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        int? maxTokens = null;
        int instructionsChars = 0;

        if (oce.Initializer is not null)
        {
            foreach (var expr in oce.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                var leftText = expr.Left.ToString();
                if (leftText == "MaxOutputTokens" && expr.Right is LiteralExpressionSyntax lit
                    && lit.Token.Value is int n)
                {
                    maxTokens = n;
                }
                else if (leftText == "Instructions")
                {
                    instructionsChars = ExtractLiteralLength(expr.Right);
                }
                else if (leftText == "ChatOptions" && expr.Right is ObjectCreationExpressionSyntax nestedOce
                         && IsChatOptionsCreation(nestedOce))
                {
                    // ChatClientAgentOptions has a nested ChatOptions — recurse.
                    var nested = ExtractChatOptionsInfo(nestedOce);
                    maxTokens ??= nested.MaxOutputTokens;
                    if (nested.InstructionsLiteralChars > instructionsChars)
                        instructionsChars = nested.InstructionsLiteralChars;
                }
            }
        }

        return new ChatOptionsInfo(line, maxTokens, instructionsChars);
    }

    private static int ExtractLiteralLength(ExpressionSyntax expr) => expr switch
    {
        LiteralExpressionSyntax lit when lit.Kind() == SyntaxKind.StringLiteralExpression
            => lit.Token.ValueText.Length,
        InterpolatedStringExpressionSyntax interp
            when interp.Contents.All(c => c is InterpolatedStringTextSyntax)
            => interp.Contents
                .OfType<InterpolatedStringTextSyntax>()
                .Sum(t => t.TextToken.ValueText.Length),
        _ => 0,
    };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IEnumerable<string> EnumerateScannableFiles(string repoRoot, SourceFileWalker.ScanBudget budget)
        => SourceFileWalker.EnumerateCsFiles(repoRoot, excludes: null, budget);

    private static string MakeRelative(string root, string file)
        => SourceFileWalker.MakeRelative(root, file);

    // -------------------------------------------------------------------------
    // Report
    // -------------------------------------------------------------------------

    private static string FormatReport(
        string repoPath,
        IReadOnlyList<CostFinding> findings,
        SourceFileWalker.ScanBudget? budget = null,
        int skippedUnreadable = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 💰 MAF token-cost audit");
        sb.AppendLine();
        sb.AppendLine($"**Repo:** `{repoPath}`");
        sb.AppendLine();

        // SEC-04: never present a capped / partial walk as a complete scan.
        if (budget?.IncompleteNote(skippedUnreadable) is { } note)
        {
            sb.AppendLine($"> ⚠️ **Scan incomplete** — {note}. Findings below reflect a partial scan, not the whole repo.");
            sb.AppendLine();
        }

        if (findings.Count == 0)
        {
            sb.AppendLine("ℹ️ No `RunAsync` / `RunStreamingAsync` call sites detected. Nothing to audit.");
            return sb.ToString();
        }

        var unbounded = findings.Count(f => f.HasCapWarning);
        var unknownInput = findings.Count(f => f.HasInputUnknownWarning);
        var totalInputTokens = findings.Sum(f => f.EstimatedInputTokens ?? 0);
        var totalOutputCap = findings.Sum(f => f.MaxOutputTokens ?? 0);

        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|---|---:|");
        sb.AppendLine($"| Total agent call sites | {findings.Count} |");
        sb.AppendLine($"| 🚨 Unbounded (no MaxOutputTokens) | {unbounded} |");
        sb.AppendLine($"| ⚠️ Input size unknown (no nearby Instructions literal) | {unknownInput} |");
        sb.AppendLine($"| Estimated total input tokens (per single call across all sites) | {totalInputTokens} |");
        sb.AppendLine($"| Sum of all MaxOutputTokens caps | {totalOutputCap} |");
        sb.AppendLine();

        sb.AppendLine("### Per call-site");
        sb.AppendLine();
        sb.AppendLine("| File | Line | Call | MaxOutputTokens | Est. input tokens | Flag |");
        sb.AppendLine("|---|---:|---|---:|---:|---|");
        foreach (var f in findings.OrderByDescending(f => f.HasCapWarning))
        {
            var cap = f.MaxOutputTokens?.ToString() ?? "(unset)";
            var input = f.EstimatedInputTokens?.ToString() ?? "?";
            var flags = string.Concat(
                f.HasCapWarning ? "🚨 unbounded " : "",
                f.HasInputUnknownWarning ? "⚠️ no nearby Instructions" : "");
            sb.AppendLine($"| `{f.File}` | {f.Line} | `{f.CallSite}` on `{f.ReceiverExpression}` | {cap} | {input} | {flags.TrimEnd()} |");
        }
        sb.AppendLine();

        if (unbounded > 0)
        {
            sb.AppendLine("### Fix");
            sb.AppendLine();
            sb.AppendLine("Each unbounded call site should set `MaxOutputTokens` on the `ChatOptions`:");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine("var opts = new ChatOptions { MaxOutputTokens = 1024 /* cap for THIS agent */ };");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("`MafNewAgent` generates agents with `MaxOutputTokens = 1024` by default — re-scaffold or copy the pattern.");
        }

        return sb.ToString();
    }
}

// -----------------------------------------------------------------------------
// Models
// -----------------------------------------------------------------------------

public sealed record CostFinding(
    string File,
    int Line,
    string CallSite,
    string ReceiverExpression,
    int? MaxOutputTokens,
    int? EstimatedInputTokens,
    bool HasCapWarning,
    bool HasInputUnknownWarning);

internal sealed record ChatOptionsInfo(int Line, int? MaxOutputTokens, int InstructionsLiteralChars);
