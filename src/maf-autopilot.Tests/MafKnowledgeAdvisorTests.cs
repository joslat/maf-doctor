using MafDoctor;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>ROADMAP U-02 — the status tool reports MAF knowledge freshness.</summary>
public class MafKnowledgeAdvisorTests
{
    private const string Index = """
        {"versions":["1.16.0","1.17.0","1.18.0-preview.1","1.18.0","1.9.0","1.23.0","1.20.0"]}
        """;

    [Fact]
    public void ParseStableVersions_DropsPrereleasesAndSortsSemantically()
    {
        Assert.Equal(
            new[] { "1.9.0", "1.16.0", "1.17.0", "1.18.0", "1.20.0", "1.23.0" },
            MafKnowledgeAdvisor.ParseStableVersions(Index));
    }

    [Fact]
    public void Build_CountsReleasesNewerThanTheRegistry()
    {
        var status = MafKnowledgeAdvisor.Build("1.17.0", MafKnowledgeAdvisor.ParseStableVersions(Index), fromCache: false);
        Assert.Equal("1.23.0", status.LatestMafVersion);
        Assert.Equal(new[] { "1.18.0", "1.20.0", "1.23.0" }, status.NewerReleases);
    }

    [Fact]
    public void Markdown_Behind_ShowsCountAndUpdateCommand()
    {
        var status = MafKnowledgeAdvisor.Build("1.17.0", MafKnowledgeAdvisor.ParseStableVersions(Index), fromCache: true);
        var md = MafKnowledgeAdvisor.BuildMarkdown(status, projectGap: null);
        Assert.Contains("**3 release(s) behind**", md, StringComparison.Ordinal);
        Assert.Contains("(cached)", md, StringComparison.Ordinal);
        Assert.Contains(CoverageGap.UpdateCommand, md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_Current_SaysSo()
    {
        var status = MafKnowledgeAdvisor.Build("1.23.0", MafKnowledgeAdvisor.ParseStableVersions(Index), fromCache: false);
        Assert.Contains("✅ Current with the latest stable MAF (`1.23.0`)", MafKnowledgeAdvisor.BuildMarkdown(status, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_ErrorAndDisabled_NeverClaimCurrent()
    {
        var failed = new MafKnowledgeStatus("1.17.0", null, [], false, "timeout", Disabled: false);
        var disabled = new MafKnowledgeStatus("1.17.0", null, [], false, null, Disabled: true);
        Assert.Contains("Could not check nuget.org", MafKnowledgeAdvisor.BuildMarkdown(failed, null), StringComparison.Ordinal);
        Assert.Contains("MAF_DOCTOR_UPDATE_CHECK", MafKnowledgeAdvisor.BuildMarkdown(disabled, null), StringComparison.Ordinal);
        Assert.DoesNotContain("✅", MafKnowledgeAdvisor.BuildMarkdown(failed, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_IncludesTheWorkspaceCoverageGap()
    {
        var status = MafKnowledgeAdvisor.Build("1.17.0", MafKnowledgeAdvisor.ParseStableVersions(Index), fromCache: false);
        var gap = new CoverageGap("1.17.0", "1.23.0", []);
        Assert.Contains("Coverage horizon", MafKnowledgeAdvisor.BuildMarkdown(status, gap), StringComparison.Ordinal);
    }
}
