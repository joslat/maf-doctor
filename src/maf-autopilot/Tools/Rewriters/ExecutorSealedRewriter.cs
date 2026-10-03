using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MafDoctor.Tools.Rewriters;

/// <summary>
/// Rule: <c>MAF-AP-WF-001</c>.
///
/// Rewrite: ensure every class deriving from <c>Executor</c> with
/// <c>[MessageHandler]</c> methods carries the <c>partial</c> modifier, which the
/// workflow source generator requires (MAFGENWF003). It does NOT add <c>sealed</c>:
/// the generator does not require it (checked on Microsoft.Agents.AI.Workflows.Generators
/// 1.23), and sealing an executor that has subclasses would break the build (CS0509).
/// (The class keeps its historical name; it no longer seals anything.)
///
/// Matches the same predicate as <see cref="AntiPatternScannerTool"/>'s
/// MAF-AP-WF-001 rule: derives from <c>Executor</c> AND has at least one method
/// decorated with <c>[MessageHandler]</c>.
/// </summary>
internal sealed class ExecutorSealedRewriter : CSharpSyntaxRewriter, IRuleRewriter
{
    public string RuleId => "MAF-AP-WF-001";

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (!IsExecutorWithMessageHandler(node))
            return base.VisitClassDeclaration(node);

        // A static class can't derive from Executor; abstract executors need `partial`
        // like any other (parity with the WF-001 scanner).
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
            || node.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            return base.VisitClassDeclaration(node);

        // `partial` must be the last modifier, immediately before `class` (CS0267).
        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        if (node.Modifiers.Count == 0)
        {
            // `partial` becomes the first token after any attributes: carry the `class`
            // keyword's leading trivia (indentation, or a `///` doc when there are no
            // attributes) onto it, else the doc detaches (CS1587) and the line de-indents.
            partialToken = partialToken.WithLeadingTrivia(node.Keyword.LeadingTrivia);
            node = node.WithKeyword(node.Keyword.WithLeadingTrivia());
        }
        return node.WithModifiers(node.Modifiers.Add(partialToken));
    }

    private static bool IsExecutorWithMessageHandler(ClassDeclarationSyntax cls)
    {
        // Shared predicate with the WF-001 scanner rule — matches plain `Executor`,
        // generic `Executor<…>`, and qualified forms — so detector and rewriter stay
        // in lockstep (no "flagged as fixable but silently not rewritten" parity gap).
        var derivesFromExecutor = cls.BaseList?.Types
            .Any(t => AntiPatternScannerTool.IsExecutorBaseType(t.Type)) ?? false;
        if (!derivesFromExecutor) return false;

        return cls.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            m.AttributeLists.SelectMany(a => a.Attributes).Any(a =>
            {
                var n = a.Name.ToString();
                var s = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
                return s is "MessageHandler" or "MessageHandlerAttribute";
            }));
    }
}
