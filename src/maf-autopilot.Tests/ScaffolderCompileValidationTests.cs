using MafDoctor.Scaffolding;
using MafDoctor.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Compile-validation tests for the scaffolder (Phase F.4 — Tests reviewer's
/// top recommendation). The dogfood pattern proves the generated source passes
/// our scanners; this test proves it actually COMPILES.
///
/// Approach: provide a minimal MAF surface stub (the types our templates
/// reference) and run <c>CSharpCompilation.Create</c> against scaffolder output +
/// stubs. The day a template references a type that doesn't exist in the stub
/// (e.g., MAF 1.4 renames `ChatClientAgentOptions`), the test fails loudly.
///
/// This is intentionally STRUCTURAL — it does NOT pull the real MAF NuGet to
/// keep test runs fast and hermetic. The stub captures the API shape we ship
/// templates against. To upgrade to real-NuGet validation, swap the stub for
/// PackageReference pins in the test csproj.
/// </summary>
public class ScaffolderCompileValidationTests
{
    /// <summary>
    /// Minimal stub of the MAF + Microsoft.Extensions.AI + Azure surface that
    /// the scaffolded templates reference. Update in lockstep with template
    /// changes — if a new template introduces a new type, add a stub here.
    /// </summary>
    private const string MafStubSource = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Microsoft.Extensions.AI
        {
            public interface IChatClient : System.IDisposable { }
            public class ChatOptions
            {
                public int? MaxOutputTokens { get; set; }
                public string? Instructions { get; set; }
            }
            public static class ChatClientBuilderExtensions
            {
                public static ChatClientBuilder AsBuilder(this IChatClient client) => new();
            }
            public class ChatClientBuilder
            {
                public ChatClientBuilder UseOpenTelemetry(string? sourceName = null) => this;
                public IChatClient Build() => null!;
            }
            // Types referenced by the generated test-stub's ScriptedChatClient.
            public enum ChatRole { Assistant, User, System, Tool }
            public class ChatMessage
            {
                public ChatMessage(ChatRole role, string text) { Role = role; Text = text; }
                public ChatRole Role { get; }
                public string Text { get; }
            }
            public class ChatResponse
            {
                public ChatResponse(ChatMessage message) { Message = message; }
                public ChatMessage Message { get; }
            }
            public class ChatResponseUpdate
            {
                public ChatResponseUpdate(ChatRole role, string text) { Role = role; Text = text; }
                public ChatRole Role { get; }
                public string Text { get; }
            }
        }

        namespace Microsoft.Agents.AI
        {
            using Microsoft.Extensions.AI;

            public class ChatClientAgentOptions
            {
                public string? Name { get; set; }
                public ChatOptions? ChatOptions { get; set; }
            }

            public class ChatClientAgent
            {
                public ChatClientAgent(IChatClient client) { }
                public ChatClientAgent(IChatClient client, ChatClientAgentOptions options) { }
                public Task<AgentResponse> RunAsync(string message, CancellationToken ct = default)
                    => Task.FromResult(new AgentResponse());
            }

            public class AgentResponse
            {
                public string Text { get; set; } = string.Empty;
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class MessageHandlerAttribute : System.Attribute { }
        }

        namespace Microsoft.Agents.AI.Workflows
        {
            // As in 1.23: no parameterless constructor; handlers take an IWorkflowContext.
            public abstract class Executor
            {
                protected Executor(string id) { }
            }

            public interface IWorkflowContext { }
        }

        namespace Azure.AI.OpenAI
        {
            public class AzureOpenAIClient { }
        }

        namespace Azure.Identity
        {
            public class ManagedIdentityCredential { }
        }
        """;

    private static readonly IReadOnlyList<MetadataReference> SystemRefs = BuildSystemRefs();

    private static IReadOnlyList<MetadataReference> BuildSystemRefs()
    {
        // Reference the netcoreapp runtime assemblies the test process is using.
        var trustedAssemblies = (string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    private static IReadOnlyList<Diagnostic> CompileWithStubs(IEnumerable<string> sources)
    {
        var allSources = sources.Append(MafStubSource);
        var trees = allSources.Select(s => CSharpSyntaxTree.ParseText(s)).ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"scaffolder-compile-test-{Guid.NewGuid():N}",
            syntaxTrees: trees,
            references: SystemRefs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: false,
                // We don't want minor warnings (like 'unused variable') to break the test —
                // only Errors mean the template ships uncompilable code.
                warningLevel: 0));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    [Fact]
    public void GeneratedAgent_CompilesAgainstMafStubs()
    {
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");
        var agentSource = files.Single(f => f.RelativePath == "Agents/ChatBot.cs").Content;

        var errors = CompileWithStubs(new[] { agentSource });

        Assert.True(errors.Count == 0,
            $"Generated agent failed to compile against MAF stubs:\n" +
            string.Join("\n", errors.Select(e => $"  {e.Id} {e.Location.GetLineSpan()}: {e.GetMessage()}")));
    }

    [Fact]
    public void GeneratedExecutor_CompilesAgainstMafStubs()
    {
        var files = AgentScaffolder.ScaffoldExecutor("FraudReviewer", "MyApp.Workflows",
            inputType: "string", outputType: "string");
        var executorSource = files.Single(f => f.RelativePath == "Workflows/FraudReviewerExecutor.cs").Content;

        var errors = CompileWithStubs(new[] { executorSource });

        Assert.True(errors.Count == 0,
            $"Generated executor failed to compile against MAF stubs:\n" +
            string.Join("\n", errors.Select(e => $"  {e.Id} {e.Location.GetLineSpan()}: {e.GetMessage()}")));
    }

    [Fact]
    public void GeneratedAgentTestStub_CompilesAgainstMafStubs()
    {
        // Phase H.3 — the test stub uses Task, IEnumerable, IAsyncEnumerable,
        // CancellationToken, StringComparison without explicit usings. ImplicitUsings
        // hides this in the test csproj; user projects with ImplicitUsings=disable
        // would fail to build. CompileWithStubs does NOT inject global usings,
        // so it reproduces the user-with-implicits-disabled scenario.
        // We compile the test stub ALONGSIDE the agent class it references.
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");
        var agentSource = files.Single(f => f.RelativePath == "Agents/ChatBot.cs").Content;
        var testSource = files.Single(f => f.RelativePath == "Tests/ChatBotTests.cs").Content;

        var errors = CompileWithStubs(new[] { agentSource, testSource });

        Assert.True(errors.Count == 0,
            $"Generated agent test stub failed to compile against MAF stubs (implicit-usings-disabled scenario):\n" +
            string.Join("\n", errors.Select(e => $"  {e.Id} {e.Location.GetLineSpan()}: {e.GetMessage()}")));
    }

    [Fact]
    public void GeneratedExecutorTestStub_CompilesAgainstMafStubs()
    {
        // Phase H.3 — same as above, applied to the executor test stub which uses
        // StringComparison.Ordinal from System. Compile the executor AND test together.
        var files = AgentScaffolder.ScaffoldExecutor("FraudReviewer", "MyApp.Workflows");
        var executorSource = files.Single(f => f.RelativePath == "Workflows/FraudReviewerExecutor.cs").Content;
        var testSource = files.Single(f => f.RelativePath == "Tests/FraudReviewerExecutorTests.cs").Content;

        var errors = CompileWithStubs(new[] { executorSource, testSource });

        Assert.True(errors.Count == 0,
            $"Generated executor test stub failed to compile against MAF stubs (implicit-usings-disabled scenario):\n" +
            string.Join("\n", errors.Select(e => $"  {e.Id} {e.Location.GetLineSpan()}: {e.GetMessage()}")));
    }

    // -------------------------------------------------------------------------
    // Phase I.3 — Self-consistency contract: the scaffolder's docstring claims
    // "generated code passes the anti-pattern scanner." If a future rule lands
    // that fires against our own templates, these tests catch it before alpha.
    // Pure in-memory — no disk, no shell-out.
    // -------------------------------------------------------------------------

    [Fact]
    public void ScaffoldedAgent_PassesAntiPatternScan_ZeroFindings()
    {
        // Arrange — scaffold a fresh agent with default instructions.
        var files = AgentScaffolder.ScaffoldAgent("ChatBot", "MyApp.Agents");
        var agentSource = files.Single(f => f.RelativePath.EndsWith("ChatBot.cs", StringComparison.Ordinal)).Content;

        // Act — scan the in-memory source string (pure, no disk).
        var findings = AntiPatternScannerTool.ScanFile(agentSource, "Agents/ChatBot.cs");

        // Assert — generator contract: every rule clean against generated code.
        Assert.True(findings.Count == 0,
            "Scaffolder output tripped the anti-pattern scanner — the docstring promise has regressed:\n" +
            string.Join("\n", findings.Select(f => $"  {f.RuleId} line {f.Line}: {f.RuleName}")));
    }

    [Fact]
    public void ScaffoldedExecutor_PassesAntiPatternScan_ZeroFindings()
    {
        // Arrange
        var files = AgentScaffolder.ScaffoldExecutor("Reviewer", "MyApp.Workflows");
        var executorSource = files.Single(f => f.RelativePath.EndsWith("ReviewerExecutor.cs", StringComparison.Ordinal)).Content;

        // Act
        var findings = AntiPatternScannerTool.ScanFile(executorSource, "Workflows/ReviewerExecutor.cs");

        // Assert
        Assert.True(findings.Count == 0,
            "Scaffolded executor tripped the anti-pattern scanner:\n" +
            string.Join("\n", findings.Select(f => $"  {f.RuleId} line {f.Line}: {f.RuleName}")));
    }

    [Fact]
    public void ScaffoldedExecutor_PassesFanOutValidator_NoStarvationRisk()
    {
        // Arrange — the generator's whole point is to produce a fan-out-safe handler.
        // If that ever regresses (e.g., return type changes), this catches it pre-ship.
        var files = AgentScaffolder.ScaffoldExecutor("Reviewer", "MyApp.Workflows",
            inputType: "string", outputType: "string");
        var executorSource = files.Single(f => f.RelativePath.EndsWith("ReviewerExecutor.cs", StringComparison.Ordinal)).Content;

        // Act
        var verdicts = FanOutValidatorTool.AnalyzeSource(executorSource, "Workflows/ReviewerExecutor.cs");

        // Assert — every handler must be Ok; never SilentStarvationRisk / LikelyInvalid.
        Assert.All(verdicts, h => Assert.Equal(FanOutVerdict.Ok, h.Verdict));
    }

    [Fact]
    public void GeneratedAgent_WithCustomInstructions_StillCompiles()
    {
        // Verify the EscapeForCSharp helper produces a valid string literal when the
        // instructions param contains characters that would break naive interpolation.
        var files = AgentScaffolder.ScaffoldAgent(
            "TrickyBot",
            "MyApp.Agents",
            instructions: "You are \"helpful\". Be\nconcise. Don't use backslashes like \\n.");
        var agentSource = files.Single(f => f.RelativePath.Contains("TrickyBot.cs")).Content;

        var errors = CompileWithStubs(new[] { agentSource });

        Assert.True(errors.Count == 0,
            $"Generated agent with special-char instructions failed to compile:\n" +
            string.Join("\n", errors.Select(e => $"  {e.Id}: {e.GetMessage()}")));
    }
}
