using MafDoctor.Data;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for the pure parser core of Cs0618HuntTool. The shell-out to `dotnet build`
/// is intentionally NOT tested here — that's integration territory. The parser is
/// where regressions hide.
/// </summary>
public class Cs0618HuntToolTests
{
    [Fact]
    public void ParseBuildOutput_Empty_ReturnsEmpty()
    {
        Assert.Empty(Cs0618HuntTool.ParseBuildOutput(""));
        Assert.Empty(Cs0618HuntTool.ParseBuildOutput("   \n  \n  "));
    }

    [Fact]
    public void ParseBuildOutput_NoDiagnostics_ReturnsEmpty()
    {
        const string output = """
            Microsoft (R) Build Engine version 17.0.0
              Determining projects to restore...
              Restored.
              Build succeeded.
                0 Warning(s)
                0 Error(s)
            """;
        Assert.Empty(Cs0618HuntTool.ParseBuildOutput(output));
    }

    [Fact]
    public void ParseBuildOutput_OneCs0618_Warning_IsExtracted()
    {
        const string output =
            """
            C:\repo\src\Workflow.cs(42,17): warning CS0618: 'WorkflowBuilder.AddFanInBarrierEdge(ExecutorBinding, params ExecutorBinding[])' is obsolete: 'Use the (sources, target) overload instead.' [C:\repo\src\MyProj.csproj]
            """;

        var findings = Cs0618HuntTool.ParseBuildOutput(output);

        var d = Assert.Single(findings);
        Assert.Equal("CS0618", d.Code);
        Assert.Equal("warning", d.Severity);
        Assert.Equal(42, d.Line);
        Assert.Contains("AddFanInBarrierEdge", d.Message);
    }

    [Fact]
    public void ParseBuildOutput_Cs0246_Error_IsExtracted()
    {
        const string output =
            """
            /repo/src/Executor.cs(15,6): error CS0246: The type or namespace name 'StreamsMessageAttribute' could not be found
            """;

        var findings = Cs0618HuntTool.ParseBuildOutput(output);

        var d = Assert.Single(findings);
        Assert.Equal("CS0246", d.Code);
        Assert.Equal("error", d.Severity);
        Assert.Contains("StreamsMessageAttribute", d.Message);
    }

    [Fact]
    public void ParseBuildOutput_AllCsCodes_AreExtractedBeforeRegistryFiltering()
    {
        const string output = """
            /repo/src/A.cs(1,1): warning CS8600: Possible null reference
            /repo/src/B.cs(2,2): warning CS1591: Missing XML comment
            /repo/src/C.cs(3,3): warning CS0618: 'X' is obsolete: 'Use Y'
            """;

        var findings = Cs0618HuntTool.ParseBuildOutput(output);
        Assert.Equal(["CS8600", "CS1591", "CS0618"], findings.Select(d => d.Code));
    }

    [Fact]
    public void FilterRegistryRelevantDiagnostics_RetainsLegacyAndCorrelatedRegistryCodesOnly()
    {
        const string output = """
            /repo/src/A.cs(1,1): warning CS8600: Possible null reference
            /repo/src/B.cs(2,2): error CS1503: Argument 3 cannot convert from 'IList<AITool>' to 'IList<AIFunctionDeclaration>'
            /repo/src/C.cs(3,3): warning CS0618: 'X' is obsolete: 'Use Y'
            /repo/src/D.cs(4,4): error CS0246: The type or namespace name 'Gone' could not be found
            """;
        var registryEntries = new[]
        {
            new RegistryEntry
            {
                CsWarning = "CS1503",
                Type = "GitHubCopilotAgent",
                Method = "AsAIAgent",
                ObsoleteSignature = "AsAIAgent(IList<AITool> tools)",
                ReplacementSignature = "AsAIAgent(IList<AIFunctionDeclaration> tools)",
            },
            new RegistryEntry { CsWarning = "BINARY_BREAK" },
            new RegistryEntry { CsWarning = "not-a-code" },
        };

        var parsed = Cs0618HuntTool.ParseBuildOutput(output);
        var findings = Cs0618HuntTool.FilterRegistryRelevantDiagnostics(parsed, registryEntries);

        Assert.Equal(["CS1503", "CS0618", "CS0246"], findings.Select(d => d.Code));
    }

    [Fact]
    public void FilterRegistryRelevantDiagnostics_UnrelatedCs1503_IsExcluded()
    {
        var diagnostic = new BuildDiagnostic(
            File: "/repo/src/A.cs",
            Line: 1,
            Severity: "error",
            Code: "CS1503",
            Message: "Argument 1 cannot convert from 'CustomerOrder' to 'Invoice'");

        var findings = Cs0618HuntTool.FilterRegistryRelevantDiagnostics(
            [diagnostic],
            [new RegistryEntry
            {
                CsWarning = "CS1503",
                Type = "GitHubCopilotAgent",
                Method = "AsAIAgent",
                ObsoleteSignature = "AsAIAgent(IList<AITool> tools)",
                ReplacementSignature = "AsAIAgent(IList<AIFunctionDeclaration> tools)",
            }]);

        Assert.Empty(findings);
    }

    [Fact]
    public void FilterRegistryRelevantDiagnostics_MatchedCs1503_IsRetained()
    {
        var diagnostic = new BuildDiagnostic(
            File: "/repo/src/Agent.cs",
            Line: 12,
            Severity: "error",
            Code: "CS1503",
            Message: "Argument 3 cannot convert from 'System.Collections.Generic.IList<Microsoft.Extensions.AI.AITool>' to 'System.Collections.Generic.IList<GitHub.Copilot.SDK.AIFunctionDeclaration>'");
        var entry = new RegistryEntry
        {
            Id = "MAF1140-GITHUB-COPILOT-TOOLS-001",
            CsWarning = "CS1503",
            Type = "GitHubCopilotAgent",
            Method = "AsAIAgent",
            ObsoleteSignature = "AsAIAgent(IList<AITool> tools)",
            ReplacementSignature = "AsAIAgent(IList<AIFunctionDeclaration> tools)",
        };

        var findings = Cs0618HuntTool.FilterRegistryRelevantDiagnostics(
            [diagnostic],
            [entry]);

        Assert.Same(diagnostic, Assert.Single(findings));
        Assert.True(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(diagnostic, entry));
    }

    [Fact]
    public void FilterRegistryRelevantDiagnostics_NewCodeRequiresSameCodeEntry()
    {
        var diagnostic = new BuildDiagnostic(
            File: "/repo/src/Agent.cs",
            Line: 12,
            Severity: "error",
            Code: "CS1503",
            Message: "Argument 3 cannot convert from 'AITool' to 'AIFunctionDeclaration'");
        var wrongCodeEntry = new RegistryEntry
        {
            CsWarning = "CS1661",
            ObsoleteSignature = "AsAIAgent(IList<AITool> tools)",
            ReplacementSignature = "AsAIAgent(IList<AIFunctionDeclaration> tools)",
        };

        var findings = Cs0618HuntTool.FilterRegistryRelevantDiagnostics(
            [diagnostic],
            [wrongCodeEntry]);

        Assert.Empty(findings);
        Assert.False(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(
            diagnostic, wrongCodeEntry));
    }

    [Fact]
    public void ParseBuildOutput_DuplicatesAcrossTfms_AreDeduplicated()
    {
        // MSBuild emits the same diagnostic once per target framework.
        const string output = """
            /repo/src/A.cs(10,1): warning CS0618: 'X' is obsolete: 'Use Y'
            /repo/src/A.cs(10,1): warning CS0618: 'X' is obsolete: 'Use Y'
            /repo/src/A.cs(10,1): warning CS0618: 'X' is obsolete: 'Use Y'
            """;

        var findings = Cs0618HuntTool.ParseBuildOutput(output);
        Assert.Single(findings);
    }

    [Fact]
    public void ParseBuildOutput_MultipleDistinctDiagnostics_AreAllExtracted()
    {
        const string output = """
            /repo/A.cs(5,1): warning CS0618: 'A' is obsolete
            /repo/B.cs(6,1): warning CS0618: 'B' is obsolete
            /repo/C.cs(7,1): error CS0246: type 'C' not found
            """;

        var findings = Cs0618HuntTool.ParseBuildOutput(output).ToList();
        Assert.Equal(3, findings.Count);
    }

    [Fact]
    public void MatchToRegistry_KnownObsoleteSignature_FindsRegistryEntry()
    {
        var registry = new RegistryService();
        var diag = new BuildDiagnostic(
            File: "/repo/src/Workflow.cs",
            Line: 42,
            Severity: "warning",
            Code: "CS0618",
            Message: "'WorkflowBuilder.AddFanInBarrierEdge(ExecutorBinding, params ExecutorBinding[])' is obsolete");

        var match = Cs0618HuntTool.MatchToRegistry(diag, registry);

        Assert.NotNull(match);
        Assert.Contains("FAN-IN", match!.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchToRegistry_AgentThreadRemoval_FindsThreadEntry()
    {
        var registry = new RegistryService();
        var diag = new BuildDiagnostic(
            File: "/repo/src/Bot.cs",
            Line: 12,
            Severity: "error",
            Code: "CS0246",
            Message: "The type or namespace name 'AgentThread' could not be found");

        var match = Cs0618HuntTool.MatchToRegistry(diag, registry);
        Assert.NotNull(match);
        Assert.Contains("THREAD", match!.Id, StringComparison.OrdinalIgnoreCase);
    }

    // Q-02 link check: real diagnostics the compiler oracle saw, which used to link to
    // the first entry sharing the type instead of the entry for the member.
    [Theory]
    [InlineData("CS1061", "'AgentFileStore' does not contain a definition for 'DeleteFileAsync' and no accessible extension method 'DeleteFileAsync' accepting a first argument of type 'AgentFileStore' could be found (are you missing a using directive or an assembly reference?)", "MAF1130-FILESTORE-003")]
    [InlineData("CS0117", "'ChatClientAgentOptions' does not contain a definition for 'EnableNonApprovalRequiredFunctionBypassing'", "MAF114-OPTIONS-001")]
    [InlineData("CS0246", "The type or namespace name 'SessionIsolationKeyProvider' could not be found (are you missing a using directive or an assembly reference?)", "MAF118-HOSTING-PROVIDER-001")]
    [InlineData("CS0246", "The type or namespace name 'FunctionApprovalRequestContent' could not be found (are you missing a using directive or an assembly reference?)", "MAF130-APPROVAL-001")]
    public void MatchToRegistry_PrefersTheEntryForTheNamedMember(string code, string message, string expectedId)
    {
        var diag = new BuildDiagnostic("/repo/src/A.cs", 3, "error", code, message);

        var match = Cs0618HuntTool.MatchToRegistry(diag, new RegistryService());

        Assert.Equal(expectedId, match?.Id);
    }

    private static readonly RegistryEntry SearchEntry = new()
    {
        Id = "TEST-SEARCH-001",
        CsWarning = "CS1503",
        Type = "AgentFileStore",
        Method = "SearchFilesAsync",
        ObsoleteSignature = "SearchFilesAsync(string directory, string regexPattern, string? filePattern = null, System.Threading.CancellationToken cancellationToken = default)",
    };

    [Fact]
    public void Correlation_EquivalentCode_Counts()
    {
        // The entry names CS0029; the compiler reports CS0266 when an explicit cast exists.
        var entry = new RegistryEntry
        {
            CsWarning = "CS0029",
            Type = "AgentSkillsProvider",
            Method = "ReadOnlyToolsAutoApprovalRule",
            ObsoleteSignature = "System.Func<Microsoft.Extensions.AI.FunctionCallContent, System.Threading.Tasks.ValueTask<bool>> ReadOnlyToolsAutoApprovalRule { get; }",
            ReplacementSignature = "System.Func<Microsoft.Agents.AI.ToolAutoApprovalRuleContext, System.Threading.Tasks.ValueTask<bool>> ReadOnlyToolsAutoApprovalRule { get; }",
        };
        var diag = new BuildDiagnostic("/repo/A.cs", 1, "error", "CS0266",
            "Cannot implicitly convert type 'System.Func<Microsoft.Agents.AI.ToolAutoApprovalRuleContext, System.Threading.Tasks.ValueTask<bool>>' to 'System.Func<Microsoft.Extensions.AI.FunctionCallContent, System.Threading.Tasks.ValueTask<bool>>'. An explicit conversion exists (are you missing a cast?)");

        Assert.True(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(diag, entry));
        Assert.False(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(diag with { Code = "CS1503" }, entry));
    }

    [Fact]
    public void Correlation_NamespaceSegmentAlone_DoesNotCount_ButTheCallOnTheLineDoes()
    {
        var diag = new BuildDiagnostic("/repo/A.cs", 1, "error", "CS1503",
            "Argument 4: cannot convert from 'System.Threading.CancellationToken' to 'bool'");

        Assert.False(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(diag, SearchEntry));
        Assert.True(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(
            diag with { SourceLine = "var hits = await store.SearchFilesAsync(\"docs\", \"TODO\", \"*.md\", ct);" },
            SearchEntry));
    }

    [Fact]
    public void Correlation_SourceLine_IsIgnoredWhenTheMessageNamesAnApi()
    {
        // The message is about another API: the call on the line must not pull in this entry.
        var diag = new BuildDiagnostic("/repo/A.cs", 1, "error", "CS1503",
            "Argument 1: cannot convert from 'CustomerOrder' to 'Invoice'",
            SourceLine: "await store.SearchFilesAsync(Convert(order), \"x\");");

        Assert.False(Cs0618HuntTool.DiagnosticCorrelatesWithRegistryEntry(diag, SearchEntry));
    }

    [Fact]
    public void WithSourceLines_ReadsOnlyCsFilesUnderTheRoot()
    {
        var root = Directory.CreateTempSubdirectory("hunt-root-");
        var outside = Directory.CreateTempSubdirectory("hunt-outside-");
        try
        {
            var inside = Path.Combine(root.FullName, "Agent.cs");
            File.WriteAllLines(inside, ["// first", "await store.SearchFilesAsync(d, p, ct);"]);
            var foreign = Path.Combine(outside.FullName, "Other.cs");
            File.WriteAllLines(foreign, ["secret();"]);
            var notCs = Path.Combine(root.FullName, "notes.txt");
            File.WriteAllLines(notCs, ["text"]);

            var result = Cs0618HuntTool.WithSourceLines(
                [
                    new BuildDiagnostic(inside, 2, "error", "CS1503", "m"),
                    new BuildDiagnostic(foreign, 1, "error", "CS1503", "m"),
                    new BuildDiagnostic(notCs, 1, "error", "CS1503", "m"),
                    new BuildDiagnostic(inside, 99, "error", "CS1503", "m"),
                ],
                root.FullName);

            Assert.Equal("await store.SearchFilesAsync(d, p, ct);", result[0].SourceLine);
            Assert.Null(result[1].SourceLine);
            Assert.Null(result[2].SourceLine);
            Assert.Null(result[3].SourceLine);
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void Mcp_EmptyPath_ReturnsErrorMessage()
    {
        var tool = new Cs0618HuntTool(new RegistryService());
        var result = tool.MafRunCs0618Hunt("");
        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mcp_NonexistentPath_ReturnsErrorMessage()
    {
        var tool = new Cs0618HuntTool(new RegistryService());
        var result = tool.MafRunCs0618Hunt("/path/that/definitely/does/not/exist");
        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Phase I.2 — ResolveBuildTarget. The path-resolution logic that runs BEFORE
    // `dotnet build` is shelled. If this regresses, the user sees "could not
    // find a .csproj or .sln" even when one exists. Hermetic — uses a single
    // throwaway temp dir, no shell-outs.
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveBuildTarget_NonexistentPath_ReturnsNull()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-path-" + Guid.NewGuid().ToString("N"));

        // Act
        var resolved = Cs0618HuntTool.ResolveBuildTarget(path);

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveBuildTarget_FileWithUnsupportedExtension_ReturnsNull()
    {
        // Arrange — a .txt file that EXISTS on disk should not be accepted.
        using var fixture = new TempProjectDir();
        var notAProject = Path.Combine(fixture.Path, "notes.txt");
        File.WriteAllText(notAProject, "not a build target");

        // Act
        var resolved = Cs0618HuntTool.ResolveBuildTarget(notAProject);

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveBuildTarget_DirectoryWithCsproj_ReturnsThatCsproj()
    {
        // Arrange — directory containing a single .csproj file.
        using var fixture = new TempProjectDir();
        var csprojPath = Path.Combine(fixture.Path, "Sample.csproj");
        File.WriteAllText(csprojPath, "<Project />");

        // Act
        var resolved = Cs0618HuntTool.ResolveBuildTarget(fixture.Path);

        // Assert
        Assert.Equal(csprojPath, resolved);
    }

    [Fact]
    public void ResolveBuildTarget_DirectoryWithBothSlnAndCsproj_PrefersSln()
    {
        // Arrange — when both exist, .sln wins (covers solution-level builds).
        using var fixture = new TempProjectDir();
        var slnPath = Path.Combine(fixture.Path, "Sample.sln");
        var csprojPath = Path.Combine(fixture.Path, "Sample.csproj");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File");
        File.WriteAllText(csprojPath, "<Project />");

        // Act
        var resolved = Cs0618HuntTool.ResolveBuildTarget(fixture.Path);

        // Assert
        Assert.Equal(slnPath, resolved);
    }

    [Fact]
    public void ResolveBuildTarget_ExplicitCsprojFile_ReturnsItDirectly()
    {
        // Arrange — passing the .csproj path directly, not a directory.
        using var fixture = new TempProjectDir();
        var csprojPath = Path.Combine(fixture.Path, "Direct.csproj");
        File.WriteAllText(csprojPath, "<Project />");

        // Act
        var resolved = Cs0618HuntTool.ResolveBuildTarget(csprojPath);

        // Assert
        Assert.Equal(csprojPath, resolved);
    }

    /// <summary>
    /// Throwaway temp directory; auto-cleaned via IDisposable. Guid-suffixed so
    /// parallel test runs don't collide. Cleanup is best-effort — Windows file
    /// locks during test teardown should never fail a test.
    /// </summary>
    private sealed class TempProjectDir : IDisposable
    {
        public string Path { get; }
        public TempProjectDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "maf-cs0618-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
