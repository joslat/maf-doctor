using MafDoctor.Scaffolding;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for the scaffolder + the critical dogfooding check: code we generate
/// must itself pass MafScanAntiPatterns and MafValidateFanOut. A scanner-flagging
/// template is a generator bug.
/// </summary>
public class AgentScaffolderTests
{
    // -------------------------------------------------------------------------
    // Agent scaffolding — shape
    // -------------------------------------------------------------------------

    [Fact]
    public void ScaffoldAgent_ProducesAgentAndTestFiles()
    {
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.RelativePath == "Agents/ChatBot.cs");
        Assert.Contains(files, f => f.RelativePath == "Tests/ChatBotTests.cs");
    }

    [Fact]
    public void ScaffoldAgent_AgentBodyContainsRequiredBestPracticeMarkers()
    {
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");
        var agent = files.Single(f => f.RelativePath == "Agents/ChatBot.cs").Content;

        Assert.Contains("UseOpenTelemetry", agent);             // observability wired
        Assert.Contains("MaxOutputTokens", agent);              // cost cap
        Assert.Contains("ChatOptions", agent);                  // Instructions inside ChatOptions
        Assert.Contains("ManagedIdentityCredential", agent);    // identity guidance present
        Assert.DoesNotContain("DefaultAzureCredential()", agent); // never the anti-pattern
        Assert.Contains("namespace MyApp.Agents;", agent);
        Assert.Contains("public sealed class ChatBot", agent);
    }

    [Fact]
    public void ScaffoldAgent_TestBodyContainsHermeticChatClient()
    {
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");
        var test = files.Single(f => f.RelativePath == "Tests/ChatBotTests.cs").Content;

        Assert.Contains("ScriptedChatClient", test);
        Assert.Contains("[Fact]", test);
        Assert.Contains("Assert.", test);
        // No real LLM calls — hermetic
        Assert.DoesNotContain("AzureOpenAIClient", test);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123name")]      // starts with digit
    [InlineData("My Bot")]        // space
    [InlineData("My-Bot")]        // hyphen
    [InlineData("My.Bot")]        // dot
    public void ScaffoldAgent_InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            AgentScaffolder.ScaffoldAgent(name, "MyApp"));
    }

    [Theory]
    [InlineData("ChatBot")]
    [InlineData("chatBot")]   // lowercase first — should be normalized
    [InlineData("Bot_v2")]
    public void ScaffoldAgent_ValidName_DoesNotThrow(string name)
    {
        var files = AgentScaffolder.ScaffoldAgent(name, "MyApp");
        Assert.NotEmpty(files);
    }

    // -------------------------------------------------------------------------
    // Executor scaffolding — shape
    // -------------------------------------------------------------------------

    [Fact]
    public void ScaffoldExecutor_ProducesExecutorAndTestFiles()
    {
        var files = AgentScaffolder.ScaffoldExecutor("FraudReviewer", "MyApp.Workflows");

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.RelativePath == "Workflows/FraudReviewerExecutor.cs");
        Assert.Contains(files, f => f.RelativePath == "Tests/FraudReviewerExecutorTests.cs");
    }

    [Fact]
    public void ScaffoldExecutor_RetainsExistingExecutorSuffix()
    {
        var files = AgentScaffolder.ScaffoldExecutor("FraudExecutor", "MyApp.Workflows");
        Assert.Contains(files, f => f.RelativePath == "Workflows/FraudExecutor.cs");
        Assert.DoesNotContain(files, f => f.RelativePath.Contains("ExecutorExecutor"));
    }

    [Fact]
    public void ScaffoldExecutor_BodyHasFanOutSafeReturnType()
    {
        var files = AgentScaffolder.ScaffoldExecutor("FraudReviewer", "MyApp.Workflows",
            inputType: "FraudCheck", outputType: "FraudReport");
        var executor = files.Single(f => f.RelativePath == "Workflows/FraudReviewerExecutor.cs").Content;

        Assert.Contains("[MessageHandler]", executor);
        Assert.Contains("ValueTask<FraudReport>", executor);     // fan-out-safe (the generator rejects Task<T>)
        Assert.Contains("sealed partial class", executor);       // correct 1.3.0 shape
        Assert.DoesNotContain("public void Handle", executor);   // never void
        Assert.DoesNotContain("public Task Handle", executor);   // never non-generic Task
    }

    // -------------------------------------------------------------------------
    // 🎯 The big dogfooding test — generated code passes our own scanners
    // -------------------------------------------------------------------------

    [Fact]
    public void GeneratedAgent_PassesAntiPatternScanner()
    {
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");
        var agentSource = files.Single(f => f.RelativePath == "Agents/ChatBot.cs").Content;

        var findings = AntiPatternScannerTool.ScanFile(agentSource, "src/Agents/ChatBot.cs");

        Assert.True(
            findings.Count == 0,
            $"Generated agent triggered {findings.Count} anti-patterns: " +
            string.Join("; ", findings.Select(f => $"{f.RuleId} at line {f.Line}: {f.Match}")));
    }

    [Fact]
    public void GeneratedExecutor_PassesFanOutValidator()
    {
        var files = AgentScaffolder.ScaffoldExecutor("FraudReviewer", "MyApp.Workflows",
            inputType: "FraudCheck", outputType: "FraudReport");
        var executorSource = files.Single(f => f.RelativePath == "Workflows/FraudReviewerExecutor.cs").Content;

        var findings = FanOutValidatorTool.AnalyzeSource(executorSource, "src/Workflows/FraudReviewerExecutor.cs");

        var risks = findings.Where(f => f.Verdict == FanOutVerdict.SilentStarvationRisk
                                     || f.Verdict == FanOutVerdict.LikelyInvalid).ToList();
        Assert.True(
            risks.Count == 0,
            $"Generated executor triggered fan-out risks: " +
            string.Join("; ", risks.Select(f => $"{f.MethodName} returns {f.ReturnType} ({f.Verdict})")));
    }

    [Fact]
    public void GeneratedExecutor_AntiPatternScanner_AlsoClean()
    {
        var files = AgentScaffolder.ScaffoldExecutor("FraudReviewer", "MyApp.Workflows");
        var executorSource = files.Single(f => f.RelativePath.Contains("FraudReviewerExecutor.cs")).Content;

        var findings = AntiPatternScannerTool.ScanFile(executorSource, "src/Workflows/FraudReviewerExecutor.cs");

        Assert.True(findings.Count == 0,
            $"Generated executor triggered {findings.Count} anti-patterns: " +
            string.Join("; ", findings.Select(f => $"{f.RuleId}")));
    }

    // -------------------------------------------------------------------------
    // Escape helper
    // -------------------------------------------------------------------------

    [Fact]
    public void EscapeForCSharp_HandlesQuotesAndNewlines()
    {
        var input = "You are \"helpful\". Be\nconcise.";
        var escaped = AgentScaffolder.EscapeForCSharp(input);
        Assert.Contains("\\\"helpful\\\"", escaped);
        Assert.Contains("\\n", escaped);
        Assert.DoesNotContain("\n", escaped);  // no literal newlines
    }
}
