using System.Text.Json;
using MafDoctor;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Regression tests for the Phase 4b (SARIF fidelity + data-correctness) findings:
/// REP-20 (real SARIF version), REP-11 (help Why/Fix), REP-12 (partialFingerprints),
/// REP-01 (version ladder from the maintained Matrix + drift lock), REP-02 (dotnet-inspect
/// failure surfaced), REP-03 (FanOut relative paths).
/// </summary>
public sealed class Phase4bSarifTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-phase4b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonDocument AntiPatternSarif()
    {
        var findings = AntiPatternScannerTool.ScanFile(
            "using Microsoft.Agents.AI; public class Prod { void M() { var c = new DefaultAzureCredential(); } }", "Prod.cs");
        return JsonDocument.Parse(SarifExportTool.EmitAntiPatternsSarif(findings));
    }

    // -------------------------------------------------------------------------
    // REP-20 — SARIF driver.version tracks the real installed version
    // -------------------------------------------------------------------------

    [Fact]
    public void Sarif_DriverVersion_IsRealToolVersion_NotFrozenAlpha()
    {
        using var doc = AntiPatternSarif();
        var version = doc.RootElement.GetProperty("runs")[0]
            .GetProperty("tool").GetProperty("driver").GetProperty("version").GetString();

        Assert.Equal(ToolVersionInfo.Current, version);
        Assert.NotEqual("1.3.0-alpha-3", version);
    }

    // -------------------------------------------------------------------------
    // REP-11 — SARIF rules carry the CLI's Why/Fix guidance in help.markdown
    // -------------------------------------------------------------------------

    [Fact]
    public void Sarif_Rules_CarryWhyAndFixHelp()
    {
        using var doc = AntiPatternSarif();
        var rule = doc.RootElement.GetProperty("runs")[0].GetProperty("tool")
            .GetProperty("driver").GetProperty("rules").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == "MAF-AP-SEC-001");

        var helpMd = rule.GetProperty("help").GetProperty("markdown").GetString();
        Assert.Contains("**Why:**", helpMd);
        Assert.Contains("**Fix:**", helpMd);
    }

    // -------------------------------------------------------------------------
    // REP-12 — SARIF results carry a stable partialFingerprint
    // -------------------------------------------------------------------------

    [Fact]
    public void Sarif_Results_CarryStablePartialFingerprint()
    {
        using var doc = AntiPatternSarif();
        var result = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        var fp = result.GetProperty("partialFingerprints").GetProperty("mafDoctorFingerprint/v1").GetString();
        Assert.False(string.IsNullOrEmpty(fp));

        // Deterministic: the same finding hashes to the same fingerprint every run.
        using var doc2 = AntiPatternSarif();
        var fp2 = doc2.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("partialFingerprints").GetProperty("mafDoctorFingerprint/v1").GetString();
        Assert.Equal(fp, fp2);
    }

    // -------------------------------------------------------------------------
    // REP-01 — the version ladder is derived from the maintained Matrix (drift-locked)
    // -------------------------------------------------------------------------

    [Fact]
    public void KnownVersions_EqualsCompatibilityMatrixKeys()
    {
        Assert.True(new HashSet<string>(RegressionPlanTool.KnownVersions)
            .SetEquals(CompatibilityTool.Matrix.Keys));
        // And it now reaches the current release, not the frozen 1.5.0 ceiling.
        Assert.Contains("1.13.0", RegressionPlanTool.KnownVersions);
    }

    // -------------------------------------------------------------------------
    // REP-02 — a dotnet-inspect failure is surfaced, never parsed as "0 changes"
    // -------------------------------------------------------------------------

    [Fact]
    public void DescribeInspectFailure_NotInstalled_IsVerbatim_OtherOutput_IsFenced()
    {
        var notInstalled = "dotnet tool install --global dotnet-inspect --version 0.9.1 to enable this.";
        var failVerbatim = DiffPackageTool.DescribeInspectFailure(1, notInstalled);
        Assert.Contains("Error: dotnet-inspect failed", failVerbatim);
        Assert.Contains(notInstalled, failVerbatim);
        Assert.DoesNotContain("BEGIN_USER_DATA", failVerbatim); // not fenced — it's tool text

        var realStderr = "Unhandled exception: System.Exception at Package.Load()";
        var fenced = DiffPackageTool.DescribeInspectFailure(1, realStderr);
        Assert.Contains("BEGIN_USER_DATA", fenced); // attacker-influenceable stdout is fenced

        var unrecognized = DiffPackageTool.DescribeInspectFailure(0, "weird output", unrecognized: true);
        Assert.Contains("unrecognized", unrecognized);
    }

    // -------------------------------------------------------------------------
    // REP-03 — MafValidateFanOut emits repo-relative paths, not absolute host paths
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateFanOut_EmitsRelativePaths_NotAbsoluteHostPaths()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Wf.cs"), """
                using System.Threading.Tasks;
                public partial class Inv : Executor
                {
                    [MessageHandler]
                    public ValueTask AlphaAsync(string m) => default;
                }
                """);
            var md = new FanOutValidatorTool().MafValidateFanOut(dir);
            Assert.Contains("Wf.cs", md);
            Assert.DoesNotContain(dir, md); // the absolute host path must not leak
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
