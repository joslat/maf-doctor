using MafDoctor.Data;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Regression tests for the Phase 5 (human report surfaces) findings:
/// REP-14 (deletion-only PR verdict), REP-23 (cost scanner in PR audit),
/// REP-40 (starvation own bucket + canonical MAF001 fix), REP-15 (draft-issue
/// title/body split), REP-33 (single-line title from a multi-line symptom).
/// </summary>
public sealed class Phase5ReportSurfacesTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-phase5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // -------------------------------------------------------------------------
    // REP-14 — a deletion-only PR reports the deletions, not "0 of N + Ship it"
    // -------------------------------------------------------------------------

    [Fact]
    public void DeletionOnlyPr_ReportsDeletions_NotShipIt()
    {
        var repo = NewTempDir();
        try
        {
            // Files in the diff that don't exist on disk = deletions.
            var report = PullRequestAuditTool.BuildReport(repo, "main", new[] { "Gone.cs", "Also.cs" });
            Assert.Contains("only deletes", report);
            Assert.Contains("were deleted in this diff", report);
            Assert.DoesNotContain("Ship it", report);
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-23 — the PR audit includes the cost scanner (COST-001)
    // -------------------------------------------------------------------------

    [Fact]
    public void PrAudit_IncludesCostScanner()
    {
        var repo = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(repo, "Svc.cs"),
                "public class Svc { async System.Threading.Tasks.Task M(dynamic agent) { await agent.RunAsync(\"hi\"); } }");
            var report = PullRequestAuditTool.BuildReport(repo, "main", new[] { "Svc.cs" });
            Assert.Contains("COST-001", report);
            Assert.Contains("🟠 Cost", report); // the new summary-table column
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-40 — silent-starvation gets its own bucket + the canonical MAF001 fix
    // -------------------------------------------------------------------------

    [Fact]
    public void PrAudit_StarvationHasOwnBucket_AndCanonicalFix()
    {
        var repo = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(repo, "Wf.cs"),
                "using System.Threading.Tasks; public partial class Inv : Executor { [MessageHandler] public void H(string m) {} }");
            var report = PullRequestAuditTool.BuildReport(repo, "main", new[] { "Wf.cs" });
            Assert.Contains("🔴 Starvation risk", report);           // own label, not "❌ Error"
            Assert.Contains("declare the sent type", report); // DoctorTool.Maf001Fix canonical text
            Assert.Contains(DoctorTool.Maf001Fix, report);
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-15 — the draft issue splits the title out of the body
    // -------------------------------------------------------------------------

    [Fact]
    public void DraftIssue_SplitsTitleFromBody_ReviewerNoteAboveSentinel()
    {
        var repo = NewTempDir();
        try
        {
            var body = new DraftIssueTool(new RegistryService()).MafDraftIssue(repo, "AddFanInBarrierEdge throws NRE at runtime");
            const string sentinel = "----- ISSUE BODY BELOW THIS LINE -----";
            Assert.Contains("Suggested title", body);
            Assert.Contains(sentinel, body);
            Assert.DoesNotContain("## Title:", body);

            var sentinelIdx = body.IndexOf(sentinel, StringComparison.Ordinal);
            // The reviewer note is ABOVE the sentinel (never lands in the posted body);
            // the first body section is BELOW it.
            Assert.True(body.IndexOf("Review before posting", StringComparison.Ordinal) < sentinelIdx);
            Assert.True(body.IndexOf("### Symptom", StringComparison.Ordinal) > sentinelIdx);
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-33 — a multi-line symptom yields a single-line title
    // -------------------------------------------------------------------------

    [Fact]
    public void SuggestTitle_CollapsesMultilineAndTabWhitespace()
    {
        var title = DraftIssueTool.SuggestTitle("line one\n   line two\t\tline three.");
        Assert.DoesNotContain("\n", title);
        Assert.DoesNotContain("\t", title);
        Assert.Equal("line one line two line three", title);
    }
}
