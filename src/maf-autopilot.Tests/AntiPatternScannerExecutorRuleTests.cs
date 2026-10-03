using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for the `MAF-AP-EXEC-001` anti-pattern rule — detects pre-1.3.0 executor
/// patterns (`ReflectingExecutor<T>`, `IMessageHandler<TIn,TOut>`, `[StreamsMessage]`,
/// `[YieldsMessage]`). Salvaged from the May-6 sketch (tag: pre-phase-o-may6-sketch).
///
/// Distinct from the existing `MAF-AP-WF-001` (which asserts the new pattern is
/// `sealed partial class : Executor`) — `MAF-AP-EXEC-001` flags the OLD pattern's
/// presence directly. Both rules can fire on the same file: one for "new pattern
/// missing," one for "old pattern present."
/// </summary>
public sealed class AntiPatternScannerExecutorRuleTests
{
    [Theory]
    // ReflectingExecutor<> is a MAF-specific name (low collision) → flagged always.
    [InlineData("public class MyExec : ReflectingExecutor<string> { }")]
    // [StreamsMessage] / [YieldsMessage] are obsolete MAF attributes → flagged always.
    [InlineData("[StreamsMessage] public void Foo() { }")]
    [InlineData("[YieldsMessage] public void Bar() { }")]
    public void Scan_LegacyExecutorPattern_FiresExecRule(string snippet)
    {
        // Arrange
        var source = $$"""
            using System;
            namespace Demo;
            {{snippet}}
            """;

        // Act
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Foo.cs");

        // Assert
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-EXEC-001");
        var hit = findings.First(f => f.RuleId == "MAF-AP-EXEC-001");
        // Obsolete (CS0618) in Workflows 1.23, not removed: a warning, not a build break.
        Assert.Equal(AntiPatternSeverity.Warning, hit.Severity);
        Assert.Contains("Obsolete", hit.RuleName);
    }

    [Fact]
    public void Scan_LegacyIMessageHandler_WithWorkflowsUsing_Fires()
    {
        // The legacy MAF IMessageHandler<TIn,TOut> in a file that imports the MAF
        // Workflows namespace IS the real pre-1.3.0 surface → fires.
        const string source = """
            using Microsoft.Agents.AI.Workflows;
            namespace Demo;
            public class MyExec : IMessageHandler<string, string> { }
            """;

        var findings = AntiPatternScannerTool.ScanFile(source, "src/Foo.cs");
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-EXEC-001");
    }

    [Fact]
    public void Scan_UnrelatedIMessageHandler_NoWorkflowsUsing_DoesNotFire()
    {
        // False-positive guard: `IMessageHandler<T>` is the most common message-bus
        // interface name (MediatR / NServiceBus / hand-rolled). Without the MAF
        // Workflows import it must NOT be flagged as a pre-1.3.0 executor pattern.
        const string source = """
            namespace MyApp.Messaging;
            public interface IMessageHandler<TMessage>
            {
                System.Threading.Tasks.Task HandleAsync(TMessage message);
            }
            public sealed class OrderPlacedHandler : IMessageHandler<string>
            {
                public System.Threading.Tasks.Task HandleAsync(string message)
                    => System.Threading.Tasks.Task.CompletedTask;
            }
            """;

        var findings = AntiPatternScannerTool.ScanFile(source, "src/Handlers.cs");
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-EXEC-001");
    }

    [Fact]
    public void Scan_CleanMaf130ExecutorPattern_DoesNotFireExecRule()
    {
        // Arrange — the canonical 1.3.0 shape that scaffolders emit.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Agents.AI.Workflows;

            namespace Demo;
            public sealed partial class MyExecutor : Executor
            {
                [MessageHandler]
                public Task<string> Handle(string input, CancellationToken ct) => Task.FromResult(input);
            }
            """;

        // Act
        var findings = AntiPatternScannerTool.ScanFile(source, "src/MyExecutor.cs");

        // Assert
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-EXEC-001");
    }

    [Fact]
    public void Scan_LegacyAndModernPatternsCoexist_LegacyRuleStillFires()
    {
        // Arrange — mid-migration shape: old class still present alongside new class.
        const string source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI.Workflows;

            namespace Demo;
            public class Legacy : ReflectingExecutor<string> { }
            public sealed partial class Modern : Executor
            {
                [MessageHandler]
                public Task<string> Handle(string s) => Task.FromResult(s);
            }
            """;

        // Act
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Mixed.cs");

        // Assert — EXEC-001 fires for the legacy class.
        Assert.Contains(findings, f => f.RuleId == "MAF-AP-EXEC-001");
    }

    [Fact]
    public void Scan_NonMafCode_NoExecRuleFires()
    {
        // Arrange — pure C# class with nothing MAF-shaped.
        const string source = """
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        // Act
        var findings = AntiPatternScannerTool.ScanFile(source, "src/Calculator.cs");

        // Assert
        Assert.DoesNotContain(findings, f => f.RuleId == "MAF-AP-EXEC-001");
    }
}
