using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using MafDoctor.Analyzers;
using Xunit;

using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<
    MafDoctor.Analyzers.FanOutHandlerAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace MafDoctor.Analyzers.Tests;

public class FanOutHandlerAnalyzerTests
{
    /// <summary>
    /// Appended to every test source: stub the [MessageHandler] attribute so
    /// the analyzer can detect it without requiring the real MAF NuGet.
    /// </summary>
    private const string MessageHandlerStub = """

        namespace Microsoft.Agents.AI
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class MessageHandlerAttribute : System.Attribute { }
        }
        """;

    [Fact]
    public async Task VoidHandler_ReportsMAF001()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;
            public partial class Exec
            {
                [MessageHandler]
                public {|MAF001:void|} Handle(string s) { }
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task NonGenericTaskHandler_ReportsMAF001()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;
            public partial class Exec
            {
                [MessageHandler]
                public async {|MAF001:Task|} Handle(string s) { await Task.Yield(); }
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task NonGenericValueTaskHandler_ReportsMAF001()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;
            public partial class Exec
            {
                [MessageHandler]
                public async {|MAF001:ValueTask|} Handle(string s) { await Task.Yield(); }
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task GenericTaskHandler_NoReport()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;
            public partial class Exec
            {
                [MessageHandler]
                public Task<string> Handle(string s) => Task.FromResult(s);
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task GenericValueTaskHandler_NoReport()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;
            public partial class Exec
            {
                [MessageHandler]
                public ValueTask<int> Handle(string s) => new ValueTask<int>(0);
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task SynchronousResultHandler_NoReport()
    {
        // `TResult Handler(TMessage, IWorkflowContext)` is a documented shape; on
        // Workflows 1.23 the returned value is sent downstream like ValueTask<T>'s.
        var source = """
            using Microsoft.Agents.AI;
            public partial class Exec
            {
                [MessageHandler]
                public string Handle(string s) => s + "!";
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task VoidHandlerThatSendsThroughTheContext_NoReport()
    {
        // The documented void / ValueTask pattern: emit with context.SendMessageAsync.
        var source = """
            using System.Threading.Tasks;
            using Microsoft.Agents.AI;
            public interface IWorkflowContext { ValueTask SendMessageAsync(object message); }
            public partial class Exec
            {
                [MessageHandler]
                public async ValueTask Handle(string s, IWorkflowContext context) => await context.SendMessageAsync(s);
            }
            """ + MessageHandlerStub;

        await new Verify { TestCode = source }.RunAsync();
    }

    [Fact]
    public async Task MethodWithoutAttribute_NoReport()
    {
        var source = """
            public class Helper
            {
                public void DoStuff(string s) { }
            }
            """;

        await new Verify { TestCode = source }.RunAsync();
    }
}
