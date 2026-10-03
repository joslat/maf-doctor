using System.Text.Json;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for the finding-confidence triage signal (certain / high / heuristic) that
/// drives false-positive triage in doctor / --plan / --json and the maf-remediate loop.
/// </summary>
public sealed class ConfidenceTriageTests
{
    [Theory]
    // Structural AST rules, low residual FP → high.
    [InlineData("MAF001", "high")]
    [InlineData("MAF-AP-SEC-003", "high")]
    [InlineData("MAF-AP-CONC-001", "high")]
    [InlineData("MAF-AP-CONC-002", "high")]
    [InlineData("MAF-AP-DEVUI-001", "high")]
    [InlineData("MAF-AP-EXEC-001", "high")]
    [InlineData("MAF-AP-AGENT-001", "high")]
    [InlineData("MAF-AP-SEC-001", "high")]
    [InlineData("MAF-AP-WF-001", "high")]
    [InlineData("MAF-AP-WF-002", "high")]
    [InlineData("MAF-AP-WF-003", "high")]
    [InlineData("MAF-AP-APPROVAL-001", "high")]
    // Name-only / text / scope-limited → heuristic (verify first).
    [InlineData("COST-001", "heuristic")]
    [InlineData("MAF-AP-SEC-002", "heuristic")]
    [InlineData("MAF-AP-OBS-001", "heuristic")]
    [InlineData("MAF-AP-MID-001", "heuristic")]
    [InlineData("PROMPT-004", "heuristic")]
    // Compiler ground-truth → certain. The branch is a CS06 PREFIX (not an exact
    // CS0618), so another CS06xx obsolete code is also certain; a non-CS06 code is not.
    [InlineData("CS0618", "certain")]
    [InlineData("CS0612", "certain")]
    [InlineData("CS1998", "heuristic")]
    public void ConfidenceFor_MapsRuleToExpectedTier(string ruleId, string expected)
    {
        Assert.Equal(expected, DoctorTool.ConfidenceFor(ruleId));
    }

    [Fact]
    public void ConfidenceFor_UnknownRule_DefaultsToHeuristic()
    {
        // Verify-first default so a new/unclassified rule errs toward human review.
        Assert.Equal("heuristic", DoctorTool.ConfidenceFor("MAF-AP-SOMETHING-NEW-999"));
    }

    [Fact]
    public void Json_CarriesConfidencePerFinding()
    {
        // A temp repo with one high-confidence finding (DEVUI-001) and one heuristic
        // (COST-001) — both must surface their confidence so a remediation loop can triage.
        using var repo = new TempRepo(
            ("Program.cs", """
                using Microsoft.Agents.AI.DevUI;
                public class Program
                {
                    public static async System.Threading.Tasks.Task Main(dynamic agent, dynamic app)
                    {
                        app.MapDevUI();
                        var r = await agent.RunAsync("hi");
                    }
                }
                """));

        var json = new DoctorTool().Run(repo.Path, format: "json", excludes: new List<string>(), full: true);
        using var doc = JsonDocument.Parse(json);
        var fixes = doc.RootElement.GetProperty("top_fixes");

        Assert.Contains(fixes.EnumerateArray(), f =>
            f.GetProperty("rule_id").GetString() == "MAF-AP-DEVUI-001"
            && f.GetProperty("confidence").GetString() == "high");
        Assert.Contains(fixes.EnumerateArray(), f =>
            f.GetProperty("rule_id").GetString() == "COST-001"
            && f.GetProperty("confidence").GetString() == "heuristic");
    }

    [Fact]
    public void Plan_FlagsHeuristicFindingsForVerification()
    {
        using var repo = new TempRepo(
            ("Program.cs", """
                public class Program
                {
                    public static async System.Threading.Tasks.Task Main(dynamic agent)
                    {
                        var r = await agent.RunAsync("hi");   // COST-001 — heuristic
                    }
                }
                """));

        var plan = new DoctorTool().Run(repo.Path, format: "plan", excludes: new List<string>(), full: true);
        Assert.Contains("heuristic", plan, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verify first", plan, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanJson_Manifest_PhasesFindingsAndTriage()
    {
        using var repo = new TempRepo(
            ("Program.cs", """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Program
                {
                    public static async System.Threading.Tasks.Task Main(dynamic agent)
                    {
                        var cred = new DefaultAzureCredential();   // MAF-AP-SEC-001 — auto-fixable
                        var r = await agent.RunAsync("hi");        // COST-001 — heuristic
                    }
                }
                """));

        var json = new DoctorTool().Run(repo.Path, format: "plan-json", excludes: new List<string>(), full: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1", root.GetProperty("schema_version").GetString());
        // Phase 1 batches the mechanical fix(es) into one command.
        var phase1 = root.GetProperty("phase1_autofix");
        // F-11: the suggested command must actually write when run verbatim —
        // autofix-all now previews by default, so the plan's command includes --apply.
        // REP-07: the command targets the scanned repo (quoted), not cwd '.'.
        Assert.Equal($"maf-doctor autofix-all \"{repo.Path}\" --apply", phase1.GetProperty("command").GetString());
        Assert.Contains(phase1.GetProperty("clears_rules").EnumerateArray(),
            e => e.GetString() == "MAF-AP-SEC-001");

        // Phase 2 carries per-finding confidence + verify_first for the loop to triage.
        var cost = root.GetProperty("phase2_semantic").EnumerateArray()
            .First(f => f.GetProperty("rule_id").GetString() == "COST-001");
        Assert.Equal("heuristic", cost.GetProperty("confidence").GetString());
        Assert.True(cost.GetProperty("verify_first").GetBoolean());
        Assert.NotEmpty(cost.GetProperty("occurrences").EnumerateArray());
    }

    [Fact]
    public void PlanJson_NothingAutoFixable_Phase1IsNull()
    {
        // Only a heuristic, non-auto-fixable finding (COST-001) — phase1_autofix must
        // serialize to JSON null while phase2 still carries the finding.
        using var repo = new TempRepo(
            ("Program.cs", """
                public class Program
                {
                    public static async System.Threading.Tasks.Task Main(dynamic agent)
                    {
                        var r = await agent.RunAsync("hi");   // COST-001 only — nothing mechanical
                    }
                }
                """));

        var json = new DoctorTool().Run(repo.Path, format: "plan-json", excludes: new List<string>(), full: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("phase1_autofix").ValueKind);
        Assert.Equal(0, root.GetProperty("counts").GetProperty("auto_fixable").GetInt32());
        Assert.Contains(root.GetProperty("phase2_semantic").EnumerateArray(),
            f => f.GetProperty("rule_id").GetString() == "COST-001");
    }

    [Fact]
    public void PlanJson_HighConfidenceFinding_VerifyFirstIsFalse()
    {
        // CONC-002 is high-confidence AND not auto-fixable → lands in phase2 with
        // confidence "high" and verify_first false (the negative branch of the flag).
        using var repo = new TempRepo(
            ("Demo.cs", """
                public class Demo
                {
                    public string M(dynamic client) => client.GetDataAsync().Result;   // CONC-002
                }
                """));

        var json = new DoctorTool().Run(repo.Path, format: "plan-json", excludes: new List<string>(), full: true);
        using var doc = JsonDocument.Parse(json);

        var conc = doc.RootElement.GetProperty("phase2_semantic").EnumerateArray()
            .First(f => f.GetProperty("rule_id").GetString() == "MAF-AP-CONC-002");
        Assert.Equal("high", conc.GetProperty("confidence").GetString());
        Assert.False(conc.GetProperty("verify_first").GetBoolean());
    }

    [Fact]
    public void PlanJson_InvalidPath_ReturnsJsonError()
    {
        var json = new DoctorTool().Run("C:/no/such/repo/xyz", format: "plan-json", excludes: new List<string>(), full: true);
        using var doc = JsonDocument.Parse(json); // must be valid JSON, not a plain-text string
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    /// <summary>A throwaway directory with seeded .cs files, deleted on dispose.</summary>
    private sealed class TempRepo : System.IDisposable
    {
        public string Path { get; }
        public TempRepo(params (string Name, string Content)[] files)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maf-conf-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
            foreach (var (name, content) in files)
                System.IO.File.WriteAllText(System.IO.Path.Combine(Path, name), content);
        }
        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
