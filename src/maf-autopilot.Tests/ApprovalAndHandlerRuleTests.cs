using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Positive and negative fixtures for the rules added for ROADMAP F-01, each grounded in
/// the MAF 1.23 packages: MAF-AP-APPROVAL-001 (approval response without the session),
/// MAF-AP-WF-002 (two [MessageHandler]s for one message type), MAF-AP-WF-003
/// (async void [MessageHandler]).
/// </summary>
public class ApprovalAndHandlerRuleTests
{
    private static bool Fires(string source, string ruleId) =>
        AntiPatternScannerTool.ScanFile(source, "src/Agent.cs").Any(f => f.RuleId == ruleId);

    private const string ApprovalUsings = """
        using Microsoft.Agents.AI;
        using Microsoft.Extensions.AI;
        """;

    // -------------------------------------------------------------------------
    // MAF-AP-APPROVAL-001
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("await agent.RunAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(true)]));")]
    [InlineData("var reply = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);\nresponse = await agent.RunAsync(reply);")]
    [InlineData("var reply = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);\nresponse = await agent.RunAsync(reply, null);")]
    [InlineData("var reply = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);\nresponse = await agent.RunAsync(reply, session: null);")]
    [InlineData("var responses = new List<AIContent>();\nresponses.Add(request.CreateResponse(true));\nawait foreach (var update in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, responses))) { }")]
    [InlineData("await agent.RunAsync(new ChatMessage(ChatRole.User, [request.CreateAlwaysApproveToolResponse()]));")]
    public void Approval001_ResponseWithoutSession_Fires(string body)
    {
        var source = ApprovalUsings + $$"""

            public class Approvals
            {
                public async Task Answer(AIAgent agent, ToolApprovalRequestContent request, bool approved)
                {
                    AgentResponse response;
                    {{body}}
                }
            }
            """;
        Assert.True(Fires(source, "MAF-AP-APPROVAL-001"));
    }

    [Theory]
    [InlineData("var reply = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);\nresponse = await agent.RunAsync(reply, session);")]
    [InlineData("var reply = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);\nresponse = await agent.RunAsync(reply, session: session);")]
    [InlineData("var reply = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);\nresponse = await agent.RunAsync(message: reply, session: session);")]
    // Not an approval flow: a session-less run is a design choice, not this bug.
    [InlineData("response = await agent.RunAsync(\"What's the weather?\");")]
    // Azure Functions' HttpRequestData.CreateResponse is unrelated.
    [InlineData("var http = req.CreateResponse(HttpStatusCode.OK);\nresponse = await agent.RunAsync(\"hi\");")]
    public void Approval001_SessionPassedOrNoApproval_DoesNotFire(string body)
    {
        var source = ApprovalUsings + $$"""

            public class Approvals
            {
                public async Task Answer(AIAgent agent, AgentSession session, ToolApprovalRequestContent request, bool approved, HttpRequestData req)
                {
                    AgentResponse response;
                    {{body}}
                }
            }
            """;
        Assert.False(Fires(source, "MAF-AP-APPROVAL-001"));
    }

    [Fact]
    public void Approval001_FileWithoutAnAiStack_DoesNotFire()
    {
        const string source = """
            public class Bus
            {
                public async Task Send(IRunner runner, Request request) =>
                    await runner.RunAsync(request.CreateResponse(true));
            }
            """;
        Assert.False(Fires(source, "MAF-AP-APPROVAL-001"));
    }

    // -------------------------------------------------------------------------
    // MAF-AP-WF-002
    // -------------------------------------------------------------------------

    private static string Executor(string handlers) => $$"""
        using Microsoft.Agents.AI.Workflows;

        public sealed partial class Router() : Executor("router")
        {
            {{handlers}}
        }
        """;

    [Theory]
    [InlineData("string", "System.String")]
    [InlineData("string", "global::System.String")]
    [InlineData("List<int>", "System.Collections.Generic.List<System.Int32>")]
    [InlineData("Order", "Shop.Order")]
    [InlineData("string?", "string")]
    public void Wf002_SameMessageTypeTwice_Fires(string first, string second)
    {
        var source = Executor($$"""
            [MessageHandler]
            private ValueTask HandleAsync({{first}} message, IWorkflowContext context) => default;

            [MessageHandler]
            private ValueTask HandleAgainAsync({{second}} message, IWorkflowContext context, CancellationToken cancellationToken) => default;
            """);
        var finding = Assert.Single(AntiPatternScannerTool.ScanFile(source, "src/Router.cs"), f => f.RuleId == "MAF-AP-WF-002");
        Assert.Contains("HandleAgainAsync", finding.Match, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("string", "int")]
    [InlineData("int", "int?")]
    [InlineData("List<int>", "List<long>")]
    public void Wf002_DifferentMessageTypes_DoesNotFire(string first, string second)
    {
        var source = Executor($$"""
            [MessageHandler]
            private ValueTask HandleAsync({{first}} message, IWorkflowContext context) => default;

            [MessageHandler]
            private ValueTask HandleOtherAsync({{second}} message, IWorkflowContext context) => default;
            """);
        Assert.False(Fires(source, "MAF-AP-WF-002"));
    }

    [Fact]
    public void Wf002_OnlyOneMethodIsAHandler_DoesNotFire()
    {
        var source = Executor("""
            [MessageHandler]
            private ValueTask HandleAsync(string message, IWorkflowContext context) => default;

            private ValueTask LogAsync(string message, IWorkflowContext context) => default;
            """);
        Assert.False(Fires(source, "MAF-AP-WF-002"));
    }

    // -------------------------------------------------------------------------
    // MAF-AP-WF-004 (an undeclared SendMessageAsync fails the run; Workflows 1.23)
    // -------------------------------------------------------------------------

    [Fact]
    public void Wf004_UndeclaredSend_Fires()
    {
        var source = Executor("""
            [MessageHandler]
            private async ValueTask HandleAsync(string message, IWorkflowContext context) =>
                await context.SendMessageAsync(message.ToUpperInvariant());
            """);
        var finding = Assert.Single(AntiPatternScannerTool.ScanFile(source, "src/Router.cs"), f => f.RuleId == "MAF-AP-WF-004");
        Assert.Equal(AntiPatternSeverity.Error, finding.Severity);
    }

    [Theory]
    // Declared on the handler.
    [InlineData("[MessageHandler(Send = [typeof(string)])]\nprivate async ValueTask HandleAsync(string message, IWorkflowContext context) => await context.SendMessageAsync(message);")]
    // The handler returns its message instead of sending it.
    [InlineData("[MessageHandler]\nprivate ValueTask<string> HandleAsync(string message, IWorkflowContext context) => ValueTask.FromResult(message);")]
    // Sends nothing at all (a sink).
    [InlineData("[MessageHandler]\nprivate void Handle(string message, IWorkflowContext context) { }")]
    public void Wf004_DeclaredOrNoSend_DoesNotFire(string member)
    {
        Assert.False(Fires(Executor(member), "MAF-AP-WF-004"));
    }

    [Fact]
    public void Wf004_UndeclaredYield_Fires()
    {
        var source = Executor("""
            [MessageHandler(Send = [typeof(string)])]
            private async ValueTask HandleAsync(string message, IWorkflowContext context)
            {
                await context.SendMessageAsync(message);
                await context.YieldOutputAsync("done: " + message);
            }
            """);
        var finding = Assert.Single(AntiPatternScannerTool.ScanFile(source, "src/Router.cs"), f => f.RuleId == "MAF-AP-WF-004");
        Assert.Contains("YieldOutputAsync", finding.Match, StringComparison.Ordinal);
    }

    [Fact]
    public void Wf004_DeclaredSendAndYield_DoesNotFire()
    {
        var source = Executor("""
            [MessageHandler(Send = [typeof(string)], Yield = [typeof(string)])]
            private async ValueTask HandleAsync(string message, IWorkflowContext context)
            {
                await context.SendMessageAsync(message);
                await context.YieldOutputAsync("done: " + message);
            }
            """);
        Assert.False(Fires(source, "MAF-AP-WF-004"));
    }

    [Fact]
    public void Wf004_ClassLevelSendsMessage_DoesNotFire()
    {
        const string source = """
            using Microsoft.Agents.AI.Workflows;

            [SendsMessage(typeof(string))]
            public sealed partial class Router() : Executor("router")
            {
                [MessageHandler]
                private async ValueTask HandleAsync(string message, IWorkflowContext context) => await context.SendMessageAsync(message);
            }
            """;
        Assert.False(Fires(source, "MAF-AP-WF-004"));
    }

    // -------------------------------------------------------------------------
    // MAF-AP-WF-003
    // -------------------------------------------------------------------------

    [Fact]
    public void Wf003_AsyncVoidHandler_Fires()
    {
        var source = Executor("""
            [MessageHandler]
            private async void HandleAsync(string message, IWorkflowContext context)
            {
                await context.SendMessageAsync(message.ToUpperInvariant());
            }
            """);
        Assert.True(Fires(source, "MAF-AP-WF-003"));
    }

    [Theory]
    // A synchronous void handler is a supported shape.
    [InlineData("[MessageHandler]\nprivate void Handle(string message, IWorkflowContext context) { }")]
    [InlineData("[MessageHandler]\nprivate async ValueTask HandleAsync(string message, IWorkflowContext context) => await context.SendMessageAsync(message);")]
    // async void outside a handler (an event handler) is not this rule's business.
    [InlineData("private async void OnClick(object sender, EventArgs e) => await Task.Yield();")]
    public void Wf003_SupportedShapes_DoNotFire(string member)
    {
        Assert.False(Fires(Executor(member), "MAF-AP-WF-003"));
    }
}
