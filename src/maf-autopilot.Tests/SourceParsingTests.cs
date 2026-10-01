using MafDoctor.Tools;
using Microsoft.CodeAnalysis;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// R-03: the scanners parse with Roslyn 5.x at the latest language version, so
/// C# 14 code (what .NET 10 projects use) produces no syntax errors and the
/// MAF rules still see the calls around the new syntax.
/// </summary>
public class SourceParsingTests
{
    private const string CSharp14 = """
        using Azure.Identity; using Microsoft.Agents.AI;

        public static class TextExtensions
        {
            extension(string text)
            {
                public bool IsBlank => string.IsNullOrWhiteSpace(text);
            }
        }

        public class Settings
        {
            public string Name { get => field; set => field = value.Trim(); }
            public Settings? Child { get; set; }

            public void Wire(Settings other)
            {
                other.Child?.Name = "child";
                var cred = new DefaultAzureCredential();
            }
        }
        """;

    [Fact]
    public void CSharp14Syntax_ParsesWithoutErrors()
    {
        var diagnostics = SourceParsing.Parse(CSharp14).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void RulesStillFireNextToCSharp14Syntax()
    {
        var findings = AntiPatternScannerTool.ScanFile(CSharp14, "src/Settings.cs");

        Assert.Contains(findings, f => f.RuleId == "MAF-AP-SEC-001");
    }
}
