using System.Text.Json;
using MafDoctor.Commands;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Regression tests for the Phase 4a (reporting output core) findings:
/// SEC-02 (MdInline neutralizer), REP-27 (unknown-format error), REP-28 (banner
/// matches findings), REP-37 (drift-stable fingerprint), REP-39 (badge consumes
/// verdict JSON), REP-24 (autofix schema_version), REP-22 (SARIF error envelope).
/// </summary>
public sealed class Phase4aReportingTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-phase4a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // -------------------------------------------------------------------------
    // SEC-02 — MdInline neutralizes the inline-markdown escape metacharacters
    // -------------------------------------------------------------------------

    [Fact]
    public void MdInline_NeutralizesBacktickNewlineAndHtmlComment()
    {
        Assert.Equal("a'b", LlmFencing.MdInline("a`b"));            // backtick → apostrophe (can't close a span)
        Assert.Equal("a b", LlmFencing.MdInline("a\r\nb"));         // CR/LF collapsed to a space (can't break a row)
        Assert.Equal("ab", LlmFencing.MdInline("a<!--x-->b"));      // HTML comment stripped (no smuggling)
        Assert.Equal("normal/path.cs", LlmFencing.MdInline("normal/path.cs")); // ordinary path unchanged
        Assert.Equal(string.Empty, LlmFencing.MdInline(null));
    }

    // -------------------------------------------------------------------------
    // REP-27 — an unknown format fails loudly instead of silently falling to markdown
    // -------------------------------------------------------------------------

    [Fact]
    public void UnknownFormat_ReturnsStructuredError_NotMarkdown()
    {
        var dir = NewTempDir();
        try
        {
            var output = new DoctorTool().Run(dir, "xml", excludes: null);
            Assert.Contains("unknown format", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MAF health grade", output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-28 — the read-only banner matches what the scan found
    // -------------------------------------------------------------------------

    [Fact]
    public void CleanRepo_BannerSaysNothingToFix_NoAutofixSuggestion()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Clean.cs"), "public class Clean { }");
            var md = new DoctorTool().Run(dir, "markdown", excludes: null);
            Assert.Contains("Nothing needs fixing", md);
            Assert.DoesNotContain("autofix-all", md); // no no-op command suggested on a clean repo
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-37 — the finding fingerprint is present and survives a line shift
    // -------------------------------------------------------------------------

    [Fact]
    public void Fingerprint_IsPresentAndStable_AcrossLineShift()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "Prod.cs");
            File.WriteAllText(file, "using Microsoft.Agents.AI; public class Prod {\n    void M() { var c = new DefaultAzureCredential(); }\n}\n");
            var fp1 = FirstFingerprint(new DoctorTool().Run(dir, "json", excludes: null, full: true), "MAF-AP-SEC-001");
            Assert.False(string.IsNullOrEmpty(fp1));

            // Shift the finding down by prepending blank lines — the source line TEXT
            // is unchanged, so the drift-stable fingerprint must not move.
            File.WriteAllText(file, "using Microsoft.Agents.AI;\n\n\npublic class Prod {\n    void M() { var c = new DefaultAzureCredential(); }\n}\n");
            var fp2 = FirstFingerprint(new DoctorTool().Run(dir, "json", excludes: null, full: true), "MAF-AP-SEC-001");
            Assert.Equal(fp1, fp2);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string? FirstFingerprint(string json, string ruleId)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var f in doc.RootElement.GetProperty("top_fixes").EnumerateArray())
            if (f.GetProperty("rule_id").GetString() == ruleId)
                return f.GetProperty("fingerprint").GetString();
        return null;
    }

    // -------------------------------------------------------------------------
    // REP-39 — the badge reads the machine-readable verdict, not the prose headline
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"verdict\":\"B\"}", "B")]
    [InlineData("{\"verdict\":\"F\",\"errors_count\":4}", "F")]
    public void ExtractVerdict_ReadsVerdictField(string json, string expected)
        => Assert.Equal(expected, BadgeCommand.ExtractVerdict(json));

    [Theory]
    [InlineData("{\"error\":\"bad path\"}")]   // error object → no verdict
    [InlineData("not json at all")]            // unparseable
    [InlineData("[1,2,3]")]                     // wrong shape
    public void ExtractVerdict_ReturnsNull_OnErrorOrGarbage(string json)
        => Assert.Null(BadgeCommand.ExtractVerdict(json));

    // -------------------------------------------------------------------------
    // REP-24 — every AutoFix machine-readable surface is schema-versioned
    // -------------------------------------------------------------------------

    [Fact]
    public void AutoFixAll_Json_CarriesSchemaVersion_OnSuccessAndError()
    {
        var dir = NewTempDir();
        try
        {
            var ok = new AutoFixTool().MafAutoFixAll(dir); // dry-run success
            using (var doc = JsonDocument.Parse(ok))
                Assert.Equal("1", doc.RootElement.GetProperty("schema_version").GetString());

            var err = new AutoFixTool().MafAutoFixAll("relative/not/absolute"); // validation error
            using (var doc = JsonDocument.Parse(err))
            {
                Assert.Equal("1", doc.RootElement.GetProperty("schema_version").GetString());
                Assert.True(doc.RootElement.TryGetProperty("error", out _));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // REP-22 — format:"sarif" returns parseable SARIF (not plain text) on bad input
    // -------------------------------------------------------------------------

    [Fact]
    public void ScanAntiPatterns_SarifFormat_InvalidPath_ReturnsSarifErrorEnvelope()
    {
        var sarif = new AntiPatternScannerTool().MafScanAntiPatterns("relative/not/absolute", "sarif");

        using var doc = JsonDocument.Parse(sarif); // must be valid JSON, not a plain error string
        var root = doc.RootElement;
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var invocation = root.GetProperty("runs")[0].GetProperty("invocations")[0];
        Assert.False(invocation.GetProperty("executionSuccessful").GetBoolean());
        Assert.Equal("error",
            invocation.GetProperty("toolExecutionNotifications")[0].GetProperty("level").GetString());
    }
}
