using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MafDoctor.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Q-02 compiler oracle: <see cref="ExampleCompiler"/> compiles registry example
/// snippets against one side of a MAF train. The tests build two versions of a
/// small fake MAF assembly (old and new) in memory, so every behaviour the real
/// registry run depends on is pinned without restoring NuGet packages.
/// </summary>
public class ExampleCompilerTests : IClassFixture<ExampleCompilerTests.FakeTrain>
{
    private const string OldSource = """
        namespace Microsoft.Agents.AI.Fake
        {
            public class Session { }
            public class Agent
            {
                public string Run(string message, Session? session = null) => message;
                public string Name => "";
            }
            public abstract class Store { public abstract string Get(string id); }
            public class Options { public bool Legacy { get; set; } }
        }
        namespace Old.Place { public class Mover { } }
        namespace Ext.Place
        {
            public static class AgentExtensions { public static int Count(this Microsoft.Agents.AI.Fake.Agent agent) => 0; }
        }
        """;

    private const string NewSource = """
        namespace Microsoft.Agents.AI.Fake
        {
            public class Session { }
            public class Agent
            {
                public string Run(string message, Session? session = null) => message;
                public string Name => "";
            }
            public abstract class Store { public abstract string Get(string id, int version); }
            public class Options { }
            [System.Diagnostics.CodeAnalysis.Experimental("FAKE001")]
            public class Preview { }
        }
        namespace New.Place { public class Mover { } }
        namespace Ext.Place
        {
            public static class AgentExtensions { public static int Count(this Microsoft.Agents.AI.Fake.Agent agent) => 0; }
        }
        """;

    /// <summary>Emits the old and new fake assemblies once per test class.</summary>
    public sealed class FakeTrain : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-example-compiler-" + Guid.NewGuid().ToString("N"));

        public FakeTrain()
        {
            var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
            OldReferences = framework.Append(Emit("old", OldSource, framework)).ToList();
            NewReferences = framework.Append(Emit("new", NewSource, framework)).ToList();
            var probe = new ExampleCompiler(OldReferences);
            New = new ExampleCompiler(NewReferences, probe.TypeNames);
            Old = new ExampleCompiler(OldReferences, New.TypeNames);
        }

        public List<string> OldReferences { get; }
        public List<string> NewReferences { get; }
        internal ExampleCompiler Old { get; }
        internal ExampleCompiler New { get; }

        private string Emit(string side, string source, IEnumerable<string> framework)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, side)).FullName;
            var path = Path.Combine(dir, "Microsoft.Agents.AI.Fake.dll");
            var compilation = CSharpCompilation.Create(
                "Microsoft.Agents.AI.Fake",
                [CSharpSyntaxTree.ParseText(source)],
                framework.Select(p => MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            var result = compilation.Emit(path);
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private readonly FakeTrain _train;

    public ExampleCompilerTests(FakeTrain train) => _train = train;

    [Fact]
    public void UndeclaredLocals_AreTypedFromUsage()
    {
        var result = _train.Old.Compile("var reply = agent.Run(message, session);", "Agent");

        Assert.Equal("ok", result.Status);
        Assert.Contains(result.Inferred, i => i.StartsWith("agent: ", StringComparison.Ordinal) && i.EndsWith("Fake.Agent", StringComparison.Ordinal));
        Assert.Contains(result.Inferred, i => i == "message: string");
        Assert.Contains(result.Inferred, i => i.StartsWith("session: ", StringComparison.Ordinal) && i.EndsWith("Fake.Session", StringComparison.Ordinal));
    }

    [Fact]
    public void MemberMissingInThatVersion_IsAnError()
    {
        var result = _train.New.Compile("var options = new Options { Legacy = true };", "Options");

        Assert.Equal("fail", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("CS0117", StringComparison.Ordinal) && e.Contains("Legacy", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaimCheck_SeesTheBreakOldCodeHitsOnTheNewPackages()
    {
        var before = _train.Old.Compile("var options = new Options { Legacy = true };", "Options");
        Assert.Equal("ok", before.Status);

        var onNew = _train.New.Compile("var options = new Options { Legacy = true };", "Options", before.Prelude);

        Assert.Contains("CS0117", onNew.DiagnosticIds);
    }

    [Fact]
    public void SnippetUsings_DecideBetweenOldAndNewNamespaces()
    {
        const string before = "using Old.Place;\nvar mover = new Mover();";
        Assert.Equal("ok", _train.Old.Compile(before).Status);

        // The type moved namespaces: the old using must break on the new side
        // instead of silently binding to New.Place.Mover.
        var onNew = _train.New.Compile(before, fixedPrelude: _train.Old.Compile(before).Prelude);
        Assert.True(onNew.DiagnosticIds.Overlaps(["CS0246", "CS0234"]), string.Join(", ", onNew.DiagnosticIds));
        Assert.Equal("ok", _train.New.Compile("using New.Place;\nvar mover = new Mover();").Status);
    }

    [Fact]
    public void OverrideOnlySnippet_IsCheckedAgainstTheEntryType()
    {
        const string snippet = "public override string Get(string id) => id;";

        Assert.Equal("ok", _train.Old.Compile(snippet, "Store").Status);
        var onNew = _train.New.Compile(snippet, "Store");
        Assert.Equal("fail", onNew.Status);
        Assert.Contains(onNew.Errors, e => e.Contains("CS0115", StringComparison.Ordinal));
        Assert.Equal("ok", _train.New.Compile("public override string Get(string id, int version) => id;", "Store").Status);
    }

    [Fact]
    public void UserOwnTypes_AreUnresolvedNotFailures()
    {
        var result = _train.Old.Compile("var thing = new MyThing();\nvar reply = agent.Run(thing.ToString());", "Agent");

        Assert.Equal("unresolved", result.Status);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Unresolved, u => u.Contains("MyThing", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionMethod_NamespaceIsInferred()
    {
        var result = _train.Old.Compile("int count = agent.Count();", "AgentExtensions");

        Assert.True(result.Status == "ok", string.Join("; ", result.Errors.Concat(result.Unresolved).Concat(result.Inferred)));
        Assert.Contains(result.Inferred, i => i.Contains("using Ext.Place", StringComparison.Ordinal));
    }

    [Fact]
    public void MethodOnlyStatements_CompileAsAMethodBody()
    {
        // `using var` is not legal at script level, only in a method.
        var result = _train.Old.Compile("using var stream = new System.IO.MemoryStream();\nvar reply = agent.Run(\"hi\");", "Agent");

        Assert.True(result.Status == "ok", string.Join("; ", result.Errors.Concat(result.Unresolved)));
    }

    [Fact]
    public void LoopItems_AreTypedFromTheMembersTheLoopUses()
    {
        const string before = "foreach (var o in allOptions)\n    o.Legacy = true;";
        var onOld = _train.Old.Compile(before, "Options");
        Assert.Equal("ok", onOld.Status);

        // Typed, not dynamic: the removed member is seen on the new side.
        var onNew = _train.New.Compile(before, "Options", onOld.Prelude);
        Assert.Contains("CS1061", onNew.DiagnosticIds);
    }

    [Fact]
    public void ExperimentalDiagnostics_AreNotExampleErrors()
    {
        Assert.Equal("ok", _train.New.Compile("var preview = new Preview();", "Preview").Status);
    }

    [Theory]
    [InlineData("agent.Run(...);", "invalid")]
    [InlineData("// 1.2.3: rebuild only; no source change.", "empty")]
    public void NonCode_IsReportedAsSuch(string snippet, string status)
    {
        Assert.Equal(status, _train.Old.Compile(snippet, "Agent").Status);
    }

    [Fact]
    public void AssemblyNames_ExposeThePackagesOnASide()
    {
        Assert.Contains("Microsoft.Agents.AI.Fake", _train.Old.AssemblyNames);
        Assert.DoesNotContain("Microsoft.Agents.AI.Missing", _train.Old.AssemblyNames);
    }

    [Fact]
    public void Command_WithoutReferenceFiles_IsAUsageError()
    {
        Assert.Equal(2, VerifyExamplesCommand.Run(["verify-examples", "--version", "1.23.0"]));
        Assert.Equal(2, VerifyExamplesCommand.Run(["verify-examples", "--bogus"]));
    }

    [Fact]
    public void LinkCheck_PassesTheDiagnosticsThroughTheHunt()
    {
        // The diagnostic old code gets on MAF 1.13, as the compiler oracle reports it.
        var registry = new MafDoctor.Data.RegistryService();
        IReadOnlyList<(string Id, string Severity, string Message, string Line)> reported =
        [
            ("CS1061", "error",
             "'AgentFileStore' does not contain a definition for 'DeleteFileAsync' and no accessible extension method 'DeleteFileAsync' accepting a first argument of type 'AgentFileStore' could be found (are you missing a using directive or an assembly reference?)",
             "bool deleted = await store.DeleteFileAsync(\"notes/old.md\");"),
        ];

        Assert.Equal("ok", VerifyExamplesCommand.LinkCheck(registry.FindById("MAF1130-FILESTORE-003")!, reported, registry));
        Assert.StartsWith("the hunt links these diagnostics to MAF1130-FILESTORE-003",
            VerifyExamplesCommand.LinkCheck(registry.FindById("MAF1130-FILESTORE-001")!, reported, registry));
    }
}
