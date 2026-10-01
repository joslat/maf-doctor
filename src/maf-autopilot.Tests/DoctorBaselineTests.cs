using System.Text.Json;
using MafDoctor.Commands;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>F-03 — `doctor --baseline`: gate only on findings that are new since the baseline.</summary>
public sealed class DoctorBaselineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maf-baseline-" + Guid.NewGuid().ToString("N"));

    public DoctorBaselineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string OldFinding = """
        using Azure.Identity;
        using Microsoft.Agents.AI;
        public class Agents
        {
            public void Setup() { var cred = new DefaultAzureCredential(); }
        }
        """;

    private string Json(IReadOnlySet<string>? baseline, out DoctorSummary? summary) =>
        new DoctorTool().Run(_dir, "json", excludes: null, full: true, baseline, out summary);

    private string WriteBaseline()
    {
        var path = Path.Combine(_dir, "..", Path.GetFileName(_dir) + "-baseline.json");
        File.WriteAllText(path, Json(null, out _));
        return path;
    }

    [Fact]
    public void Baseline_SuppressesKnownFindings_AndGradesOnlyNewOnes()
    {
        File.WriteAllText(Path.Combine(_dir, "Agents.cs"), OldFinding);
        var baselinePath = WriteBaseline();
        try
        {
            var (fingerprints, error) = DoctorBaseline.Load(baselinePath);
            Assert.Null(error);
            Assert.NotEmpty(fingerprints!);

            // Unchanged repo: everything is known, nothing is new.
            Json(fingerprints, out var unchanged);
            Assert.Equal('A', unchanged!.Grade);
            Assert.Equal(1, unchanged.BaselineSuppressed);

            // The old finding moves down three lines (still baselined: drift-stable
            // fingerprint) and a NEW finding appears in another file.
            File.WriteAllText(Path.Combine(_dir, "Agents.cs"), "\n\n\n" + OldFinding);
            File.WriteAllText(Path.Combine(_dir, "More.cs"), OldFinding.Replace("class Agents", "class MoreAgents"));
            var output = Json(fingerprints, out var changed);

            Assert.Equal(1, changed!.BaselineSuppressed);
            Assert.Equal(1, changed.AntiPatternWarnings); // SEC-001 is a warning
            using var json = JsonDocument.Parse(output);
            Assert.Equal(1, json.RootElement.GetProperty("baseline_suppressed").GetInt32());
            var files = json.RootElement.GetProperty("top_fixes").EnumerateArray()
                .Select(f => f.GetProperty("file").GetString()).ToList();
            Assert.Equal(new[] { "More.cs" }, files);
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public void NoBaseline_LeavesTheJsonFieldAbsent()
    {
        File.WriteAllText(Path.Combine(_dir, "Agents.cs"), OldFinding);
        using var json = JsonDocument.Parse(Json(null, out var summary));
        Assert.Null(summary!.BaselineSuppressed);
        Assert.False(json.RootElement.TryGetProperty("baseline_suppressed", out var value) && value.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void Markdown_StatesHowManyFindingsTheBaselineMatched()
    {
        File.WriteAllText(Path.Combine(_dir, "Agents.cs"), OldFinding);
        var baselinePath = WriteBaseline();
        try
        {
            var (fingerprints, _) = DoctorBaseline.Load(baselinePath);
            var markdown = new DoctorTool().Run(_dir, "markdown", excludes: null, full: false, fingerprints, out _);
            Assert.Contains("**Baseline:** 1 finding(s) matched `--baseline`", markdown, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public void Load_RejectsMissingFilesAndOtherJsonShapes()
    {
        Assert.Contains("not found", DoctorBaseline.Load(Path.Combine(_dir, "missing.json")).Error, StringComparison.Ordinal);

        var sarif = Path.Combine(_dir, "results.sarif");
        File.WriteAllText(sarif, """{ "runs": [] }""");
        Assert.Contains("--all --json", DoctorBaseline.Load(sarif).Error, StringComparison.Ordinal);

        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.NotNull(DoctorBaseline.Load(broken).Error);
    }

    [Fact]
    public void Load_IgnoresValuesThatAreNotFingerprints()
    {
        var path = Path.Combine(_dir, "baseline.json");
        File.WriteAllText(path, """
            { "top_fixes": [ { "fingerprint": "0123456789ab" }, { "fingerprint": "NOT-A-PRINT" }, { "fingerprint": 7 }, {} ] }
            """);
        var (fingerprints, error) = DoctorBaseline.Load(path);
        Assert.Null(error);
        Assert.Equal(new[] { "0123456789ab" }, fingerprints!.ToArray());
    }

    [Fact]
    public void Cli_ParsesBaseline_AndRequiresAValue()
    {
        var (_, _, _, _, _, error, baseline) = DoctorCli.Parse(new[] { "doctor", ".", "--baseline", "old.json", "--fail-on", "C" });
        Assert.Null(error);
        Assert.Equal("old.json", baseline);

        var (_, _, _, _, _, missing, _) = DoctorCli.Parse(new[] { "doctor", ".", "--baseline", "--json" });
        Assert.Contains("--baseline requires a file", missing, StringComparison.Ordinal);
    }
}
