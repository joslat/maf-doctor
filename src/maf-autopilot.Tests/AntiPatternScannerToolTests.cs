using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for AntiPatternScannerTool. All tests use literal C# source strings
/// to exercise both the regex and Roslyn-based rules without touching the filesystem.
/// Rule IDs come from `.github/skills/maf-anti-pattern-scanner/SKILL.md`.
/// </summary>
public class AntiPatternScannerToolTests
{
    // -------------------------------------------------------------------------
    // MAF-AP-SEC-001 — DefaultAzureCredential in production
    // -------------------------------------------------------------------------

    [Fact]
    public void Sec001_OutsideAiCode_DoesNotFlag()
    {
        // Q-04: a non-MAF repo (CleanArchitecture) was graded "C" for a Key Vault
        // configuration credential. SEC-001 targets agent/AI client credentials.
        const string source = """
            using Azure.Identity;
            using Azure.Extensions.AspNetCore.Configuration.Secrets;
            public static class DependencyInjection
            {
                public static void AddKeyVault(ConfigurationManager config, Uri vault) =>
                    config.AddAzureKeyVault(vault, new DefaultAzureCredential());
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Web/DependencyInjection.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-SEC-001");
    }

    [Theory]
    [InlineData("using Microsoft.Agents.AI;")]
    [InlineData("using Microsoft.Extensions.AI;")]
    [InlineData("using Azure.AI.OpenAI;")]
    [InlineData("using OpenAI.Chat;")]
    public void Sec001_InAiCode_Flags(string aiUsing)
    {
        var source = aiUsing + "\nusing Azure.Identity;\npublic class Agents { public void Setup() { var cred = new DefaultAzureCredential(); } }";
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Agents.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-SEC-001");
    }

    [Fact]
    public void Sec001_DefaultAzureCredentialInProduction_Flags()
    {
        const string source = """
            using Azure.Identity; using Microsoft.Agents.AI;
            public class Auth
            {
                public void Setup() { var cred = new DefaultAzureCredential(); }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Auth.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-SEC-001" && f.Severity == AntiPatternSeverity.Error);
    }

    [Fact]
    public void Sec001_DefaultAzureCredentialInTestFile_SkipsRule()
    {
        const string source = """
            public class AuthTests { public void X() { var c = new DefaultAzureCredential(); } }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "tests/AuthTests.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-SEC-001");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-SEC-002 — hard-coded API key
    // -------------------------------------------------------------------------

    [Fact]
    public void Sec002_HardCodedApiKey_Flags()
    {
        const string source = """
            public class Config { public const string Key = "sk-abc123def456ghi789jkl"; }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Config.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-SEC-002");
    }

    [Fact]
    public void Sec002_ShortStringWithSkPrefix_DoesNotFlag()
    {
        const string source = """
            public class A { public const string Name = "sk-y"; } // too short to be a real key
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/A.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-SEC-002");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-SEC-003 — EnableSensitiveData = true
    // -------------------------------------------------------------------------

    [Fact]
    public void Sec003_EnableSensitiveData_Flags()
    {
        const string source = """
            public class Setup { public void X() { var o = new Options { EnableSensitiveData = true }; } }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-SEC-003");
    }

    [Fact]
    public void Sec003_EnableSensitiveDataFalse_DoesNotFlag()
    {
        const string source = """
            public class Setup { public void X() { var o = new Options { EnableSensitiveData = false }; } }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-SEC-003");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-CONC-001 — instance fields on AIContextProvider (Roslyn-based)
    // -------------------------------------------------------------------------

    [Fact]
    public void Conc001_AIContextProviderWithMutableField_Flags()
    {
        const string source = """
            public class MyProvider : AIContextProvider
            {
                private int _counter;   // mutable instance field — disallowed
                private readonly int _allowed = 0;
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/MyProvider.cs");
        Assert.Single(findings, f => f.RuleId == "MAF-AP-CONC-001");
    }

    [Fact]
    public void Conc001_AIContextProviderWithOnlyReadonlyFields_DoesNotFlag()
    {
        const string source = """
            public class MyProvider : AIContextProvider
            {
                private readonly int _x = 0;
                private const string Y = "hi";
                private static int _z;
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/MyProvider.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-CONC-001");
    }

    [Fact]
    public void Conc001_NonAIContextProviderClass_IsNotChecked()
    {
        const string source = """
            public class RegularClass
            {
                private int _counter;
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/RegularClass.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-CONC-001");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-OBS-001 — agent built without UseOpenTelemetry
    // -------------------------------------------------------------------------

    [Fact]
    public void Obs001_AgentBuiltWithoutOTel_Flags()
    {
        const string source = """
            public class AgentSetup
            {
                public void Build() { var agent = new AIAgentBuilder(client).Build(); }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/AgentSetup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-OBS-001");
    }

    [Fact]
    public void Obs001_AgentBuiltWithOTel_DoesNotFlag()
    {
        const string source = """
            public class AgentSetup
            {
                public void Build() { var agent = new AIAgentBuilder(client).UseOpenTelemetry(otel).Build(); }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/AgentSetup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-OBS-001");
    }

    [Fact]
    public void Obs001_BuilderMentionedOnlyInComment_DoesNotFlag()
    {
        // Round-8 regression: the builder type appears ONLY in a comment — no agent
        // is actually constructed. The node-based rule must not fire (comments are
        // trivia, never syntax nodes).
        const string source = """
            public class AgentSetup
            {
                // example: var a = new AIAgentBuilder(client).Build();
                public int Compute() => 1 + 2;
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/AgentSetup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-OBS-001");
    }

    [Fact]
    public void Obs001_BuilderMentionedOnlyInStringLiteral_DoesNotFlag()
    {
        // Round-8 regression: a string body that mentions AIAgentBuilder is data,
        // not code — a literal token, not an identifier node.
        const string source = """
            public class Docs
            {
                public string Help => "see the AIAgentBuilder docs for telemetry";
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Docs.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-OBS-001");
    }

    [Fact]
    public void Obs001_ChatClientAgentConstruction_Flags()
    {
        // The `new ChatClientAgent(...)` construction path (not just AIAgentBuilder)
        // must also fire when telemetry is absent.
        const string source = """
            public class AgentSetup
            {
                public void Build(object client, object opts)
                    => _ = new ChatClientAgent(client, opts);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/AgentSetup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-OBS-001");
    }

    [Fact]
    public void Obs001_TypeReferenceOnly_DoesNotFlag()
    {
        // A bare TYPE reference (parameter type, field type, nameof) is not building
        // an agent — only a `new AIAgentBuilder(...)` / `new ChatClientAgent(...)`
        // CONSTRUCTION should fire. The earlier identifier-match over-flagged these.
        const string source = """
            public class AgentSetup
            {
                private AIAgentBuilder _cached;
                public void Configure(AIAgentBuilder builder) { var n = nameof(AIAgentBuilder); }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/AgentSetup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-OBS-001");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-AGENT-001 — Instructions at top of ChatClientAgentOptions (silent ignore)
    // -------------------------------------------------------------------------

    [Fact]
    public void Agent001_InstructionsAtTopLevel_Flags()
    {
        const string source = """
            public class Setup
            {
                public void Build()
                {
                    var opts = new ChatClientAgentOptions
                    {
                        Name = "Bot",
                        Instructions = "You are helpful",
                    };
                }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-AGENT-001" && f.Severity == AntiPatternSeverity.Error);
    }

    [Fact]
    public void Agent001_InstructionsInsideChatOptions_DoesNotFlag()
    {
        const string source = """
            public class Setup
            {
                public void Build()
                {
                    var opts = new ChatClientAgentOptions
                    {
                        Name = "Bot",
                        ChatOptions = new ChatOptions { Instructions = "You are helpful" },
                    };
                }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-AGENT-001");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-WF-001 — Executor must be `sealed partial`
    // -------------------------------------------------------------------------

    [Fact]
    public void Wf001_NonPartialExecutor_Flags()
    {
        const string source = """
            public class FraudExecutor : Executor
            {
                [MessageHandler]
                public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/FraudExecutor.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-WF-001");
    }

    [Fact]
    public void Wf001_SealedPartialExecutor_DoesNotFlag()
    {
        const string source = """
            public sealed partial class FraudExecutor : Executor
            {
                [MessageHandler]
                public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/FraudExecutor.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-WF-001");
    }

    [Fact]
    public void Wf001_ExecutorWithoutMessageHandler_IsNotChecked()
    {
        // No [MessageHandler] → not really an MAF executor → don't fire the rule.
        const string source = """
            public class HelperExecutor : Executor
            {
                public Task<int> DoWork(string s) => Task.FromResult(0);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/HelperExecutor.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-WF-001");
    }

    [Fact]
    public void Wf001_GenericExecutorBase_Flags()
    {
        // Round-8 regression: a generic `Executor<…>` base was missed because the
        // old check compared the base-type STRING against "Executor"/".Executor".
        const string source = """
            public class FraudExecutor : Microsoft.Agents.AI.Workflows.Executor<string, int>
            {
                [MessageHandler]
                public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/FraudExecutor.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-WF-001");
    }

    [Fact]
    public void Wf001_StaticExecutor_NotFlagged_ParityWithRewriter()
    {
        // A static class can't derive from Executor (compile error) — not fixable, so
        // the scanner skips it, matching ExecutorSealedRewriter which leaves it
        // untouched (detector⇆rewriter parity; previously the scanner flagged it).
        const string source = """
            public static class HelperExecutor : Executor
            {
                [MessageHandler]
                public static System.Threading.Tasks.Task<int> Handle(string s) => System.Threading.Tasks.Task.FromResult(0);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/HelperExecutor.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-WF-001");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-MID-001 — Middleware Use() must provide both callbacks
    // -------------------------------------------------------------------------

    [Fact]
    public void Mid001_UseWithoutStreamingFunc_Flags()
    {
        const string source = """
            public class Setup
            {
                public void Build(IChatClient c)
                {
                    var client = c.AsBuilder()
                        .Use(runFunc: async (m, o, n, ct) => await n(m, o, ct))
                        .Build();
                }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-MID-001");
    }

    [Fact]
    public void Mid001_UseWithExplicitNullStreaming_Flags()
    {
        const string source = """
            public class Setup
            {
                public void Build(IChatClient c)
                {
                    var client = c.AsBuilder()
                        .Use(runFunc: async (m, o, n, ct) => await n(m, o, ct), runStreamingFunc: null)
                        .Build();
                }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-MID-001");
    }

    [Fact]
    public void Mid001_UseWithBothCallbacks_DoesNotFlag()
    {
        const string source = """
            public class Setup
            {
                public void Build(IChatClient c)
                {
                    var client = c.AsBuilder()
                        .Use(
                            runFunc: async (m, o, n, ct) => await n(m, o, ct),
                            runStreamingFunc: (m, o, n, ct) => n(m, o, ct))
                        .Build();
                }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-MID-001");
    }

    [Fact]
    public void Mid001_PositionalArgNamedRunFunc_DoesNotFlag()
    {
        // Round-8 regression: a positional local named `runFunc` (no `runFunc:` label)
        // must NOT fire — the old substring scan false-fired on the bare text.
        const string source = """
            public class Setup
            {
                public void Build(object pipeline, object runFunc) => pipeline.Use(runFunc);
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-MID-001");
    }

    [Fact]
    public void Mid001_SubstringInUnrelatedIdentifier_DoesNotFlag()
    {
        // Round-8 regression: an identifier that merely CONTAINS "runFunc"
        // (e.g. `computeMyrunFunc()`) must NOT fire.
        const string source = """
            public class Setup
            {
                object computeMyrunFunc() => null;
                public void Build(object obj) => obj.Use(computeMyrunFunc());
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-MID-001");
    }

    // -------------------------------------------------------------------------
    // MAF-AP-DEVUI-001 — DevUI references must be guarded
    // -------------------------------------------------------------------------

    [Fact]
    public void DevUi001_UnguardedUsing_Flags()
    {
        const string source = """
            using Microsoft.Agents.AI.DevUI;
            public class Setup
            {
                public void Start() { var ui = new DevUIServer(); }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-DEVUI-001");
    }

    [Fact]
    public void DevUi001_GuardedUsing_DoesNotFlag()
    {
        const string source = """
            #if DEVUI_ENABLED
            using Microsoft.Agents.AI.DevUI;
            #endif
            public class Setup
            {
            #if DEVUI_ENABLED
                public void Start() { var ui = new DevUIServer(); }
            #endif
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-DEVUI-001");
    }

    [Fact]
    public void DevUi001_QualifiedReferenceUnguarded_Flags()
    {
        const string source = """
            public class Setup
            {
                public void Start()
                {
                    var ui = new Microsoft.Agents.AI.DevUI.DevUIServer();
                }
            }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Setup.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-DEVUI-001");
    }

    // -------------------------------------------------------------------------
    // Test-file detection
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("src/MyService.cs", false)]
    [InlineData("tests/MyServiceTests.cs", true)]
    [InlineData("Tests/Something.cs", true)]
    [InlineData("samples/Sample.cs", true)]
    [InlineData("MyServiceTests.cs", true)]
    [InlineData("C:\\repo\\tests\\Foo.cs", true)]
    // Project-name conventions surfaced by the 2026-05-12 audit — common in real
    // codebases that don't use a top-level /tests/ folder.
    [InlineData("src/MyApp.UnitTests/AuthService.cs", true)]
    [InlineData("src/MyApp.IntegrationTests/Helpers/Builder.cs", true)]
    [InlineData("src/MyApp.E2E/Scenarios.cs", true)]
    [InlineData("unittests/Foo.cs", true)]
    [InlineData("src/MyApp/Fixtures.cs", true)]
    [InlineData("src/MyApp/Service.cs", false)]   // ordinary production file — must NOT match
    public void IsTestFile_KnownPaths(string path, bool expected) =>
        Assert.Equal(expected, AntiPatternScannerTool.IsTestFile(path));

    // -------------------------------------------------------------------------
    // Empty / clean code
    // -------------------------------------------------------------------------

    [Fact]
    public void CleanCode_ProducesNoFindings()
    {
        const string source = """
            public class Foo { public int Bar() => 42; }
            """;
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Foo.cs");
        Assert.Empty(findings);
    }

    // -------------------------------------------------------------------------
    // MCP entry point
    // -------------------------------------------------------------------------

    [Fact]
    public void Mcp_EmptyPath_ReturnsErrorMessage()
    {
        var tool = new AntiPatternScannerTool();
        Assert.Contains("Error", tool.MafScanAntiPatterns(""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mcp_NonexistentDirectory_ReturnsErrorMessage()
    {
        var tool = new AntiPatternScannerTool();
        Assert.Contains("Error", tool.MafScanAntiPatterns("/path/that/definitely/does/not/exist"), StringComparison.OrdinalIgnoreCase);
    }
}
