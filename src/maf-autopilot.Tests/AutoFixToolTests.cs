using System.Text.Json;
using MafDoctor.Tools;
using MafDoctor.Tools.Rewriters;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Phase W.6 — tests for <see cref="AutoFixTool"/> + each rewriter.
///
/// Pattern: each rewriter test parses a synthetic input, runs the rewriter
/// directly (no I/O), asserts the output's text. Plus an end-to-end test
/// that exercises <see cref="AutoFixTool.MafAutoFix"/> against a temp dir
/// with a real sample file.
/// </summary>
public class AutoFixToolTests
{
    private static string ApplyRewriter(IRuleRewriter rewriter, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var newRoot = rewriter.Visit(tree.GetRoot());
        return newRoot.ToFullString();
    }

    // A clean WF-001 Executor target (so a rewriter WILL match) with one invalid
    // UTF-8 byte (0x80) inside a `//` comment, optionally UTF-8-BOM-prefixed. Because
    // a rewriter matches, the "byte-identical after apply" assertion is load-bearing:
    // if the strict decode were reverted the file would be U+FFFD-decoded, gain
    // `partial`, and be written back — no longer byte-identical.
    private static byte[] ExecutorSourceWithInvalidByte(bool utf8Bom)
    {
        const string head =
            "using System.Threading.Tasks;\npublic class Executor { }\n" +
            "public sealed class MessageHandlerAttribute : System.Attribute { }\n" +
            "public class MyExec : Executor {\n  // ";
        const string tail =
            "\n  [MessageHandler]\n  public Task<int> Handle(string s) => Task.FromResult(0);\n}\n";
        IEnumerable<byte> bytes = System.Text.Encoding.ASCII.GetBytes(head)
            .Concat(new byte[] { 0x80 })
            .Concat(System.Text.Encoding.ASCII.GetBytes(tail));
        if (utf8Bom) bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes);
        return bytes.ToArray();
    }

    // -------------------------------------------------------------------------
    // DefaultAzureCredentialRewriter (MAF-AP-SEC-001 / MAF002)
    // -------------------------------------------------------------------------

    [Fact]
    public void DefaultAzureCredentialRewriter_RewritesToManagedIdentity()
    {
        var input = """
            class C { object F() => new DefaultAzureCredential(); }
            """;
        var output = ApplyRewriter(new DefaultAzureCredentialRewriter(), input);
        Assert.Contains("new ManagedIdentityCredential()", output);
        Assert.DoesNotContain("DefaultAzureCredential", output);
    }

    [Fact]
    public void DefaultAzureCredentialRewriter_PreservesLeadingComment()
    {
        var input = """
            class C
            {
                // important comment about credentials
                object F() => new DefaultAzureCredential();
            }
            """;
        var output = ApplyRewriter(new DefaultAzureCredentialRewriter(), input);
        Assert.Contains("// important comment about credentials", output);
        Assert.Contains("new ManagedIdentityCredential()", output);
    }

    // -------------------------------------------------------------------------
    // EnableSensitiveDataRewriter (MAF-AP-SEC-003 / MAF003)
    // -------------------------------------------------------------------------

    [Fact]
    public void EnableSensitiveDataRewriter_DropsAssignment()
    {
        var input = """
            class C
            {
                object F() => new ChatOptions
                {
                    EnableSensitiveData = true,
                    Temperature = 0.7,
                };
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("EnableSensitiveData", output);
        Assert.Contains("Temperature = 0.7", output);
    }

    [Fact]
    public void EnableSensitiveDataRewriter_DropsDictionaryStyle()
    {
        var input = """
            class C
            {
                object F() => new AdditionalPropertiesDictionary
                {
                    ["EnableSensitiveData"] = true,
                    ["Other"] = 1,
                };
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("EnableSensitiveData", output);
        Assert.Contains("[\"Other\"] = 1", output);
    }

    [Fact]
    public void EnableSensitiveDataRewriter_NoOpIfFalse()
    {
        var input = """
            class C { object F() => new ChatOptions { EnableSensitiveData = false }; }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        // EnableSensitiveData = false is fine — only `true` triggers the fix.
        Assert.Contains("EnableSensitiveData = false", output);
    }

    // -------------------------------------------------------------------------
    // Phase 4.G fixup — shape C (member-access statement) + shape D (standalone
    // element-access statement). Analyzer Phase 4.4 caught these; rewriter
    // was missing parity. Now closed.
    // -------------------------------------------------------------------------

    [Fact]
    public void EnableSensitiveDataRewriter_MemberAccessStatement_StatementRemoved()
    {
        var input = """
            class C
            {
                void Configure()
                {
                    var opts = new ChatOptions();
                    opts.EnableSensitiveData = true;
                    opts.MaxTokens = 1024;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("opts.EnableSensitiveData = true", output);
        Assert.Contains("opts.MaxTokens = 1024", output);
    }

    [Fact]
    public void EnableSensitiveDataRewriter_ElementAccessStatement_StatementRemoved()
    {
        var input = """
            using System.Collections.Generic;
            class C
            {
                void Configure()
                {
                    var dict = new Dictionary<string, object>();
                    dict["EnableSensitiveData"] = true;
                    dict["KeepThis"] = true;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("dict[\"EnableSensitiveData\"]", output);
        Assert.Contains("dict[\"KeepThis\"]", output);
    }

    [Fact]
    public void EnableSensitiveDataRewriter_MemberAccessStatementFalse_NotRemoved()
    {
        var input = """
            class C
            {
                void Configure()
                {
                    var opts = new ChatOptions();
                    opts.EnableSensitiveData = false;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.Contains("opts.EnableSensitiveData = false", output);
    }

    // -------------------------------------------------------------------------
    // Phase 7-final fixup — embedded-statement contexts.
    //
    // Returning `null` from `VisitExpressionStatement` to remove a statement
    // CRASHES Roslyn's `VisitIfStatement` / `VisitForStatement` / etc. when
    // the single embedded child is a removable assignment (the parent is
    // not a block and expects a non-null replacement). Fix: substitute an
    // empty `;` statement in those positions.
    //
    // The crash was caught by AutoFixTool's outer try/catch — but the rule
    // silently no-op'd on those files, re-creating the analyzer/rewriter
    // asymmetry Phase 4.G was meant to close. Tests below pin every
    // embedded shape.
    // -------------------------------------------------------------------------

    [Fact]
    public void EnableSensitiveDataRewriter_EmbeddedInSingleLineIf_ReplacedWithEmptyStatement()
    {
        var input = """
            class C
            {
                void Configure(bool cond)
                {
                    var opts = new ChatOptions();
                    if (cond) opts.EnableSensitiveData = true;
                    opts.MaxTokens = 1024;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        // The dangerous assignment is gone.
        Assert.DoesNotContain("opts.EnableSensitiveData = true", output);
        // Surrounding statements survive intact.
        Assert.Contains("opts.MaxTokens = 1024", output);
        // The output re-parses cleanly (no crash, no orphan tokens).
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(output);
        Assert.Empty(tree.GetDiagnostics().Where(
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void EnableSensitiveDataRewriter_EmbeddedInSingleLineFor_ReplacedWithEmptyStatement()
    {
        var input = """
            class C
            {
                void Configure()
                {
                    var opts = new ChatOptions();
                    for (int i = 0; i < 10; i++) opts.EnableSensitiveData = true;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("opts.EnableSensitiveData = true", output);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(output);
        Assert.Empty(tree.GetDiagnostics().Where(
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void EnableSensitiveDataRewriter_EmbeddedInIfElse_ReplacedWithEmptyStatement()
    {
        var input = """
            class C
            {
                void Configure(bool cond)
                {
                    var opts = new ChatOptions();
                    if (cond) { opts.MaxTokens = 100; }
                    else opts.EnableSensitiveData = true;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("opts.EnableSensitiveData = true", output);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(output);
        Assert.Empty(tree.GetDiagnostics().Where(
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void EnableSensitiveDataRewriter_EmbeddedDictionaryStyleInSingleLineIf_Handled()
    {
        var input = """
            using System.Collections.Generic;
            class C
            {
                void Configure(bool cond, Dictionary<string, object> dict)
                {
                    if (cond) dict["EnableSensitiveData"] = true;
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("[\"EnableSensitiveData\"] = true", output);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(output);
        Assert.Empty(tree.GetDiagnostics().Where(
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    // -------------------------------------------------------------------------
    // ExecutorSealedRewriter (MAF-AP-WF-001)
    // -------------------------------------------------------------------------

    [Fact]
    public void ExecutorSealedRewriter_AddsOnlyPartial()
    {
        // The generator requires `partial` (MAFGENWF003), not `sealed`; adding `sealed`
        // would break a build that subclasses the executor (CS0509).
        var input = """
            using Microsoft.Agents.AI.Workflows;
            public class MyExec : Executor
            {
                [MessageHandler]
                public ValueTask H(int x) => default;
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input);
        Assert.Contains("public partial class MyExec", output);
        Assert.DoesNotContain("sealed", output);
    }

    [Fact]
    public void ExecutorSealedRewriter_NoOpIfAlreadyPartial()
    {
        var input = """
            using Microsoft.Agents.AI.Workflows;
            public partial class MyExec : Executor
            {
                [MessageHandler]
                public ValueTask H(int x) => default;
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input);
        Assert.Equal(input, output.TrimEnd());
    }

    [Fact]
    public void ExecutorSealedRewriter_NoOpIfAlreadySealed()
    {
        var input = """
            using Microsoft.Agents.AI.Workflows;
            public sealed partial class MyExec : Executor
            {
                [MessageHandler]
                public ValueTask H(int x) => default;
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input);
        // Must NOT double-add `sealed`.
        Assert.Equal(input, output.TrimEnd());
    }

    [Fact]
    public void ExecutorSealedRewriter_NoOpIfNoMessageHandler()
    {
        // Class derives from Executor but has no [MessageHandler] method.
        // Rewriter must not touch it (might be a base class or fixture).
        var input = """
            using Microsoft.Agents.AI.Workflows;
            public partial class MyExec : Executor
            {
                public ValueTask H(int x) => default;
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input);
        Assert.Equal(input, output.TrimEnd());
    }

    [Fact]
    public void ExecutorSealedRewriter_GenericExecutorBase_AddsPartial()
    {
        // Round-8 parity: a generic `Executor<…>` base must be partial'd too,
        // matching the now generic-aware WF-001 scanner (shared IsExecutorBaseType).
        var input = """
            public class MyExec : Microsoft.Agents.AI.Workflows.Executor<string, int>
            {
                [MessageHandler]
                public Task<int> Handle(string s) => Task.FromResult(0);
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input);
        Assert.Contains("public partial class MyExec", output);
    }

    // -------------------------------------------------------------------------
    // FanInArgOrderRewriter (MAF130-FAN-IN-001)
    // -------------------------------------------------------------------------

    [Fact]
    public void FanInArgOrderRewriter_SwapsPositionalArgs()
    {
        var input = """
            class C
            {
                void F(object builder, object aggregator, object osint, object history)
                {
                    builder.AddFanInBarrierEdge(aggregator, new[] { osint, history });
                }
            }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        // Expect `(new[] { ... }, aggregator)` order.
        var idxArray = output.IndexOf("new[] {");
        var idxAggregator = output.IndexOf("aggregator", StringComparison.Ordinal);
        // Skip the method-parameter occurrences ("object aggregator," in the signature).
        var idxAggregatorInCall = output.IndexOf("aggregator", idxArray, StringComparison.Ordinal);
        Assert.True(idxArray > 0);
        Assert.True(idxAggregatorInCall > idxArray, "Expected `aggregator` to appear AFTER the array in the new call.");
    }

    [Fact]
    public void FanInArgOrderRewriter_NoOpIfAlreadyCorrect()
    {
        var input = """
            class C
            {
                void F(object builder, object aggregator, object osint, object history)
                {
                    builder.AddFanInBarrierEdge(new[] { osint, history }, aggregator);
                }
            }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        Assert.Equal(input, output.TrimEnd());
    }

    [Fact]
    public void FanInArgOrderRewriter_SwapsNamedArgs()
    {
        var input = """
            class C
            {
                void F(object builder, object aggregator, object osint, object history)
                {
                    builder.AddFanInBarrierEdge(target: aggregator, sources: new[] { osint, history });
                }
            }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        // Named labels stripped + args reordered.
        Assert.DoesNotContain("target:", output);
        Assert.DoesNotContain("sources:", output);
    }

    [Fact]
    public void FanInArgOrderRewriter_DoesNotDropInterArgComment()
    {
        // Round-8 regression: a comment carried on the separator (between the two
        // arguments) must survive the swap — the old SeparatedList(values) discarded
        // the original separator and lost it.
        var input = """
            class C
            {
                void F(object builder, object target, object s1, object s2)
                {
                    builder.AddFanInBarrierEdge(
                        target,            // the join node
                        new[] { s1, s2 });
                }
            }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        Assert.Contains("// the join node", output); // preserved, not dropped
    }

    [Fact]
    public void FanInArgOrderRewriter_KeepsEachValuesOwnTrailingComment()
    {
        // Round-8 regression: each argument's own trailing comment travels with its
        // VALUE, not its slot. Pre-fix WithTriviaFrom swapped them onto wrong args.
        var input = """
            class C
            {
                void F(object builder, object targetNode, object s1, object s2)
                {
                    builder.AddFanInBarrierEdge(targetNode /*T*/, new[] { s1, s2 } /*S*/);
                }
            }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        // New order: `new[] { s1, s2 } /*S*/, targetNode /*T*/`.
        var idxArray = output.IndexOf("new[]", StringComparison.Ordinal);
        var idxS = output.IndexOf("/*S*/", StringComparison.Ordinal);
        var callStart = output.IndexOf("AddFanInBarrierEdge", StringComparison.Ordinal);
        var idxTargetInCall = output.IndexOf("targetNode", callStart, StringComparison.Ordinal);
        var idxT = output.IndexOf("/*T*/", StringComparison.Ordinal);
        Assert.Contains("/*T*/", output);
        Assert.Contains("/*S*/", output);
        Assert.True(idxArray < idxS && idxS < idxTargetInCall, $"/*S*/ must stay with the array. Output:\n{output}");
        Assert.True(idxTargetInCall < idxT, $"/*T*/ must stay with targetNode. Output:\n{output}");
    }

    // -------------------------------------------------------------------------
    // SyncOverAsyncRewriter (MAF-AP-CONC-002)
    // -------------------------------------------------------------------------

    [Fact]
    public void SyncOverAsyncRewriter_ReplacesResultWithAwait()
    {
        var input = """
            using System.Threading.Tasks;
            class C { async Task F() { var x = SomeAsync().Result; } static Task<int> SomeAsync() => default; }
            """;
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), input);
        Assert.True(output.Contains("await SomeAsync()"), $"Output:\n{output}");
        Assert.DoesNotContain(".Result", output);
    }

    [Fact]
    public void SyncOverAsyncRewriter_ReplacesWaitWithAwait()
    {
        var input = """
            using System.Threading.Tasks;
            class C { async Task F() { SomeAsync().Wait(); } static Task SomeAsync() => default; }
            """;
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), input);
        Assert.True(output.Contains("await SomeAsync()"), $"Output:\n{output}");
        Assert.DoesNotContain(".Wait(", output);
    }

    // -------------------------------------------------------------------------
    // AutoFixTool — orchestration
    // -------------------------------------------------------------------------

    [Fact]
    public void AutoFixTool_UnknownRule_ReturnsErrorJson()
    {
        var tool = new AutoFixTool();
        var result = tool.MafAutoFix(Path.GetTempPath(), "MAF-AP-NOT-A-RULE");
        Assert.Contains("Unknown ruleId", result);
    }

    [Fact]
    public void AutoFixTool_EmptyPath_ReturnsErrorJson()
    {
        var tool = new AutoFixTool();
        var result = tool.MafAutoFix("", "MAF-AP-SEC-001");
        // PathGuard rejects empty.
        Assert.Contains("error", result);
    }

    [Fact]
    public void AutoFixTool_DryRun_DoesNotWriteFiles()
    {
        // Arrange — temp dir with one offending file.
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testFile = Path.Combine(tempRoot, "Bad.cs");
        var original = "class C { object F() => new DefaultAzureCredential(); }";
        File.WriteAllText(testFile, original);

        try
        {
            // Act — dry run.
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFix(tempRoot, "MAF-AP-SEC-001", dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;

            // Assert — JSON says 1 file would change, but disk is untouched.
            Assert.Equal(1, result.RootElement.GetProperty("filesChanged").GetInt32());
            Assert.True(result.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(original, File.ReadAllText(testFile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AutoFixTool_AppliesFixAndWritesFile()
    {
        // Arrange — temp dir with one offending file.
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testFile = Path.Combine(tempRoot, "Bad.cs");
        File.WriteAllText(testFile, "class C { object F() => new DefaultAzureCredential(); }");

        try
        {
            // Act.
            var tool = new AutoFixTool();
            // F-11: dryRun now defaults to true — this test is specifically about
            // the write path, so it opts in explicitly.
            var resultJson = tool.MafAutoFix(tempRoot, "MAF-AP-SEC-001", dryRun: false);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;

            // Assert — JSON says 1 file changed; disk reflects the rewrite.
            Assert.Equal(1, result.RootElement.GetProperty("filesChanged").GetInt32());
            Assert.False(result.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Contains("ManagedIdentityCredential", File.ReadAllText(testFile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AutoFixTool_AliasedRuleIds_AreEquivalent()
    {
        // MAF-AP-SEC-001 and MAF002 must produce the same rewriter behaviour.
        Assert.Contains("MAF-AP-SEC-001", AutoFixTool.SupportedRuleIds);
        Assert.Contains("MAF002", AutoFixTool.SupportedRuleIds);
        Assert.Contains("MAF-AP-SEC-003", AutoFixTool.SupportedRuleIds);
        Assert.Contains("MAF003", AutoFixTool.SupportedRuleIds);
    }

    // -------------------------------------------------------------------------
    // MafAutoFixAll — the "fix everything fixable" batch command
    // -------------------------------------------------------------------------

    [Fact]
    public void AutoFixAll_EmptyPath_ReturnsErrorJson()
    {
        var tool = new AutoFixTool();
        var result = tool.MafAutoFixAll("");
        Assert.Contains("error", result);
    }

    [Fact]
    public void AutoFixAll_AppliesAllRulesInOrder()
    {
        // Arrange — temp dir with files triggering multiple rules at once.
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-all-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "Sec.cs"),
            "class C { object F() => new DefaultAzureCredential(); }");
        File.WriteAllText(Path.Combine(tempRoot, "Sensitive.cs"),
            "class C { object F() => new ChatOptions { EnableSensitiveData = true, Temperature = 0.5 }; }");

        try
        {
            // Act.
            var tool = new AutoFixTool();
            // F-11: dryRun now defaults to true — this test is specifically about
            // the write path, so it opts in explicitly.
            var resultJson = tool.MafAutoFixAll(tempRoot, dryRun: false);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;

            // Assert — 2 distinct files changed across the rule set.
            Assert.Equal(2, result.RootElement.GetProperty("totalDistinctFilesChanged").GetInt32());

            // The per-rule breakdown should show the SEC-001 + SEC-003 entries.
            var perRule = result.RootElement.GetProperty("perRule");
            Assert.Equal(1, perRule.GetProperty("MAF-AP-SEC-001").GetProperty("filesChanged").GetInt32());
            Assert.Equal(1, perRule.GetProperty("MAF-AP-SEC-003").GetProperty("filesChanged").GetInt32());

            // Disk state confirms the rewrites.
            Assert.Contains("ManagedIdentityCredential", File.ReadAllText(Path.Combine(tempRoot, "Sec.cs")));
            Assert.DoesNotContain("EnableSensitiveData", File.ReadAllText(Path.Combine(tempRoot, "Sensitive.cs")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AutoFixAll_DryRun_DoesNotWriteFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-all-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "Bad.cs");
        var original = "class C { object F() => new DefaultAzureCredential(); }";
        File.WriteAllText(path, original);

        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFixAll(tempRoot, dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;

            // Dry-run reports 1 file would change AND leaves the disk content untouched.
            Assert.Equal(1, result.RootElement.GetProperty("totalDistinctFilesChanged").GetInt32());
            Assert.True(result.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AutoFixAll_OrderOfExecution_PutsSealedBeforeFanIn()
    {
        // Critical invariant: the dependency-safe order is documented in the
        // tool's docstring AND in the returned `orderOfExecution` field. This
        // test pins the order so future refactors don't silently break it.
        //
        // Use a unique temp SUBDIRECTORY (not Path.GetTempPath() itself) — on
        // Linux CI the bare temp root contains systemd-private-* dirs owned by
        // root which trigger UnauthorizedAccessException in the walker. The
        // walker is now defensive (IgnoreInaccessible=true) but the test
        // shouldn't depend on a system-wide path either way.
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFixAll(tempRoot, dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;

            var order = result.RootElement.GetProperty("orderOfExecution")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();

            var sealedIdx = order.IndexOf("MAF-AP-WF-001");
            var fanInIdx = order.IndexOf("MAF130-FAN-IN-001");
            var syncIdx = order.IndexOf("MAF-AP-CONC-002");

            Assert.True(sealedIdx >= 0 && fanInIdx >= 0 && syncIdx >= 0);
            Assert.True(sealedIdx < fanInIdx, "ExecutorSealed must run before FanInArgOrder");
            Assert.True(fanInIdx < syncIdx,   "FanInArgOrder must run before SyncOverAsync");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AutoFixAll_NoOpRepo_ReportsZeroChanges()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-all-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "Clean.cs"), "class C { int X() => 1; }");

        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFixAll(tempRoot);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;
            Assert.Equal(0, result.RootElement.GetProperty("totalDistinctFilesChanged").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Phase 1.1 — C1 path-escape fix
    //
    // MafAutoFix.specificFile previously joined to repoPath with a
    // `Path.IsPathRooted` short-circuit that accepted absolute paths and
    // unblocked `..` segments. The new flow runs every non-empty specificFile
    // through PathGuard.ValidateContainment at the public entry point.
    // -------------------------------------------------------------------------

    [Fact]
    public void MafAutoFix_SpecificFile_AbsolutePathOutsideRepo_ReturnsError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-c1-abs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        // Pick an absolute path that exists but lives outside the repo root.
        // %SystemRoot%\System32\drivers\etc\hosts on Windows or /etc/hosts on POSIX.
        var outsidePath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : "/etc/hosts";

        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFix(tempRoot, "MAF-AP-SEC-001",
                specificFile: outsidePath, dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;
            Assert.True(result.RootElement.TryGetProperty("error", out var err),
                $"Expected error property, got: {resultJson}");
            Assert.Contains("repository root", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MafAutoFix_SpecificFile_DotDotEscape_ReturnsError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-c1-dotdot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var tool = new AutoFixTool();
            // Relative `..` segments that would syntactically escape the root.
            var resultJson = tool.MafAutoFix(tempRoot, "MAF-AP-SEC-001",
                specificFile: "../../../etc/hosts", dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;
            Assert.True(result.RootElement.TryGetProperty("error", out var err),
                $"Expected error property, got: {resultJson}");
            // PathGuard rejects `..` segments with the "traversal" wording from ValidateRepoPath.
            Assert.Contains("..", err.GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MafAutoFix_SpecificFile_ShellMetacharacter_ReturnsError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-c1-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFix(tempRoot, "MAF-AP-SEC-001",
                specificFile: "Foo.cs; rm -rf /", dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;
            Assert.True(result.RootElement.TryGetProperty("error", out var err),
                $"Expected error property, got: {resultJson}");
            Assert.Contains("invalid characters", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MafAutoFix_SpecificFile_LegitRelativePath_NoContainmentError()
    {
        // A legitimate relative path inside the repo must not be rejected by
        // containment; the tool should reach EnumerateFiles and return a normal
        // (empty changed-files) result rather than an `error` property.
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofix-c1-legit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "Foo.cs"), "class C { int X() => 1; }");

        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFix(tempRoot, "MAF-AP-SEC-001",
                specificFile: "Foo.cs", dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;
            Assert.False(result.RootElement.TryGetProperty("error", out _),
                $"Did not expect error property for legit relative path, got: {resultJson}");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Phase 4.1a — ExecutorSealedRewriter corruption guards.
    //
    // `sealed abstract class` is a C# compile error (the two modifiers are
    // mutually exclusive). Pre-fix the rewriter happily inserted `sealed` on
    // any class deriving from Executor with a [MessageHandler], producing
    // uncompilable user code when the source class was abstract. Same for
    // `sealed static class` (also rejected by the compiler). Since 2026-10 the
    // rewriter never adds `sealed`; an abstract Executor gets `partial` like any
    // other (the generator requires it), and a static one is left alone.
    // -------------------------------------------------------------------------

    [Fact]
    public void ExecutorSealedRewriter_AbstractClass_GetsPartialNeverSealed()
    {
        var src = """
            using Microsoft.Agents.AI.Workflow;
            public abstract class BaseAuditor : Executor
            {
                [MessageHandler]
                public abstract System.Threading.Tasks.Task<string> Audit(string input);
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), src);

        // `partial` (which the generator requires), never `sealed` (the uncompilable
        // `abstract sealed`).
        Assert.Contains("public abstract partial class BaseAuditor", output);
        Assert.DoesNotContain("sealed", output);
    }

    [Fact]
    public void ExecutorSealedRewriter_RegularClass_StillRewritten()
    {
        // Sanity check — the guard doesn't accidentally skip a legitimate target.
        var src = """
            using Microsoft.Agents.AI.Workflow;
            public class FraudAuditor : Executor
            {
                [MessageHandler]
                public System.Threading.Tasks.ValueTask<string> Audit(string input, IWorkflowContext context) => default;
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), src);
        Assert.Contains("public partial class FraudAuditor", output);
    }

    // -------------------------------------------------------------------------
    // Phase 4.1b — SyncOverAsyncRewriter corruption guards.
    //
    // `await` is a compile error (a) inside a `lock` block (CS1996) and
    // (b) inside a non-async method (CS0117). Pre-fix the rewriter inserted
    // `await` without checking either context, producing uncompilable code.
    // Now: skip the rewrite and emit a TODO comment.
    // -------------------------------------------------------------------------

    [Fact]
    public void SyncOverAsyncRewriter_InsideLockBlock_NotRewritten_WithWarning()
    {
        var src = """
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
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), src);

        // Result access still uses `.Result` — the rewriter did NOT insert `await`.
        Assert.Contains(".Result", output);
        Assert.Contains("cannot await inside a lock", output);
    }

    [Fact]
    public void SyncOverAsyncRewriter_NonAsyncMethod_NotRewritten_WithWarning()
    {
        var src = """
            class C
            {
                int M()
                {
                    return Foo().Result;
                }
                System.Threading.Tasks.Task<int> Foo() => null!;
            }
            """;
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), src);

        Assert.Contains(".Result", output);
        Assert.Contains("enclosing method is not async", output);
    }

    [Fact]
    public void SyncOverAsyncRewriter_AsyncMethodOutsideLock_StillRewritten()
    {
        // Sanity check — the guard doesn't break the happy path. Method named
        // `*Async` so the syntax-only awaitability gate rewrites it (a non-Async
        // name would now be a safe skip — see SemaphoreSlim/custom-.Result cases).
        var src = """
            class C
            {
                async System.Threading.Tasks.Task M()
                {
                    var x = FooAsync().Result;
                }
                System.Threading.Tasks.Task<int> FooAsync() => null!;
            }
            """;
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), src);
        Assert.Contains("await FooAsync()", output);
        Assert.DoesNotContain(".Result", output);
    }

    [Fact]
    public void SyncOverAsyncRewriter_WaitCall_InsideLock_NotRewritten()
    {
        var src = """
            class C
            {
                readonly object _lock = new();
                async System.Threading.Tasks.Task M()
                {
                    lock (_lock)
                    {
                        Foo().Wait();
                    }
                }
                System.Threading.Tasks.Task Foo() => null!;
            }
            """;
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), src);
        Assert.Contains(".Wait()", output);
        Assert.Contains("cannot await inside a lock", output);
    }

    [Fact]
    public void MafAutoFixAll_SpecificFile_AbsolutePathOutsideRepo_ReturnsError()
    {
        // Same fix applied to MafAutoFixAll; multiplies blast radius via the
        // ordered rule pass, so an independent test pins the behavior.
        var tempRoot = Path.Combine(Path.GetTempPath(), "maf-autofixall-c1-abs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var outsidePath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : "/etc/hosts";

        try
        {
            var tool = new AutoFixTool();
            var resultJson = tool.MafAutoFixAll(tempRoot, specificFile: outsidePath, dryRun: true);
            var result = JsonSerializer.Deserialize<JsonDocument>(resultJson)!;
            Assert.True(result.RootElement.TryGetProperty("error", out var err),
                $"Expected error property, got: {resultJson}");
            Assert.Contains("repository root", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // WM-41: a missing / non-.cs specificFile is an ERROR, not a silent 0-change.
    // -------------------------------------------------------------------------

    [Fact]
    public void MafAutoFix_MissingSpecificFile_ReturnsError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm41-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var json = new AutoFixTool().MafAutoFix(dir, "MAF-AP-CONC-002", specificFile: "nope.cs", dryRun: true);
            var doc = JsonSerializer.Deserialize<JsonDocument>(json)!;
            Assert.True(doc.RootElement.TryGetProperty("error", out var err), json);
            Assert.Contains("not found", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MafAutoFix_NonCsSpecificFile_ReturnsError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm41-noncs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not code");
            var json = new AutoFixTool().MafAutoFix(dir, "MAF-AP-CONC-002", specificFile: "readme.txt", dryRun: true);
            var doc = JsonSerializer.Deserialize<JsonDocument>(json)!;
            Assert.True(doc.RootElement.TryGetProperty("error", out var err), json);
            Assert.Contains(".cs file", err.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // WM-20: encoding/BOM preservation + undecodable-file safety on apply.
    // -------------------------------------------------------------------------

    [Fact]
    public void MafAutoFixAll_PreservesUtf8Bom_OnApply()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm20-bom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Bom.cs");
            var code = """
                using System.Threading.Tasks;
                public class Executor { }
                public sealed class MessageHandlerAttribute : System.Attribute { }
                public class MyExec : Executor {
                  [MessageHandler]
                  public Task<int> Handle(string s) => Task.FromResult(0);
                }
                """;
            File.WriteAllText(file, code, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false); // WF-001 adds `partial` → file rewritten
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "UTF-8 BOM must be preserved across the rewrite");
            Assert.Contains("public partial class MyExec", File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MafAutoFixAll_UndecodableFile_SkippedWithError_NotCorrupted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm20-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A WF-001-matching Executor with an invalid byte in a comment — so if the
            // strict decode were reverted, WF-001 would add `partial` and the file WOULD
            // be rewritten (0x80 → EF BF BD), making the byte-identical check fail.
            var file = Path.Combine(dir, "MyExec.cs");
            var badBytes = ExecutorSourceWithInvalidByte(utf8Bom: false);
            File.WriteAllBytes(file, badBytes);
            var json = new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var doc = JsonSerializer.Deserialize<JsonDocument>(json)!;
            Assert.True(doc.RootElement.GetProperty("errors").GetArrayLength() >= 1,
                "an undecodable file must be reported, not silently swallowed: " + json);
            Assert.Equal(badBytes, File.ReadAllBytes(file)); // skipped whole — not U+FFFD-rewritten
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // WM-23: single per-file pass — dry-run composes rewriters exactly as apply,
    // both rules land in one pass, and a second apply is a no-op (idempotent).
    // -------------------------------------------------------------------------

    [Fact]
    public void MafAutoFixAll_DryRunComposesLikeApply_AndIsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm23-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Multi.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class Executor { }
                public sealed class MessageHandlerAttribute : System.Attribute { }
                public class MyExec : Executor {
                  [MessageHandler]
                  public async Task<int> Handle(string s) { return FetchAsync().Result; }
                  static Task<int> FetchAsync() => Task.FromResult(0);
                }
                """);
            var tool = new AutoFixTool();

            var preview = JsonSerializer.Deserialize<JsonDocument>(tool.MafAutoFixAll(dir, dryRun: true))!;
            var previewTotal = preview.RootElement.GetProperty("totalDistinctFilesChanged").GetInt32();

            var apply = JsonSerializer.Deserialize<JsonDocument>(tool.MafAutoFixAll(dir, dryRun: false))!;
            var applyTotal = apply.RootElement.GetProperty("totalDistinctFilesChanged").GetInt32();

            Assert.Equal(previewTotal, applyTotal); // dry-run reported exactly what apply did
            Assert.Equal(1, applyTotal);

            var after = File.ReadAllText(file);
            Assert.Contains("public partial class MyExec", after);        // WF-001 applied
            Assert.Contains("(await FetchAsync())", after);  // CONC-002 applied in the SAME pass

            var again = JsonSerializer.Deserialize<JsonDocument>(tool.MafAutoFixAll(dir, dryRun: false))!;
            Assert.Equal(0, again.RootElement.GetProperty("totalDistinctFilesChanged").GetInt32());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // WM-24: dry-run preview carries a reviewable per-file diff, not just names.
    // -------------------------------------------------------------------------

    [Fact]
    public void MafAutoFixAll_DryRun_IncludesReviewableDiff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm24-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Exec.cs");
            var original = """
                using System.Threading.Tasks;
                public class Executor { }
                public sealed class MessageHandlerAttribute : System.Attribute { }
                public class MyExec : Executor {
                  [MessageHandler]
                  public Task<int> Handle(string s) => Task.FromResult(0);
                }
                """;
            File.WriteAllText(file, original);
            var json = new AutoFixTool().MafAutoFixAll(dir, dryRun: true);
            var doc = JsonSerializer.Deserialize<JsonDocument>(json)!;
            var diffs = doc.RootElement.GetProperty("diffs");
            Assert.Equal(1, diffs.GetArrayLength());
            var diffText = diffs[0].GetProperty("diff").GetString()!;
            Assert.Contains("+", diffText);                    // a real added line, not just a file name
            Assert.Contains("partial class MyExec", diffText); // the rewrite is shown
            Assert.Equal(original, File.ReadAllText(file));    // dry-run wrote nothing
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // WM-22 / WM-42 / WM-43: rewriter-correctness regressions.
    // -------------------------------------------------------------------------

    [Fact]
    public void EnableSensitiveDataRewriter_PreservesSurvivingEntryCommentsAndLines()
    {
        var input = """
            class C {
                void M() {
                    var o = new Options {
                        Name = "x", // keep me
                        EnableSensitiveData = true,
                        Count = 3,
                    };
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input).Replace("\r\n", "\n");
        Assert.DoesNotContain("EnableSensitiveData", output);
        Assert.Contains("// keep me", output);   // inline comment survives (was deleted before WM-22)
        Assert.Contains("Name = \"x\"", output);
        Assert.Contains("Count = 3", output);
        // Surviving entries are NOT collapsed onto one line: a newline separates them.
        var idxComment = output.IndexOf("// keep me", StringComparison.Ordinal);
        var idxCount = output.IndexOf("Count = 3", StringComparison.Ordinal);
        Assert.True(idxComment >= 0 && idxCount > idxComment);
        Assert.Contains('\n', output[idxComment..idxCount]);
    }

    [Fact]
    public void SyncOverAsyncRewriter_SkipTodo_UsesFileNewline_Crlf()
    {
        // `.Result` in a NON-async method → skip-with-TODO. CRLF source: the
        // inserted comment line must end CRLF, not splice in a lone LF (WM-42).
        var src = "class C {\r\n    int M() {\r\n        return Foo().Result;\r\n    }\r\n    System.Threading.Tasks.Task<int> Foo() => null!;\r\n}\r\n";
        var output = ApplyRewriter(new SyncOverAsyncRewriter(), src);
        var idx = output.IndexOf("// MAF-AP-CONC-002", StringComparison.Ordinal);
        Assert.True(idx >= 0, "expected a skip-with-TODO comment");
        var firstNl = output.IndexOf('\n', idx);
        Assert.True(firstNl > 0 && output[firstNl - 1] == '\r',
            "the inserted TODO line must end with CRLF in a CRLF file, not a lone LF");
    }

    [Fact]
    public void FanInArgOrderRewriter_DoesNotSwap_TypeMerelyContainingList()
    {
        // `ListenerNode` merely CONTAINS "List" but is not a collection — must not
        // be mistaken for a sources array and trigger a wrong swap (WM-43).
        var input = """
            class ListenerNode {}
            class B { void AddFanInBarrierEdge(object target, object sources) {} }
            class C { void M() { var b = new B(); object t = null; b.AddFanInBarrierEdge(t, new ListenerNode()); } }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        Assert.Contains("AddFanInBarrierEdge(t, new ListenerNode())", output); // unchanged
    }

    [Fact]
    public void FanInArgOrderRewriter_StillSwaps_GenericListSource()
    {
        var input = """
            using System.Collections.Generic;
            class B {
              void AddFanInBarrierEdge(object target, List<object> sources) {}
              void AddFanInBarrierEdge(List<object> sources, object target) {}
            }
            class C { void M() { var b = new B(); object t = null; b.AddFanInBarrierEdge(t, new List<object>()); } }
            """;
        var output = ApplyRewriter(new FanInArgOrderRewriter(), input);
        Assert.Contains("AddFanInBarrierEdge(new List<object>(), t)", output); // genuine List still swaps
    }

    // -------------------------------------------------------------------------
    // WM-44: concurrent applies on one repo are serialised by an exclusive lock.
    // -------------------------------------------------------------------------

    [Fact]
    public void MafAutoFixAll_Apply_WhenLockHeld_ReturnsError_ButDryRunIsNeverBlocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-wm44-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lockPath = AutoFixTool.ApplyLockPath(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "C.cs"), "public class C {}");

            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // A concurrent APPLY is refused while the lock is held...
                var applyJson = new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
                var applyDoc = JsonSerializer.Deserialize<JsonDocument>(applyJson)!;
                Assert.True(applyDoc.RootElement.TryGetProperty("error", out var err), applyJson);
                Assert.Contains("already applying", err.GetString(), StringComparison.OrdinalIgnoreCase);

                // ...but a DRY-RUN writes nothing, so it must never be blocked.
                var dryDoc = JsonSerializer.Deserialize<JsonDocument>(
                    new AutoFixTool().MafAutoFixAll(dir, dryRun: true))!;
                Assert.False(dryDoc.RootElement.TryGetProperty("error", out _));
            }

            // Lock released → apply succeeds.
            var okDoc = JsonSerializer.Deserialize<JsonDocument>(
                new AutoFixTool().MafAutoFixAll(dir, dryRun: false))!;
            Assert.False(okDoc.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            try { File.Delete(lockPath); } catch { /* best-effort */ }
            Directory.Delete(dir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Adversarial-review follow-ups (Phase 1 verification pass).
    // -------------------------------------------------------------------------

    [Fact]
    public void ExecutorSealedRewriter_ZeroModifierClass_KeepsIndentAndDocAttached()
    {
        // A bare (internal-by-default) Executor with a leading XML doc: `partial`
        // must carry the declaration's leading trivia, else the doc is stranded
        // between `partial` and `class` (de-indent + CS1587 doc detachment).
        var input = """
            namespace N
            {
                /// <summary>Doc.</summary>
                class MyExec : Executor
                {
                    [MessageHandler]
                    public System.Threading.Tasks.Task<int> Handle(string s) => null!;
                }
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input).Replace("\r\n", "\n");
        Assert.Contains("    partial class MyExec", output); // indented, class not stranded
        var docIdx = output.IndexOf("/// <summary>Doc", StringComparison.Ordinal);
        var partialIdx = output.IndexOf("partial class MyExec", StringComparison.Ordinal);
        Assert.True(docIdx >= 0 && docIdx < partialIdx, "doc comment must stay attached above the declaration");
    }

    [Fact]
    public void EnableSensitiveDataRewriter_RemovesNestedFlagInSurvivingSibling()
    {
        // The outer entry is removed AND the nested flag inside a surviving sibling
        // is cleaned in the same single pass (no fixpoint) — WM-22 review follow-up.
        var input = """
            class C {
                void M() {
                    var o = new AgentConfig {
                        EnableSensitiveData = true,
                        Telemetry = new TelemetryOptions { EnableSensitiveData = true },
                    };
                }
            }
            """;
        var output = ApplyRewriter(new EnableSensitiveDataRewriter(), input);
        Assert.DoesNotContain("EnableSensitiveData", output);          // BOTH flags gone
        Assert.Contains("Telemetry = new TelemetryOptions", output);   // the sibling itself survives
    }

    [Fact]
    public void MafAutoFixAll_Utf8BomWithInvalidBytes_SkippedWithError_NotCorrupted()
    {
        // A UTF-8-BOM file with an invalid byte must still be rejected (not U+FFFD'd)
        // — the strict decode must hold for BOM'd files too, not only BOM-less ones.
        var dir = Path.Combine(Path.GetTempPath(), "maf-bomstrict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // UTF-8-BOM + a WF-001 target + an invalid byte: proves the strict decode
            // holds for BOM'd files too (StreamReader's BOM path would otherwise swap in
            // a replacement-fallback decoder, U+FFFD the byte, then rewrite the file).
            var file = Path.Combine(dir, "MyExec.cs");
            var bytes = ExecutorSourceWithInvalidByte(utf8Bom: true);
            File.WriteAllBytes(file, bytes);
            var json = new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var doc = JsonSerializer.Deserialize<JsonDocument>(json)!;
            Assert.True(doc.RootElement.GetProperty("errors").GetArrayLength() >= 1, json);
            Assert.Equal(bytes, File.ReadAllBytes(file)); // byte-identical, no corruption
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MafAutoFixAll_UnreadableFile_RecordedAsError_PassContinues()
    {
        // An unreadable .cs (locked / permission-denied) must be a per-file error, not
        // a whole-pass abort — and every OTHER file must still be fixed. Forced via a
        // Windows exclusive-share lock or a POSIX chmod-000 so it runs on Linux CI too.
        var dir = Path.Combine(Path.GetTempPath(), "maf-unreadable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Good.cs"), """
                using System.Threading.Tasks;
                public class Executor { }
                public sealed class MessageHandlerAttribute : System.Attribute { }
                public class MyExec : Executor {
                  [MessageHandler]
                  public Task<int> Handle(string s) => Task.FromResult(0);
                }
                """);
            var bad = Path.Combine(dir, "Bad.cs");
            File.WriteAllText(bad, "public class Bad {}");

            FileStream? winLock = null;
            if (OperatingSystem.IsWindows())
            {
                winLock = new FileStream(bad, FileMode.Open, FileAccess.Read, FileShare.None); // exclusive
            }
            else
            {
                File.SetUnixFileMode(bad, UnixFileMode.None); // chmod 000
                // Running as root (common in CI containers) ignores the mode; then the
                // failure can't be forced, so skip rather than assert a false negative.
                try { using var probe = File.OpenRead(bad); File.SetUnixFileMode(bad, UnixFileMode.UserRead | UnixFileMode.UserWrite); return; }
                catch { /* good — genuinely unreadable */ }
            }

            try
            {
                var json = new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
                var doc = JsonSerializer.Deserialize<JsonDocument>(json)!;
                Assert.True(doc.RootElement.GetProperty("errors").GetArrayLength() >= 1, json); // unreadable file reported
                Assert.Contains("public partial class MyExec", File.ReadAllText(Path.Combine(dir, "Good.cs"))); // other file STILL fixed
            }
            finally
            {
                winLock?.Dispose();
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(bad, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(false)] // UTF-16 LE
    [InlineData(true)]  // UTF-16 BE
    public void MafAutoFixAll_PreservesUtf16Bom_OnApply(bool bigEndian)
    {
        // The UTF-16 detect+preserve branches must round-trip byte-faithfully: the BOM
        // (and endianness) survive and the file is NOT normalised to UTF-8.
        var dir = Path.Combine(Path.GetTempPath(), "maf-utf16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "U16.cs");
            var enc = new System.Text.UnicodeEncoding(bigEndian, byteOrderMark: true);
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class Executor { }
                public sealed class MessageHandlerAttribute : System.Attribute { }
                public class MyExec : Executor {
                  [MessageHandler]
                  public Task<int> Handle(string s) => Task.FromResult(0);
                }
                """, enc);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false); // WF-001 adds `partial`
            var bytes = File.ReadAllBytes(file);
            if (bigEndian) Assert.True(bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF, "UTF-16 BE BOM preserved");
            else Assert.True(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE, "UTF-16 LE BOM preserved");
            Assert.Contains("public partial class MyExec", File.ReadAllText(file, enc)); // rewrite applied, still UTF-16
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(false)] // UTF-32 LE (FF FE 00 00) — must NOT be misread as UTF-16 LE
    [InlineData(true)]  // UTF-32 BE (00 00 FE FF)
    public void MafAutoFixAll_PreservesUtf32Bom_OnApply(bool bigEndian)
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-utf32-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "U32.cs");
            var enc = new System.Text.UTF32Encoding(bigEndian, byteOrderMark: true);
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class Executor { }
                public sealed class MessageHandlerAttribute : System.Attribute { }
                public class MyExec : Executor {
                  [MessageHandler]
                  public Task<int> Handle(string s) => Task.FromResult(0);
                }
                """, enc);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var bytes = File.ReadAllBytes(file);
            if (bigEndian) Assert.True(bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF, "UTF-32 BE BOM preserved");
            else Assert.True(bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00, "UTF-32 LE BOM preserved");
            Assert.Contains("public partial class MyExec", File.ReadAllText(file, enc)); // decoded as UTF-32, rewrite applied
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ApplyLockPath_TrailingSeparator_MapsToSameLock()
    {
        // `C:\repo` and `C:\repo\` must resolve to ONE lock key, else two callers
        // spelling the path differently fail to serialise (WM-44).
        var dir = Path.Combine(Path.GetTempPath(), "maf-locktrim-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(AutoFixTool.ApplyLockPath(dir), AutoFixTool.ApplyLockPath(dir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ExecutorSealedRewriter_AttributedZeroModifierClass_StaysIndented()
    {
        // A zero-modifier Executor WITH an attribute: `partial` must inherit the
        // declaration's indentation, not de-indent to column 0.
        var input = """
            namespace N
            {
                [System.Obsolete]
                class Worker : Executor
                {
                    [MessageHandler]
                    public System.Threading.Tasks.Task<int> Handle(string s) => null!;
                }
            }
            """;
        var output = ApplyRewriter(new ExecutorSealedRewriter(), input).Replace("\r\n", "\n");
        Assert.Contains("    partial class Worker", output); // indented, not column 0
        Assert.DoesNotContain("\npartial", output);          // never at column 0
        Assert.Contains("[System.Obsolete]", output);               // attribute untouched
    }
}
