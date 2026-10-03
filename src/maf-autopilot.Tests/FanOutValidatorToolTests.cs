using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for FanOutValidatorTool — pure Roslyn syntax-tree walking.
/// Uses literal source strings so the tests run without any file I/O.
/// </summary>
public class FanOutValidatorToolTests
{
    [Fact]
    public void AnalyzeSource_VoidHandler_FlagsSilentStarvationRisk()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;

            public partial class MyExecutor
            {
                [MessageHandler]
                public void Handle(string input) { }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);

        var finding = Assert.Single(findings);
        Assert.Equal("Handle", finding.MethodName);
        Assert.Equal("void", finding.ReturnType);
        Assert.Equal(FanOutVerdict.SilentStarvationRisk, finding.Verdict);
    }

    [Fact]
    public void AnalyzeSource_NonGenericTaskHandler_FlagsSilentStarvationRisk()
    {
        var source = """
            using System.Threading.Tasks;

            public partial class MyExecutor
            {
                [MessageHandler]
                public async Task Handle(string input) { await Task.Yield(); }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);

        Assert.Equal(FanOutVerdict.SilentStarvationRisk, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_NonGenericValueTaskHandler_FlagsSilentStarvationRisk()
    {
        var source = """
            using System.Threading.Tasks;

            public partial class MyExecutor
            {
                [MessageHandler]
                public async ValueTask Handle(string input) { await Task.Yield(); }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.SilentStarvationRisk, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_NonGenericValueTask_ButEmitsViaSendMessageAsync_IsOk()
    {
        // The idiomatic non-fan-out-edge pattern: void/ValueTask return + explicit
        // context.SendMessageAsync. This produces a downstream message, so it is NOT
        // a silent-starvation dead-end (was the false-positive flood before body-awareness).
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI.Workflows;

            public sealed partial class MyExecutor
            {
                [MessageHandler]
                public async ValueTask Handle(string input, IWorkflowContext context)
                {
                    await context.SendMessageAsync(input.ToUpper());
                }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.Ok, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_VoidHandler_ButEmitsViaYieldOutputAsync_IsOk()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI.Workflows;

            public sealed partial class MyExecutor
            {
                [MessageHandler]
                public async ValueTask Handle(string input, IWorkflowContext context)
                {
                    await context.YieldOutputAsync($"Processed: {input}");
                }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.Ok, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_GenericSendMessageAsync_IsOk()
    {
        // context.SendMessageAsync<T>(x) — the invoked name is a GenericName; must still match.
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI.Workflows;

            public sealed partial class MyExecutor
            {
                [MessageHandler]
                public async ValueTask Handle(string input, IWorkflowContext context)
                {
                    await context.SendMessageAsync<string>(input);
                }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.Ok, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_NonGenericValueTask_DiscardsResult_StaysSilentStarvationRisk()
    {
        // The genuine dead-end (mirrors the sample investigators): computes a value,
        // emits nothing via the context, returns nothing. Must STILL be flagged.
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI.Workflows;

            public sealed partial class MyExecutor
            {
                [MessageHandler]
                public async ValueTask Handle(string input, IWorkflowContext context)
                {
                    await Task.Yield();
                    var finding = input.ToUpper();
                    _ = finding; // thrown away — nothing reaches the fan-in barrier
                }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.SilentStarvationRisk, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_GenericValueTaskHandler_IsOk()
    {
        var source = """
            using System.Threading.Tasks;

            public partial class MyExecutor
            {
                [MessageHandler]
                public async ValueTask<string> Handle(int input) { return "ok"; }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.Ok, Assert.Single(findings).Verdict);
    }

    // Task<T> fails the source generator (MAFGENWF002) on Workflows 1.3.0 and 1.23.0.
    [Fact]
    public void AnalyzeSource_GenericTaskHandler_IsLikelyInvalid()
    {
        var source = """
            using System.Threading.Tasks;

            public partial class MyExecutor
            {
                [MessageHandler]
                public async Task<MyMessage> Handle(int input) => new MyMessage();
            }

            public record MyMessage();
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.LikelyInvalid, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_MethodWithoutMessageHandler_IsNotReported()
    {
        var source = """
            public partial class MyExecutor
            {
                public void DoStuff(string input) { }
                public async Task<string> Compute() => "ok";
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Empty(findings);
    }

    [Fact]
    public void AnalyzeSource_FullyQualifiedAttribute_IsRecognised()
    {
        var source = """
            using System.Threading.Tasks;

            public partial class MyExecutor
            {
                [Microsoft.Agents.AI.MessageHandler]
                public void Handle(string input) { }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Equal(FanOutVerdict.SilentStarvationRisk, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void AnalyzeSource_AttributeWithSuffix_IsRecognised()
    {
        var source = """
            public partial class MyExecutor
            {
                [MessageHandlerAttribute]
                public void Handle(string input) { }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source);
        Assert.Single(findings);
    }

    [Fact]
    public void AnalyzeSource_MultipleHandlers_AreAllReported()
    {
        var source = """
            using System.Threading.Tasks;

            public partial class MultiExecutor
            {
                [MessageHandler] public void A(string s) { }
                [MessageHandler] public Task<int> B(string s) => Task.FromResult(0);
                [MessageHandler] public ValueTask C(string s) => default;
                public void NotAHandler(string s) { }
            }
            """;

        var findings = FanOutValidatorTool.AnalyzeSource(source).ToList();

        Assert.Equal(3, findings.Count);
        Assert.Equal(FanOutVerdict.SilentStarvationRisk, findings[0].Verdict); // void
        Assert.Equal(FanOutVerdict.LikelyInvalid, findings[1].Verdict);         // Task<int>: MAFGENWF002
        Assert.Equal(FanOutVerdict.SilentStarvationRisk, findings[2].Verdict); // ValueTask
    }

    [Theory]
    [InlineData("void", FanOutVerdict.SilentStarvationRisk)]
    [InlineData("Task", FanOutVerdict.SilentStarvationRisk)]
    [InlineData("ValueTask", FanOutVerdict.SilentStarvationRisk)]
    [InlineData("System.Threading.Tasks.Task", FanOutVerdict.SilentStarvationRisk)]
    // Checked by building and running a workflow on Microsoft.Agents.AI.Workflows 1.23.
    [InlineData("ValueTask<string>", FanOutVerdict.Ok)]
    [InlineData("int", FanOutVerdict.Ok)]                           // synchronous TResult: sent downstream
    [InlineData("MyMessage", FanOutVerdict.Ok)]
    [InlineData("Task<int>", FanOutVerdict.LikelyInvalid)]          // MAFGENWF002
    [InlineData("Task<MyMessage>", FanOutVerdict.LikelyInvalid)]
    [InlineData("IAsyncEnumerable<int>", FanOutVerdict.LikelyInvalid)] // iterator sent as one message: the run fails
    public void ClassifyReturnType_KnownPatterns(string returnType, FanOutVerdict expected)
    {
        Assert.Equal(expected, FanOutValidatorTool.ClassifyReturnType(returnType));
    }

    [Fact]
    public void Mcp_EmptyPath_ReturnsErrorMessage()
    {
        var tool = new FanOutValidatorTool();
        var result = tool.MafValidateFanOut("");
        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mcp_NonexistentPath_ReturnsErrorMessage()
    {
        var tool = new FanOutValidatorTool();
        var result = tool.MafValidateFanOut("/path/that/definitely/does/not/exist.cs");
        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
    }
}
