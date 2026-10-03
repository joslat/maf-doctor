using System.Collections.Immutable;
using MafDoctor.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Compile-roundtrip safety net for EVERY <c>IRuleRewriter</c>.
///
/// For a corpus of VALID (clean-compiling) C# fixtures, running a rewriter must
/// NOT introduce a new compiler ERROR. Two real source-corruption bugs —
/// <c>CS1995</c> (await injected into a nested-query clause) and <c>CS4004</c>
/// (await injected into an <c>unsafe</c> member) — shipped past the string-assertion
/// unit tests and were caught ONLY by compiling the rewritten output. This test
/// makes that check permanent: any future rewriter change that injects an
/// <c>await</c> (or otherwise breaks compilation) into a context the C# spec
/// forbids fails CI immediately, instead of silently corrupting user source.
///
/// Scope note: the corpus contains only VALID inputs — that is the whole point
/// (a rewriter must never turn compilable code into uncompilable code). Cases
/// where the INPUT is already uncompilable (e.g. a <c>.Result</c> in a parameter
/// default) cannot occur in real code and are covered by the rewriter's own unit
/// tests, not here. Several rule IDs share one rewriter; rewriters that don't
/// match a given fixture are no-ops and are skipped (no compile attempt).
/// </summary>
public class RewriterCompileRoundtripTests
{
    // Full framework reference set so fixtures using Task / LINQ / unsafe compile.
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    private static readonly CSharpParseOptions ParseOpts = new(LanguageVersion.Latest);

    private static readonly CSharpCompilationOptions CompileOpts =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

    private static ImmutableArray<Diagnostic> CompileErrors(SyntaxNode root)
    {
        var comp = CSharpCompilation.Create(
            "roundtrip",
            new[] { root.SyntaxTree },
            References,
            CompileOpts);
        return comp.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Rewriter_NeverIntroducesACompilerError(string label, string source)
    {
        var inputTree = CSharpSyntaxTree.ParseText(source, ParseOpts);
        var inputRoot = inputTree.GetRoot();
        var inputErrors = CompileErrors(inputRoot);

        // The fixture itself must be valid C# — otherwise the test is meaningless
        // (a rewriter can only be blamed for breaking code that compiled to begin with).
        Assert.True(inputErrors.IsEmpty,
            $"[{label}] FIXTURE does not compile clean (fix the test, not the rewriter): " +
            string.Join("; ", inputErrors.Select(d => $"{d.Id} {d.GetMessage()}")));

        foreach (var ruleId in AutoFixTool.SupportedRuleIds)
        {
            Assert.True(AutoFixTool.TryCreateRewriter(ruleId, out var rewriter) && rewriter is not null,
                $"could not create rewriter for {ruleId}");

            var newRoot = rewriter!.Visit(inputRoot);
            if (ReferenceEquals(newRoot, inputRoot)) continue; // rewriter didn't match this fixture

            var rewritten = CSharpSyntaxTree.ParseText(newRoot.ToFullString(), ParseOpts).GetRoot();
            var outErrors = CompileErrors(rewritten);

            // inputErrors is empty (asserted above), so ANY output error is new.
            Assert.True(outErrors.IsEmpty,
                $"[{label}] rewriter '{ruleId}' INTRODUCED a compiler error — source corruption:\n  " +
                string.Join("\n  ", outErrors.Select(d => $"{d.Id}: {d.GetMessage()}")) +
                $"\n--- rewritten output ---\n{newRoot.ToFullString()}");
        }

        // Coverage assertion: the rule this fixture targets must actually ENGAGE
        // (rewrite OR add a skip-comment). Otherwise a future regression that makes a
        // rewriter silently stop matching would leave the fixture vacuously green —
        // "the output compiles" is trivially true when nothing changed. `-noop-`
        // fixtures are the deliberate exception (the rule must NOT touch them).
        var expectedRule = ExpectedRuleFor(label);
        Assert.True(AutoFixTool.TryCreateRewriter(expectedRule, out var targeted) && targeted is not null,
            $"could not create rewriter for {expectedRule}");
        var engaged = !ReferenceEquals(targeted!.Visit(inputRoot), inputRoot);
        var shouldEngage = !label.Contains("-noop-", StringComparison.Ordinal);
        Assert.True(engaged == shouldEngage,
            $"[{label}] expected rule '{expectedRule}' to {(shouldEngage ? "ENGAGE (rewrite or add a skip-comment)" : "be a NO-OP")}, " +
            $"but it {(engaged ? "changed" : "did NOT change")} the input — coverage silently lost.");
    }

    // Maps a fixture label's prefix to the rule whose rewriter it targets.
    private static string ExpectedRuleFor(string label) => label.Split('-')[0] switch
    {
        "conc002" => "MAF-AP-CONC-002",
        "wf001"   => "MAF-AP-WF-001",
        "fanin"   => "MAF130-FAN-IN-001",
        "sec001"  => "MAF-AP-SEC-001",
        "sec003"  => "MAF-AP-SEC-003",
        var other => throw new InvalidOperationException($"fixture label '{label}' has an unknown rule prefix '{other}'"),
    };

    [Fact]
    public void EveryRewriterHasRoundtripCoverage()
    {
        // Durable guard against the corpus silently falling behind: every distinct
        // rewriter TYPE that AutoFixTool can dispatch must have at least one fixture
        // targeting it. A newly-added rewriter therefore cannot ship without a
        // compile-roundtrip fixture — the exact gap that left SEC-001 / SEC-003
        // unexercised until the adversarial review caught it.
        var fixtureRules = Fixtures()
            .Select(f => ExpectedRuleFor((string)f[0]))
            .ToHashSet();

        var ruleToType = new Dictionary<string, Type>();
        foreach (var id in AutoFixTool.SupportedRuleIds)
            if (AutoFixTool.TryCreateRewriter(id, out var r) && r is not null)
                ruleToType[id] = r.GetType();

        var coveredTypes = fixtureRules
            .Where(ruleToType.ContainsKey)
            .Select(id => ruleToType[id])
            .ToHashSet();

        var uncovered = ruleToType.Values.Distinct()
            .Where(t => !coveredTypes.Contains(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "Rewriter type(s) with NO compile-roundtrip fixture — add one to Fixtures() " +
            "(label prefix must map via ExpectedRuleFor): " + string.Join(", ", uncovered));
    }

    // -------------------------------------------------------------------------
    // Corpus. Each entry is a self-contained, clean-compiling C# program.
    // -------------------------------------------------------------------------

    public static IEnumerable<object[]> Fixtures()
    {
        // ---- CONC-002: REGRESSION shapes (the two historical corruptions). ----
        // The rewriter must SKIP these (await is illegal here); the skip-with-comment
        // output still compiles, so the roundtrip passes. If the guard regresses,
        // an injected `await` makes the output fail to compile and this test goes red.

        yield return F("conc002-regression-nested-query-join-in-let", """
            using System.Linq;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            public class C {
              Task<List<int>> Foo() => Task.FromResult(new List<int>());
              public async Task M() {
                var a = new List<int>();
                var b = new List<int>();
                var q = from i in a
                        let k = (from x in b join y in Foo().Result on x equals y select x).Count()
                        select i + k;
                await Task.Yield();
                _ = q.ToList();
              }
            }
            """);

        foreach (var (kind, member) in new[]
        {
            ("property",   "public unsafe int P { get { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return 0; } }"),
            ("indexer",    "public unsafe int this[int i] { get { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return 0; } }"),
            ("operator",   "public static unsafe C operator +(C a, C b) { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return a; }"),
            ("conversion", "public static unsafe implicit operator int(C c) { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return 0; }"),
            ("constructor","public unsafe C() { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; }"),
        })
        {
            yield return F($"conc002-regression-unsafe-{kind}-nested-lambda",
                "using System.Threading.Tasks;\npublic class C {\n  static Task<int> Foo() => Task.FromResult(1);\n  " + member + "\n}\n");
        }

        // ---- CONC-002: other await-ILLEGAL contexts (valid input ⇒ must skip). ----
        yield return F("conc002-skip-lock-body", """
            using System.Threading.Tasks;
            public class C {
              readonly object _lock = new();
              static Task<int> Foo() => Task.FromResult(1);
              public async Task M() { lock (_lock) { var x = Foo().Result; } await Task.Yield(); }
            }
            """);

        yield return F("conc002-skip-unsafe-block", """
            using System.Threading.Tasks;
            public class C {
              static Task<int> Foo() => Task.FromResult(1);
              public async Task M() { unsafe { var x = Foo().Result; } await Task.Yield(); }
            }
            """);

        yield return F("conc002-skip-catch-filter", """
            using System;
            using System.Threading.Tasks;
            public class C {
              static Task<int> Foo() => Task.FromResult(1);
              public async Task M() { try { await Task.Yield(); } catch (Exception) when (Foo().Result > 0) { } }
            }
            """);

        yield return F("conc002-skip-query-let", """
            using System.Linq;
            using System.Threading.Tasks;
            public class C {
              static Task<int> F() => Task.FromResult(1);
              public async Task M() {
                var q = from i in new[] { 1, 2 } let r = F().Result select r;
                await Task.Yield();
                _ = q.ToList();
              }
            }
            """);

        yield return F("conc002-skip-non-async-method", """
            using System.Threading.Tasks;
            public class C {
              static Task<int> Foo() => Task.FromResult(1);
              public int M() { return Foo().Result; }
            }
            """);

        yield return F("conc002-skip-async-lambda-in-unsafe-block", """
            using System;
            using System.Threading.Tasks;
            public class C {
              static Task<int> Foo() => Task.FromResult(1);
              public async Task M() { unsafe { Func<Task> f = async () => { var x = Foo().Result; }; } await Task.Yield(); }
            }
            """);

        yield return F("conc002-skip-fixed-block-result", """
            using System.Threading.Tasks;
            public class C {
              static Task<int> Foo() => Task.FromResult(1);
              int[] _a = new int[4];
              public async Task M() {
                unsafe { fixed (int* p = _a) { var x = Foo().Result; } }
                await Task.Yield();
              }
            }
            """);

        yield return F("conc002-skip-fixed-block-wait", """
            using System.Threading.Tasks;
            public class C {
              static Task Foo() => Task.CompletedTask;
              int[] _a = new int[4];
              public async Task M() {
                unsafe { fixed (int* p = _a) { Foo().Wait(); } }
                await Task.Yield();
              }
            }
            """);

        yield return F("conc002-skip-unsafe-local-function", """
            using System.Threading.Tasks;
            public class C {
              static Task<int> Foo() => Task.FromResult(1);
              public async Task M() {
                unsafe void Local() { var x = Foo().Result; }
                Local();
                await Task.Yield();
              }
            }
            """);

        yield return F("conc002-skip-query-secondary-from", """
            using System.Linq;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            public class C {
              static Task<List<int>> F() => Task.FromResult(new List<int>());
              public async Task M() {
                var q = from i in new[] { 1, 2 } from j in F().Result select i + j;
                await Task.Yield();
                _ = q.ToList();
              }
            }
            """);

        yield return F("conc002-skip-query-orderby", """
            using System.Linq;
            using System.Threading.Tasks;
            public class C {
              static Task<int> F() => Task.FromResult(1);
              public async Task M() {
                var q = from i in new[] { 1, 2 } orderby F().Result select i;
                await Task.Yield();
                _ = q.ToList();
              }
            }
            """);

        // ---- CONC-002: await-LEGAL contexts (valid input ⇒ should REWRITE; output must still compile). ----
        yield return F("conc002-rewrite-async-method-body", """
            using System.Threading.Tasks;
            public class C {
              static Task<int> FooAsync() => Task.FromResult(1);
              public async Task<int> M() { return FooAsync().Result; }
            }
            """);

        yield return F("conc002-rewrite-wait-async-method-body", """
            using System.Threading.Tasks;
            public class C {
              static Task FooAsync() => Task.CompletedTask;
              public async Task M() { FooAsync().Wait(); await Task.Yield(); }
            }
            """);

        yield return F("conc002-rewrite-top-level-join-source", """
            using System.Linq;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            public class C {
              Task<List<int>> FooAsync() => Task.FromResult(new List<int>());
              public async Task M() {
                var b = new List<int>();
                var q = from x in b join y in FooAsync().Result on x equals y select x;
                await Task.Yield();
                _ = q.ToList();
              }
            }
            """);

        yield return F("conc002-rewrite-async-lambda-in-lock", """
            using System;
            using System.Threading.Tasks;
            public class C {
              readonly object _lock = new();
              static Task<int> FooAsync() => Task.FromResult(1);
              public async Task M() { lock (_lock) { Func<Task> f = async () => { var x = FooAsync().Result; }; } await Task.Yield(); }
            }
            """);

        yield return F("conc002-rewrite-catch-body", """
            using System;
            using System.Threading.Tasks;
            public class C {
              static Task<int> FooAsync() => Task.FromResult(1);
              public async Task M() { try { await Task.Yield(); } catch (Exception) { var x = FooAsync().Result; } }
            }
            """);

        yield return F("conc002-rewrite-async-finally", """
            using System.Threading.Tasks;
            public class C {
              static Task<int> FooAsync() => Task.FromResult(1);
              public async Task M() { try { await Task.Yield(); } finally { var x = FooAsync().Result; } }
            }
            """);

        // ---- CONC-002: WM-03 regression — non-awaitable receivers must SKIP. ----
        // Valid inputs. Without the syntax-only awaitability gate the rewriter injected
        // `await` and produced CS1061 (SemaphoreSlim / a custom `.Result` wrapper have
        // no GetAwaiter). The skip-with-comment output still compiles ⇒ roundtrip green;
        // a regression that drops the gate makes the output fail to compile ⇒ red.
        yield return F("conc002-skip-semaphore-wait-nonawaitable", """
            using System.Threading;
            using System.Threading.Tasks;
            public class C {
              readonly SemaphoreSlim _sem = new(1);
              SemaphoreSlim GetLock() => _sem;
              public async Task M() { GetLock().Wait(); await Task.Yield(); }
            }
            """);

        yield return F("conc002-skip-custom-result-property", """
            using System.Threading.Tasks;
            public class OpResult { public int Result { get; set; } }
            public class C {
              OpResult Parse(string s) => new OpResult();
              public async Task M() { var x = Parse("a").Result; await Task.Yield(); }
            }
            """);

        // A Task/ValueTask factory receiver IS recognised as awaitable ⇒ still rewrites.
        yield return F("conc002-rewrite-task-run-result", """
            using System.Threading.Tasks;
            public class C {
              public async Task<int> M() { return Task.Run(() => 1).Result; }
            }
            """);

        // ---- ExecutorSealedRewriter (WF-001): output must compile. ----
        yield return F("wf001-rewrite-plain-executor", """
            using System.Threading.Tasks;
            public class Executor { }
            public sealed class MessageHandlerAttribute : System.Attribute { }
            public class MyExec : Executor {
              [MessageHandler]
              public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """);

        yield return F("wf001-rewrite-generic-executor", """
            using System.Threading.Tasks;
            public class Executor<TIn, TOut> { }
            public sealed class MessageHandlerAttribute : System.Attribute { }
            public class MyExec : Executor<string, int> {
              [MessageHandler]
              public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """);

        // WM-02 regression, re-aimed: `partial` must come last, right before `class`
        // (`partial sealed class` is CS0267). A `sealed` non-partial executor must
        // become `sealed partial`.
        yield return F("wf001-rewrite-sealed-without-partial", """
            using System.Threading.Tasks;
            public class Executor { }
            public sealed class MessageHandlerAttribute : System.Attribute { }
            sealed class MyExec : Executor {
              [MessageHandler]
              public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """);

        // WM-02 review follow-up: ZERO-modifier Executor (no access modifier, no
        // `partial`) — `partial` becomes the declaration's first token; it must carry
        // the class keyword's leading trivia rather than strand it.
        yield return F("wf001-rewrite-zero-modifier-executor", """
            using System.Threading.Tasks;
            public class Executor { }
            public sealed class MessageHandlerAttribute : System.Attribute { }
            class MyExec : Executor {
              [MessageHandler]
              public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """);

        yield return F("wf001-noop-abstract-partial-executor", """
            using System.Threading.Tasks;
            public class Executor { }
            public sealed class MessageHandlerAttribute : System.Attribute { }
            public abstract partial class BaseAuditor : Executor {
              [MessageHandler]
              public abstract Task<string> Audit(string s);
            }
            """);

        // ---- FanInArgOrderRewriter (FAN-IN-001): swap output must compile. ----
        yield return F("fanin-rewrite-swap-with-overloads", """
            public class B {
              public void AddFanInBarrierEdge(object target, object[] sources) { }
              public void AddFanInBarrierEdge(object[] sources, object target) { }
            }
            public class C {
              void F() {
                var b = new B();
                object agg = null;
                object o1 = null, o2 = null;
                b.AddFanInBarrierEdge(agg, new[] { o1, o2 }); // swapped to (sources, target)
              }
            }
            """);

        yield return F("fanin-rewrite-named-args", """
            public class B {
              public void AddFanInBarrierEdge(object[] sources, object target) { }
            }
            public class C {
              void F() {
                var b = new B();
                object agg = null;
                object o1 = null, o2 = null;
                b.AddFanInBarrierEdge(target: agg, sources: new[] { o1, o2 }); // labels stripped on swap
              }
            }
            """);

        yield return F("fanin-rewrite-collection-expression-source", """
            public class B {
              public void AddFanInBarrierEdge(object target, System.Collections.Generic.List<object> sources) { }
              public void AddFanInBarrierEdge(System.Collections.Generic.List<object> sources, object target) { }
            }
            public class C {
              void F() {
                var b = new B();
                object agg = null;
                object o1 = null, o2 = null;
                b.AddFanInBarrierEdge(agg, [o1, o2]); // collection-expression source
              }
            }
            """);

        // ---- SEC-001 / SEC-003: output of the security rewrites must compile. ----
        yield return F("sec001-rewrite-default-azure-credential", """
            public class DefaultAzureCredential { public DefaultAzureCredential() { } }
            public class ManagedIdentityCredential { public ManagedIdentityCredential() { } }
            public class C { object _c = new DefaultAzureCredential(); }
            """);

        yield return F("sec003-rewrite-embedded-if-assignment", """
            public class Options { public bool EnableSensitiveData { get; set; } }
            public class C {
              void M(Options opts, bool flag) { if (flag) opts.EnableSensitiveData = true; }
            }
            """);

        // The documented configure-callback shape: `c => c.EnableSensitiveData = true`
        // becomes `c => {}` (simple and parenthesized lambdas).
        yield return F("sec003-rewrite-expression-lambda", """
            public class Options { public bool EnableSensitiveData { get; set; } }
            public static class Builder { public static void UseOpenTelemetry(System.Action<Options> configure) { } }
            public class C {
              void M() { Builder.UseOpenTelemetry(configure: c => c.EnableSensitiveData = true); }
              void N() { Builder.UseOpenTelemetry((Options c) => c.EnableSensitiveData = true); }
            }
            """);
    }

    private static object[] F(string label, string source) => new object[] { label, source };
}
