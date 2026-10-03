using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MafDoctor.Analyzers;

/// <summary>
/// MAF003 — EnableSensitiveData = true outside of test code.
///
/// Sensitive-data telemetry in production sends PII, prompts, and tool arguments
/// to whatever trace exporter is wired. Allowed in tests/samples; not in production.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SensitiveDataAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "MAF003";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "EnableSensitiveData = true outside test code",
        messageFormat: "EnableSensitiveData = true records prompts, tool arguments, and message contents in telemetry (traces and their exporters). Gate behind `#if DEBUG` or `builder.Environment.IsDevelopment()` before shipping.",
        category: "MAF.Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Sensitive-data telemetry is intended for development diagnostics only. It is off by default (unless OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true); production telemetry should keep it off.",
        // Anchor fragments are slugified by GitHub from the full heading text. Link
        // at the document root; the rule ID is grep-able inside the page.
        helpLinkUri: "https://github.com/joslat/maf-doctor/blob/main/.github/skills/maf-anti-pattern-scanner/SKILL.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        // Phase 4.5 — analyze generated code too; consistent with MAF002 and
        // MAF001. Generated code can still contain `EnableSensitiveData = true`
        // (e.g. via a T4 template) and the rule's risk applies regardless of
        // who emitted the line. ReportDiagnostics so the warning surfaces in
        // the IDE / build output.
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var node = (AssignmentExpressionSyntax)context.Node;

        // Three shapes to catch:
        //   1. `<something>.EnableSensitiveData = true`              (member access)
        //   2. `EnableSensitiveData = true`                          (object initializer)
        //   3. `["EnableSensitiveData"] = true` / `dict["EnableSensitiveData"] = true`
        //      (dictionary-style, ImplicitElementAccess or ElementAccess)
        //
        // Phase 4.4: the rewriter (EnableSensitiveDataRewriter) already
        // handles all three. The analyzer was missing the dictionary form;
        // that asymmetry meant the IDE didn't warn even though `MafAutoFix`
        // would silently remove the entry on build. Now they're in lockstep.
        if (!IsEnableSensitiveDataAssignment(node.Left)) return;

        if (node.Right is not LiteralExpressionSyntax literal) return;
        if (literal.Kind() != SyntaxKind.TrueLiteralExpression) return;

        var filePath = context.Node.SyntaxTree.FilePath ?? string.Empty;
        if (IsTestFile(filePath)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation()));
    }

    /// <summary>
    /// True if the left-hand side of an assignment targets the
    /// <c>EnableSensitiveData</c> slot in any of the three supported shapes.
    /// </summary>
    private static bool IsEnableSensitiveDataAssignment(ExpressionSyntax left)
    {
        switch (left)
        {
            case IdentifierNameSyntax id:
                // Bare object-initializer entry: `EnableSensitiveData = true`.
                return id.Identifier.ValueText == "EnableSensitiveData";

            case MemberAccessExpressionSyntax mae:
                // Qualified form: `opts.EnableSensitiveData = true` /
                // `_options.EnableSensitiveData = true`.
                return mae.Name.Identifier.ValueText == "EnableSensitiveData";

            case ImplicitElementAccessSyntax iea:
                // Object-initializer dictionary form:
                //   new Dictionary<string,object> { ["EnableSensitiveData"] = true }
                return IsLiteralStringKey(iea.ArgumentList, "EnableSensitiveData");

            case ElementAccessExpressionSyntax eae:
                // Standalone dictionary assignment:
                //   dict["EnableSensitiveData"] = true
                return IsLiteralStringKey(eae.ArgumentList, "EnableSensitiveData");

            default:
                return false;
        }
    }

    private static bool IsLiteralStringKey(BracketedArgumentListSyntax args, string expected)
    {
        if (args.Arguments.Count != 1) return false;
        if (args.Arguments[0].Expression is not LiteralExpressionSyntax lit) return false;
        if (lit.Kind() != SyntaxKind.StringLiteralExpression) return false;
        return lit.Token.ValueText == expected;
    }

    private static bool IsTestFile(string filePath) => TestFileHeuristic.IsTestFile(filePath);
}
