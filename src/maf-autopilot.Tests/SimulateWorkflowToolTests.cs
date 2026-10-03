using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for the workflow topology analyzer. All tests use literal source strings —
/// the analyzer runs purely on syntax trees, no semantic model, no compilation.
/// </summary>
public class SimulateWorkflowToolTests
{
    // -------------------------------------------------------------------------
    // Executor discovery
    // -------------------------------------------------------------------------

    [Fact]
    public void Analyze_DiscoversExecutorsWithMessageHandlers()
    {
        const string source = """
            public partial class FraudExecutor
            {
                [MessageHandler] public ValueTask<FraudReport> Handle(FraudCheck c, IWorkflowContext ctx) => default;
            }
            public class NotAnExecutor { public void X() { } }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var executor = Assert.Single(topology.Executors);
        Assert.Equal("FraudExecutor", executor.Name);
        var handler = Assert.Single(executor.Handlers);
        Assert.Equal("Handle", handler.MethodName);
        Assert.Equal(HandlerVerdict.ProducesMessage, handler.Verdict);
        Assert.Equal(3, handler.Line); // REP-19: the verdict carries its location
    }

    // Checked by building and running workflows on Microsoft.Agents.AI.Workflows 1.23.
    [Theory]
    [InlineData("public string Handle(string m, IWorkflowContext c) => m;", HandlerVerdict.ProducesMessage)]           // synchronous T: sent
    [InlineData("public async ValueTask Handle(string m, IWorkflowContext c) => await c.SendMessageAsync(m);", HandlerVerdict.ProducesMessage)] // sends through the context
    [InlineData("public ValueTask Handle(string m, IWorkflowContext c) => default;", HandlerVerdict.ProducesNothing)]
    [InlineData("public Task<string> Handle(string m, IWorkflowContext c) => null!;", HandlerVerdict.UnusualReturnType)]    // MAFGENWF002
    [InlineData("public IAsyncEnumerable<string> Handle(string m, IWorkflowContext c) => null!;", HandlerVerdict.UnusualReturnType)] // fails the run
    public void Analyze_ClassifiesHandlersLikeTheValidator(string member, HandlerVerdict expected)
    {
        var source = $$"""
            public partial class E : Executor
            {
                [MessageHandler] {{member}}
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        Assert.Equal(expected, Assert.Single(Assert.Single(topology.Executors).Handlers).Verdict);
    }

    [Fact]
    public void Analyze_VoidHandler_FlagsProducesNothing()
    {
        const string source = """
            public partial class BadExecutor
            {
                [MessageHandler] public void Handle(string s) { }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var handler = Assert.Single(topology.Executors[0].Handlers);
        Assert.Equal(HandlerVerdict.ProducesNothing, handler.Verdict);
    }

    // -------------------------------------------------------------------------
    // Edge discovery
    // -------------------------------------------------------------------------

    [Fact]
    public void Analyze_DiscoversAddEdgeCalls()
    {
        const string source = """
            public partial class A { [MessageHandler] public ValueTask<string> Handle(string s) => null!; }
            public partial class B { [MessageHandler] public ValueTask<string> Handle(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    builder.AddEdge(A, B);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var edge = Assert.Single(topology.Edges);
        Assert.Equal(EdgeKind.Direct, edge.Kind);
        Assert.Equal("A", edge.Source);
        Assert.Equal("B", edge.Target);
        Assert.Equal(EdgeVerdict.Ok, edge.Verdict);
    }

    [Fact]
    public void Analyze_FanInBarrierWithVoidSource_FlagsWouldStarve()
    {
        const string source = """
            public partial class Producer { [MessageHandler] public ValueTask<string> Run(int x) => null!; }
            public partial class Silent   { [MessageHandler] public void Run(int x) { } }
            public partial class Sink     { [MessageHandler] public ValueTask<int> Collect(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    builder.AddFanInBarrierEdge(Producer, Silent, Sink);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var edge = Assert.Single(topology.Edges);
        Assert.Equal(EdgeKind.FanInBarrier, edge.Kind);
        Assert.Equal("Sink", edge.Target);
        Assert.Equal(EdgeVerdict.WouldStarve, edge.Verdict);
    }

    [Fact]
    public void Analyze_FanInBarrierAllProducing_VerdictOk()
    {
        const string source = """
            public partial class P1   { [MessageHandler] public ValueTask<string> Run(int x) => null!; }
            public partial class P2   { [MessageHandler] public ValueTask<string> Run(int x) => null!; }
            public partial class Sink { [MessageHandler] public ValueTask<int> Collect(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    builder.AddFanInBarrierEdge(P1, P2, Sink);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var edge = Assert.Single(topology.Edges);
        Assert.Equal(EdgeVerdict.Ok, edge.Verdict);
    }

    // -------------------------------------------------------------------------
    // 🎯 The "real code" tests — variable bindings, not type-name shortcuts.
    // These guard against the silent ✅ verdict failure mode Opus flagged.
    // -------------------------------------------------------------------------

    [Fact]
    public void Analyze_VariableBoundEdges_ResolvesViaTypeMap()
    {
        // This is what REAL workflow wiring looks like — instances bound to
        // variables, then passed to AddEdge by name. The previous PascalCase-only
        // regex would silently return zero edges here.
        const string source = """
            public partial class Reviewer  { [MessageHandler] public ValueTask<string> Run(int x) => null!; }
            public partial class Finalizer { [MessageHandler] public ValueTask<int>    Done(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    var reviewer  = new Reviewer();
                    var finalizer = new Finalizer();
                    builder.AddEdge(reviewer, finalizer);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var edge = Assert.Single(topology.Edges);
        Assert.Equal("Reviewer", edge.Source);
        Assert.Equal("Finalizer", edge.Target);
        Assert.Equal(EdgeVerdict.Ok, edge.Verdict);
    }

    [Fact]
    public void Analyze_ExplicitTypeDeclarations_AreResolved()
    {
        const string source = """
            public partial class Producer { [MessageHandler] public ValueTask<string> P(int x) => null!; }
            public partial class Consumer { [MessageHandler] public ValueTask<int>    C(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    Producer prod = new Producer();
                    Consumer cons = new Consumer();
                    builder.AddEdge(prod, cons);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });
        var edge = Assert.Single(topology.Edges);
        Assert.Equal("Producer", edge.Source);
        Assert.Equal("Consumer", edge.Target);
    }

    [Fact]
    public void Analyze_ParameterBoundEdges_AreResolved()
    {
        // Pattern: a wiring method receives executor instances as parameters.
        const string source = """
            public partial class Reviewer  { [MessageHandler] public ValueTask<string> Run(int x) => null!; }
            public partial class Finalizer { [MessageHandler] public ValueTask<int>    Done(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder, Reviewer reviewer, Finalizer finalizer)
                {
                    builder.AddEdge(reviewer, finalizer);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });
        Assert.Equal("Reviewer", topology.Edges[0].Source);
        Assert.Equal("Finalizer", topology.Edges[0].Target);
    }

    [Fact]
    public void Analyze_FanInBarrierWithRealisticBindings_FlagsWouldStarve()
    {
        // The flagship failure case: a variable-bound fan-in barrier where one
        // source returns `void`. Before the fix, this would silently pass.
        const string source = """
            public partial class Producer { [MessageHandler] public ValueTask<string> Run(int x) => null!; }
            public partial class Silent   { [MessageHandler] public void         Run(int x) { } }
            public partial class Sink     { [MessageHandler] public ValueTask<int>    Collect(string s) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    var producer = new Producer();
                    var silent   = new Silent();
                    var sink     = new Sink();
                    builder.AddFanInBarrierEdge(producer, silent, sink);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });

        var edge = Assert.Single(topology.Edges);
        Assert.Equal(EdgeKind.FanInBarrier, edge.Kind);
        Assert.Equal("Sink", edge.Target);
        Assert.Equal(EdgeVerdict.WouldStarve, edge.Verdict);
    }

    [Fact]
    public void Analyze_AddEdgeWithUnknownSource_FlagsUnresolved()
    {
        // GhostExecutor is referenced but never defined.
        const string source = """
            public partial class Real { [MessageHandler] public ValueTask<string> Run(int x) => null!; }

            public class Wiring
            {
                public void Wire(WorkflowBuilder builder)
                {
                    builder.AddEdge(GhostExecutor, Real);
                }
            }
            """;

        var topology = SimulateWorkflowTool.AnalyzeSources(new[] { ("file.cs", source) });
        Assert.Equal(EdgeVerdict.UnresolvedExecutor, topology.Edges[0].Verdict);
    }

    [Fact]
    public void MafSimulateWorkflow_EmptyPath_ReturnsError()
    {
        var tool = new SimulateWorkflowTool();
        Assert.Contains("Error", tool.MafSimulateWorkflow(""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MafSimulateWorkflow_NonexistentDirectory_ReturnsError()
    {
        var tool = new SimulateWorkflowTool();
        Assert.Contains("Error", tool.MafSimulateWorkflow("/path/that/does/not/exist"),
            StringComparison.OrdinalIgnoreCase);
    }
}
