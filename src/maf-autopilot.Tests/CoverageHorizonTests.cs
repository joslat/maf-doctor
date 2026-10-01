using System.Text.Json;
using MafDoctor.Data;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// ROADMAP U-01 — a project on a newer MAF than the shipped registry covers must be
/// told "UNKNOWN", never "SAFE" (the registry stalled at MAF 1.17 while upstream
/// shipped 1.23, and MafApiSafety kept answering "✅ SAFE … in MAF 1.17.0").
/// </summary>
public class CoverageHorizonTests
{
    private const string FarFuture = "99.0.0";

    [Theory]
    [InlineData("1.23.0", "1.23.0")]
    [InlineData("1.23.0-preview.260928.1", "1.23.0")]
    [InlineData("1.23.0-alpha.1", "1.23.0")]
    [InlineData("[1.23.0, )", "1.23.0")]
    [InlineData("1.20", "1.20.0")]
    public void TryParseTrain_ReadsTheReleaseTrain(string text, string expected)
    {
        Assert.True(CoverageHorizon.TryParseTrain(text, out var train));
        Assert.Equal(System.Version.Parse(expected), train);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(unpinned)")]
    [InlineData("1.*")]
    [InlineData("(, 1.5.0]")]
    [InlineData("$(MafVersion)")]
    public void TryParseTrain_RejectsUnparseableVersions(string text)
    {
        Assert.False(CoverageHorizon.TryParseTrain(text, out _));
    }

    [Fact]
    public void Evaluate_ProjectNewerThanRegistry_ReportsGap()
    {
        using var repo = new TempRepo(("App.csproj", Csproj(("Microsoft.Agents.AI", "1.23.0"))));
        var gap = CoverageHorizon.Evaluate(repo.Path, "1.17.0");
        Assert.NotNull(gap);
        Assert.Equal("1.23.0", gap!.ProjectMafVersion);
        Assert.Equal("1.17.0", gap.RegistryMafVersion);
        Assert.Contains(gap.NewerPackages, p => p.Id == "Microsoft.Agents.AI");
        Assert.Contains("UNKNOWN", gap.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ProjectWithinRegistry_ReportsNoGap()
    {
        using var repo = new TempRepo(("App.csproj", Csproj(("Microsoft.Agents.AI", "1.17.0"))));
        Assert.Null(CoverageHorizon.Evaluate(repo.Path, "1.18.0"));
    }

    [Fact]
    public void Evaluate_ReadsCentralPackageManagementPins()
    {
        using var repo = new TempRepo(
            ("App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><PackageReference Include="Microsoft.Agents.AI.Workflows" /></ItemGroup>
                </Project>
                """),
            ("Directory.Packages.props", """
                <Project>
                  <ItemGroup><PackageVersion Include="Microsoft.Agents.AI.Workflows" Version="1.20.0" /></ItemGroup>
                </Project>
                """));
        Assert.Equal("1.20.0", CoverageHorizon.Evaluate(repo.Path, "1.17.0")?.ProjectMafVersion);
    }

    [Fact]
    public void Evaluate_IgnoresNonMafLookalikesAndUpperBounds()
    {
        using var repo = new TempRepo(("App.csproj", Csproj(
            ("Microsoft.Agents.AIExtras", FarFuture),       // not a MAF package
            ("Microsoft.Agents.AI", "[1.10.0, 2.0.0)"),      // range: lower bound is in use
            ("Microsoft.Agents.AI.Hosting", "$(HostingVersion)"))));
        Assert.Null(CoverageHorizon.Evaluate(repo.Path, "1.17.0"));
    }

    [Fact]
    public void Evaluate_TakesTheHighestOfDisagreeingPins()
    {
        using var repo = new TempRepo(
            ("A.csproj", Csproj(("Microsoft.Agents.AI", "1.12.0"))),
            ("B.csproj", Csproj(("Microsoft.Agents.AI", "1.21.0-preview.1"))));
        Assert.Equal("1.21.0", CoverageHorizon.Evaluate(repo.Path, "1.17.0")?.ProjectMafVersion);
    }

    [Fact]
    public void ApiSafety_WithRepoPastTheHorizon_SaysUnknownNotSafe()
    {
        using var repo = new TempRepo(("App.csproj", Csproj(("Microsoft.Agents.AI", FarFuture))));
        var result = new ApiSafetyTool(new RegistryService()).MafApiSafety("zzzNotAnApiAnywhere", repo.Path);
        Assert.Contains("UNKNOWN", result, StringComparison.Ordinal);
        Assert.DoesNotContain("✅ SAFE", result, StringComparison.Ordinal);
        Assert.Contains(CoverageGap.UpdateCommand, result, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiSafety_WithoutRepo_StatesTheCoverageLimit()
    {
        var registry = new RegistryService();
        var result = new ApiSafetyTool(registry).MafApiSafety("zzzNotAnApiAnywhere");
        Assert.Contains("✅ SAFE", result, StringComparison.Ordinal);
        Assert.Contains($"Coverage ends at MAF {registry.TargetVersion}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiSafety_WithInvalidRepoPath_ReturnsTheGuardError()
    {
        var result = new ApiSafetyTool(new RegistryService()).MafApiSafety("AgentThread", "relative/path");
        Assert.StartsWith("Error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_PastTheHorizon_AddsBannerAndJsonField()
    {
        using var repo = new TempRepo(
            ("App.csproj", Csproj(("Microsoft.Agents.AI", FarFuture))),
            ("Program.cs", "class Program { static void Main() { } }"));
        var doctor = new DoctorTool();

        Assert.Contains("Coverage horizon", doctor.MafDoctor(repo.Path), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(doctor.MafDoctor(repo.Path, "json"));
        var gap = json.RootElement.GetProperty("coverage_gap");
        Assert.Equal(FarFuture, gap.GetProperty("project_maf_version").GetString());
        Assert.Equal(CoverageGap.UpdateCommand, gap.GetProperty("update_command").GetString());
    }

    [Fact]
    public void Doctor_WithoutMafReferences_HasNullCoverageGap()
    {
        using var repo = new TempRepo(("Program.cs", "class Program { static void Main() { } }"));
        using var json = JsonDocument.Parse(new DoctorTool().MafDoctor(repo.Path, "json"));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("coverage_gap").ValueKind);
    }

    private static string Csproj(params (string Id, string Version)[] packages)
    {
        var refs = string.Concat(packages.Select(p => $"""<PackageReference Include="{p.Id}" Version="{p.Version}" />"""));
        return $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{refs}</ItemGroup></Project>""";
    }

    private sealed class TempRepo : System.IDisposable
    {
        public string Path { get; }
        public TempRepo(params (string Name, string Content)[] files)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maf-horizon-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
            foreach (var (name, content) in files)
                System.IO.File.WriteAllText(System.IO.Path.Combine(Path, name), content);
        }
        public void Dispose() { try { System.IO.Directory.Delete(Path, recursive: true); } catch { } }
    }
}
