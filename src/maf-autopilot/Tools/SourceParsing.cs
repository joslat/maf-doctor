using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MafDoctor.Tools;

/// <summary>
/// The one place that parses user C# for every scanner and rewriter (R-03).
/// Parsing at <see cref="LanguageVersion.Latest"/> on Roslyn 5.x accepts the
/// C# 14 syntax a .NET 10 codebase uses (extension blocks, null-conditional
/// assignment, the <c>field</c> keyword) instead of producing error nodes that
/// can hide the surrounding MAF calls from the rules.
/// </summary>
internal static class SourceParsing
{
    internal static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    internal static SyntaxTree Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, Options);
}
