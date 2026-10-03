using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MafDoctor.Tools.Rewriters;

/// <summary>
/// Rule: <c>MAF-AP-SEC-003</c> / <c>MAF003</c>.
///
/// Rewrite: any <c>EnableSensitiveData = true</c> assignment is dropped.
/// The property defaults to false, so removal restores the safe default.
///
/// <para>Four shapes handled (parity with <see cref="Analyzers.SensitiveDataAnalyzer"/>):</para>
/// <list type="bullet">
///   <item>A. Object-initializer entry — <c>new Options { EnableSensitiveData = true }</c>.
///         Removed from the initializer's expression list.</item>
///   <item>B. Dictionary-initializer entry — <c>new Dict { ["EnableSensitiveData"] = true }</c>.
///         Removed from the initializer's expression list.</item>
///   <item>C. Member-access assignment statement — <c>opts.EnableSensitiveData = true;</c>.
///         The whole statement is removed via <see cref="VisitExpressionStatement"/>.</item>
///   <item>D. Standalone dictionary-key assignment statement — <c>dict["EnableSensitiveData"] = true;</c>.
///         Same removal path.</item>
///   <item>E. Expression-bodied lambda — <c>cfg =&gt; cfg.EnableSensitiveData = true</c>, the form
///         Microsoft documents for <c>UseOpenTelemetry(configure: …)</c>. The body becomes
///         an empty block: <c>cfg =&gt; {}</c>.</item>
/// </list>
///
/// <para>Phase 4.G fixup: shapes C and D were missing pre-Phase-4.G. The analyzer
/// (<see cref="Analyzers.SensitiveDataAnalyzer"/>) was extended to all four shapes
/// in Phase 4.4, which created an asymmetry where the IDE warned but
/// <c>MafAutoFix</c> silently left the assignment in place. This rewriter now
/// matches.</para>
/// </summary>
internal sealed class EnableSensitiveDataRewriter : CSharpSyntaxRewriter, IRuleRewriter
{
    public string RuleId => "MAF-AP-SEC-003";

    public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
    {
        // Recurse FIRST so a nested `EnableSensitiveData = true` inside a SURVIVING
        // sibling entry is cleaned too — the autofix pipeline runs each rewriter
        // exactly once (no fixpoint pass), so a missed nested flag would otherwise
        // silently stay enabled.
        var visited = (InitializerExpressionSyntax)base.VisitInitializerExpression(node)!;

        var toRemove = visited.Expressions
            .Where(IsEnableSensitiveDataTrueAssignment)
            .ToList();

        if (toRemove.Count == 0)
            return visited;

        // WM-22: the previous `SyntaxFactory.SeparatedList(filtered)` rebuild dropped
        // ALL separator trivia — collapsing the surviving entries onto one line and
        // deleting their comments. RemoveNodes excises each flagged entry together
        // with its OWN adjacent separator while leaving the survivors' separators —
        // newlines, inline comments, trailing comma — intact.
        return visited.RemoveNodes(toRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
    }

    /// <summary>
    /// Statement-level visitor for shapes C + D (member access and standalone
    /// element access).
    ///
    /// <para>When the parent is a <see cref="BlockSyntax"/>, returns
    /// <c>null</c> to remove the statement from the block — Roslyn's
    /// syntax-list semantics treat a null Visit result as "remove this
    /// child". Most call sites land here (`{ … }` body).</para>
    ///
    /// <para>When the parent is NOT a block (single-line <c>if</c>,
    /// <c>for</c>, <c>while</c>, <c>else</c>, <c>using</c> without braces),
    /// returns a no-op empty statement (<c>;</c>) instead — Roslyn's
    /// <c>VisitIfStatement</c> (and its siblings) throw
    /// <see cref="ArgumentNullException"/> when their single embedded
    /// child returns null. Substituting an empty statement preserves the
    /// surrounding structure without introducing the offending true-literal
    /// assignment.</para>
    ///
    /// <para>Phase 7-final fixup — the original implementation returned
    /// <c>null</c> unconditionally, which crashed the rewriter on
    /// embedded-statement contexts. The crash was caught at the outer
    /// <see cref="AutoFixTool"/> try/catch (no file corruption) but the
    /// rule silently no-op'd on those files, re-creating the
    /// analyzer/rewriter asymmetry Phase 4.G was meant to close.</para>
    /// </summary>
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax assign
            && assign.Right is LiteralExpressionSyntax valueLit
            && valueLit.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            // Shape C: `opts.EnableSensitiveData = true;`
            bool isShapeC = assign.Left is MemberAccessExpressionSyntax mae
                && mae.Name.Identifier.ValueText == "EnableSensitiveData";

            // Shape D: `dict["EnableSensitiveData"] = true;`
            bool isShapeD = assign.Left is ElementAccessExpressionSyntax eae
                && eae.ArgumentList.Arguments.Count == 1
                && eae.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax keyLit
                && keyLit.IsKind(SyntaxKind.StringLiteralExpression)
                && keyLit.Token.ValueText == "EnableSensitiveData";

            if (isShapeC || isShapeD)
            {
                // Safe to remove only when the parent is a block. Embedded
                // statement contexts (single-line if/for/while/else) require
                // a replacement node — return an empty `;` statement that
                // preserves the parent shape without keeping the assignment.
                if (node.Parent is BlockSyntax)
                {
                    return null;
                }
                return SyntaxFactory.EmptyStatement()
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
            }
        }
        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        return IsMemberAccessTrueAssignment(visited.ExpressionBody)
            ? visited.WithExpressionBody(null).WithBlock(EmptyBodyLike(visited.ExpressionBody!))
            : visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        return IsMemberAccessTrueAssignment(visited.ExpressionBody)
            ? visited.WithExpressionBody(null).WithBlock(EmptyBodyLike(visited.ExpressionBody!))
            : visited;
    }

    // Shape E: `x => x.EnableSensitiveData = true` (the lambda's whole body).
    private static bool IsMemberAccessTrueAssignment(ExpressionSyntax? body) =>
        body is AssignmentExpressionSyntax
        {
            Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "EnableSensitiveData" },
            Right: LiteralExpressionSyntax value,
        } && value.IsKind(SyntaxKind.TrueLiteralExpression);

    private static BlockSyntax EmptyBodyLike(ExpressionSyntax body) =>
        SyntaxFactory.Block().WithLeadingTrivia(body.GetLeadingTrivia()).WithTrailingTrivia(body.GetTrailingTrivia());

    private static bool IsEnableSensitiveDataTrueAssignment(ExpressionSyntax expr)
    {
        if (expr is not AssignmentExpressionSyntax assign)
            return false;

        // Pattern A: `EnableSensitiveData = true`
        if (assign.Left is IdentifierNameSyntax id
            && id.Identifier.ValueText == "EnableSensitiveData"
            && assign.Right is LiteralExpressionSyntax lit
            && lit.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return true;
        }

        // Pattern B: `["EnableSensitiveData"] = true` (collection-initializer).
        if (assign.Left is ImplicitElementAccessSyntax indexer
            && indexer.ArgumentList.Arguments.Count == 1
            && indexer.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax keyLit
            && keyLit.IsKind(SyntaxKind.StringLiteralExpression)
            && keyLit.Token.ValueText == "EnableSensitiveData"
            && assign.Right is LiteralExpressionSyntax valueLit
            && valueLit.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return true;
        }

        return false;
    }
}
