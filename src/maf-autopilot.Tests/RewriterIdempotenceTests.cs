using MafDoctor.Tools.Rewriters;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Idempotence property tests for every <c>IRuleRewriter</c>: applying a
/// rewriter twice must produce the same output as applying it once. A failure
/// here means a rewriter is non-idempotent — for example because it accidentally
/// matches its own output and rewrites again, or because trivia handling drifts
/// between passes.
///
/// Idempotence is a load-bearing invariant for <c>MafAutoFix</c> and
/// <c>MafAutoFixAll</c>: the developer-facing contract is that running the
/// fix twice is a no-op on the second run. Without these tests, regressions
/// would only surface in production via "the diff is non-empty after running
/// the fix" — far too late.
///
/// Phase 4.3 of the v1.1 security-hardening release.
/// </summary>
public sealed class RewriterIdempotenceTests
{
    private static string ApplyTwice(IRuleRewriter rewriter, string source)
    {
        var pass1 = rewriter.Visit(CSharpSyntaxTree.ParseText(source).GetRoot()).ToFullString();
        var pass2 = rewriter.Visit(CSharpSyntaxTree.ParseText(pass1).GetRoot()).ToFullString();
        Assert.Equal(pass1, pass2);
        return pass1;
    }

    // -------------------------------------------------------------------------
    // DefaultAzureCredentialRewriter
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("var c = new DefaultAzureCredential();")]
    [InlineData("var c = new DefaultAzureCredential(new DefaultAzureCredentialOptions());")]
    [InlineData("class C { void M() { var x = new DefaultAzureCredential(); } }")]
    [InlineData("// nothing to do here\nclass C { }")]
    public void DefaultAzureCredentialRewriter_Idempotent(string source)
    {
        ApplyTwice(new DefaultAzureCredentialRewriter(), source);
    }

    // -------------------------------------------------------------------------
    // EnableSensitiveDataRewriter
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("var opt = new ChatOptions { EnableSensitiveData = true };")]
    [InlineData("var opt = new Dictionary<string, object> { [\"EnableSensitiveData\"] = true };")]
    [InlineData("class C { void M() { var x = 1; } }")]
    public void EnableSensitiveDataRewriter_Idempotent(string source)
    {
        ApplyTwice(new EnableSensitiveDataRewriter(), source);
    }

    // -------------------------------------------------------------------------
    // ExecutorSealedRewriter (Phase 4.1a guards)
    // -------------------------------------------------------------------------

    [Fact]
    public void ExecutorSealedRewriter_Idempotent_AddsPartialOnce()
    {
        const string src = """
            using Microsoft.Agents.AI.Workflow;
            public class FraudAuditor : Executor
            {
                [MessageHandler]
                public System.Threading.Tasks.Task<string> Audit(string input) => null!;
            }
            """;
        var output = ApplyTwice(new ExecutorSealedRewriter(), src);
        Assert.Contains("public partial class FraudAuditor", output);
        Assert.Single(output.Split("partial").Skip(1).ToArray()); // added once, not twice
        Assert.DoesNotContain("sealed", output);
    }

    [Fact]
    public void ExecutorSealedRewriter_AbstractClass_GetsPartialOnce_Idempotent()
    {
        // The generator requires `partial` on an abstract Executor too; `sealed` is never
        // added (`abstract sealed` would not compile). Two passes add `partial` once.
        const string src = """
            using Microsoft.Agents.AI.Workflow;
            public abstract class BaseAuditor : Executor
            {
                [MessageHandler]
                public abstract System.Threading.Tasks.Task<string> Audit(string input);
            }
            """;
        var output = ApplyTwice(new ExecutorSealedRewriter(), src);
        Assert.DoesNotContain("sealed", output);
        Assert.Equal(src.Replace("public abstract class", "public abstract partial class"), output.TrimEnd());
    }

    [Fact]
    public void ExecutorSealedRewriter_TwoAbstractExecutors_BothGetPartial()
    {
        // Two abstract Executor-derived classes in one source — both get `partial`, once.
        const string src = """
            using Microsoft.Agents.AI.Workflow;
            public abstract class FirstAuditor : Executor
            {
                [MessageHandler]
                public abstract System.Threading.Tasks.Task<string> Audit(string input);
            }
            public abstract class SecondAuditor : Executor
            {
                [MessageHandler]
                public abstract System.Threading.Tasks.Task<string> Audit(string input);
            }
            """;
        var output = ApplyTwice(new ExecutorSealedRewriter(), src);
        Assert.Equal(src.Replace("public abstract class", "public abstract partial class"), output.TrimEnd());
    }

    [Fact]
    public void ExecutorSealedRewriter_Idempotent_AlreadySealed()
    {
        const string src = """
            using Microsoft.Agents.AI.Workflow;
            public sealed partial class FraudAuditor : Executor
            {
                [MessageHandler]
                public System.Threading.Tasks.Task<string> Audit(string input) => null!;
            }
            """;
        var output = ApplyTwice(new ExecutorSealedRewriter(), src);
        // Only one `sealed` — no duplication.
        Assert.Single(output.Split("sealed").Skip(1).ToArray());
    }

    // -------------------------------------------------------------------------
    // FanInArgOrderRewriter
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("builder.AddFanInBarrierEdge(target, new[] { src1, src2 });")]
    [InlineData("builder.AddFanInBarrierEdge(sources: new[] { src1 }, target: t);")]
    [InlineData("var x = 1;")]
    public void FanInArgOrderRewriter_Idempotent(string source)
    {
        ApplyTwice(new FanInArgOrderRewriter(), source);
    }

    // -------------------------------------------------------------------------
    // SyncOverAsyncRewriter (Phase 4.1b guards)
    // -------------------------------------------------------------------------

    [Fact]
    public void SyncOverAsyncRewriter_Idempotent_AsyncMethod_HappyPath()
    {
        // After the rewrite produces `await FooAsync()`, a second pass should NOT
        // re-rewrite (`.Result` is gone; nothing matches). Method named `*Async` so
        // the syntax-only awaitability gate (LooksAwaitableReceiver) rewrites it.
        const string src = """
            class C
            {
                async System.Threading.Tasks.Task M()
                {
                    var x = FooAsync().Result;
                }
                System.Threading.Tasks.Task<int> FooAsync() => null!;
            }
            """;
        var output = ApplyTwice(new SyncOverAsyncRewriter(), src);
        Assert.Contains("await FooAsync()", output);
        Assert.DoesNotContain(".Result", output);
    }

    [Fact]
    public void SyncOverAsyncRewriter_Idempotent_InsideLock_SkipPath()
    {
        // The lock guard skips with a TODO comment. Running twice must not
        // duplicate the comment infinitely — the rewriter's behavior should
        // converge after pass 1 (no further match after pass 1 produces the
        // comment + leaves `.Result` intact).
        const string src = """
            class C
            {
                readonly object _lock = new();
                async System.Threading.Tasks.Task M()
                {
                    lock (_lock)
                    {
                        var x = Foo().Result;
                    }
                }
                System.Threading.Tasks.Task<int> Foo() => null!;
            }
            """;
        ApplyTwice(new SyncOverAsyncRewriter(), src);
    }

    [Fact]
    public void SyncOverAsyncRewriter_Idempotent_WaitCall()
    {
        const string src = """
            class C
            {
                async System.Threading.Tasks.Task M()
                {
                    FooAsync().Wait();
                }
                System.Threading.Tasks.Task FooAsync() => null!;
            }
            """;
        var output = ApplyTwice(new SyncOverAsyncRewriter(), src);
        Assert.Contains("await FooAsync()", output);
    }
}
