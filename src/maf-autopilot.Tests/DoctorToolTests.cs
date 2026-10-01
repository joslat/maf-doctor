using System.Text.Json;
using MafDoctor.Tools;
using Xunit;

namespace MafDoctor.Tests;

public class DoctorToolTests
{
    // -------------------------------------------------------------------------
    // Grade boundaries
    // -------------------------------------------------------------------------

    [Fact]
    public void Grade_Clean_ReturnsA()
    {
        var summary = DoctorTool.Grade([], []);
        Assert.Equal('A', summary.Grade);
        Assert.Empty(summary.TopFixes);
    }

    [Fact]
    public void Grade_OneWarning_NoErrors_ReturnsB()
    {
        var antiPatterns = new[]
        {
            new AntiPatternFinding("MAF-AP-OBS-001", "name", AntiPatternSeverity.Warning, "f", 1, "m"),
        };
        var summary = DoctorTool.Grade(antiPatterns, []);
        Assert.Equal('B', summary.Grade);
    }

    [Fact]
    public void Grade_OneError_ReturnsC()
    {
        var antiPatterns = new[]
        {
            new AntiPatternFinding("MAF-AP-SEC-001", "name", AntiPatternSeverity.Error, "f", 1, "m"),
        };
        var summary = DoctorTool.Grade(antiPatterns, []);
        Assert.Equal('C', summary.Grade);
        Assert.NotEmpty(summary.TopFixes);
    }

    [Fact]
    public void Grade_FourErrors_ReturnsF()
    {
        var antiPatterns = Enumerable.Range(0, 4)
            .Select(i => new AntiPatternFinding($"R{i}", "n", AntiPatternSeverity.Error, "f", i, "m"))
            .ToList();
        var summary = DoctorTool.Grade(antiPatterns, []);
        Assert.Equal('F', summary.Grade);
    }

    [Fact]
    public void Grade_TwoStarvationRisks_ReturnsF()
    {
        var handlers = new[]
        {
            new MessageHandlerFinding("f", 1, "H1", "void", FanOutVerdict.SilentStarvationRisk),
            new MessageHandlerFinding("f", 2, "H2", "Task", FanOutVerdict.SilentStarvationRisk),
        };
        var summary = DoctorTool.Grade([], handlers);
        Assert.Equal('F', summary.Grade);
        Assert.Equal(2, summary.SilentStarvationRisks);
    }

    [Fact]
    public void Grade_OneStarvationRisk_ReturnsC()
    {
        var handlers = new[]
        {
            new MessageHandlerFinding("f", 1, "H1", "void", FanOutVerdict.SilentStarvationRisk),
        };
        var summary = DoctorTool.Grade([], handlers);
        Assert.Equal('C', summary.Grade);
    }

    // -------------------------------------------------------------------------
    // Top-fix prioritisation
    // -------------------------------------------------------------------------

    [Fact]
    public void TopFixes_PrioritisesStarvationOverErrors()
    {
        var antiPatterns = new[]
        {
            new AntiPatternFinding("MAF-AP-SEC-001", "Sec rule", AntiPatternSeverity.Error, "src/A.cs", 5, "match"),
        };
        var handlers = new[]
        {
            new MessageHandlerFinding("src/W.cs", 10, "VoidHandler", "void", FanOutVerdict.SilentStarvationRisk),
        };

        var summary = DoctorTool.Grade(antiPatterns, handlers);

        // First fix should be the starvation risk (priority 1), not the security error (priority 2).
        Assert.Equal("MafValidateFanOut", summary.TopFixes[0].Source);
        Assert.Equal("MafScanAntiPatterns", summary.TopFixes[1].Source);
    }

    [Fact]
    public void TopFixes_Caps3()
    {
        var antiPatterns = Enumerable.Range(0, 10)
            .Select(i => new AntiPatternFinding($"R{i}", "n", AntiPatternSeverity.Error, "f", i, "m"))
            .ToList();
        var summary = DoctorTool.Grade(antiPatterns, []);
        Assert.Equal(3, summary.TopFixes.Count);
        // --all / full surfaces EVERY finding, not just the top 3.
        Assert.Equal(10, summary.AllFixes.Count);
    }

    [Fact]
    public void Full_ReportListsEveryFinding()
    {
        // 6 distinct anti-pattern errors → default shows 3, full shows all 6.
        var antiPatterns = Enumerable.Range(0, 6)
            .Select(i => new AntiPatternFinding($"MAF-AP-R{i}", $"rule {i}", AntiPatternSeverity.Error, $"File{i}.cs", i + 1, "m"))
            .ToList();
        var summary = DoctorTool.Grade(antiPatterns, []);
        Assert.Equal(3, summary.TopFixes.Count);
        Assert.Equal(6, summary.AllFixes.Count);
        // Every finding carries a one-line Description (what the --all report prints).
        Assert.All(summary.AllFixes, f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
    }

    // -------------------------------------------------------------------------
    // MCP tool entry
    // -------------------------------------------------------------------------

    [Fact]
    public void MafDoctor_EmptyPath_ReturnsError()
    {
        var tool = new DoctorTool();
        Assert.Contains("Error", tool.MafDoctor(""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MafDoctor_NonexistentDirectory_ReturnsError()
    {
        var tool = new DoctorTool();
        Assert.Contains("Error", tool.MafDoctor("/path/that/definitely/does/not/exist"),
            StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Phase I.4 — Composition: Grade() over findings from every real scanner
    // running on the same source. Catches breakage in the 4-scanner fan-in
    // (anti-pattern + fan-out + prompt + cost). Hermetic — string sources only.
    // -------------------------------------------------------------------------

    [Fact]
    public void Grade_AggregatesFromAllFourRealScanners_SurfacesSecurityError()
    {
        // Arrange — source that legitimately trips MAF-AP-SEC-001 (DefaultAzureCredential).
        const string source = """
            using Azure.Identity; using Microsoft.Agents.AI;
            public class BadAgent
            {
                public BadAgent()
                {
                    var cred = new DefaultAzureCredential();
                }
            }
            """;

        // Act — run every scanner pure on the source, then aggregate.
        var antiPatterns = AntiPatternScannerTool.ScanFile(source, "src/BadAgent.cs");
        var handlers = FanOutValidatorTool.AnalyzeSource(source, "src/BadAgent.cs");
        var prompts = PromptLintTool.LintSource(source, "src/BadAgent.cs");
        var costs = EstimateCostTool.AnalyzeSource(source, "src/BadAgent.cs");

        var summary = DoctorTool.Grade(antiPatterns, handlers, prompts, costs);

        // Assert — the security error propagates to the grade AND the top-fix list.
        Assert.True(summary.AntiPatternErrors >= 1,
            "DefaultAzureCredential should have tripped MAF-AP-SEC-001 — composition is broken.");
        Assert.NotEqual('A', summary.Grade);
        Assert.Contains(summary.TopFixes,
            fix => fix.Description.Contains("MAF-AP-SEC-001", StringComparison.Ordinal));
    }

    [Fact]
    public void Grade_CleanSource_AllFourScanners_StaysAGrade()
    {
        // Arrange — non-MAF source with no patterns any scanner cares about.
        const string source = """
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        // Act
        var antiPatterns = AntiPatternScannerTool.ScanFile(source, "src/Calculator.cs");
        var handlers = FanOutValidatorTool.AnalyzeSource(source, "src/Calculator.cs");
        var prompts = PromptLintTool.LintSource(source, "src/Calculator.cs");
        var costs = EstimateCostTool.AnalyzeSource(source, "src/Calculator.cs");

        var summary = DoctorTool.Grade(antiPatterns, handlers, prompts, costs);

        // Assert
        Assert.Equal('A', summary.Grade);
        Assert.Empty(summary.TopFixes);
    }

    // -------------------------------------------------------------------------
    // JSON output format (format: "json")
    // -------------------------------------------------------------------------

    [Fact]
    public void MafDoctor_JsonFormat_ReturnsValidJson()
    {
        var tool = new DoctorTool();
        // Use a path that exists but has no MAF .cs files — should return grade A with valid JSON.
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-json-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var output = tool.MafDoctor(tempDir, format: "json");

            // Must parse as valid JSON
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            // Required fields
            Assert.Equal("1", root.GetProperty("schema_version").GetString());
            var verdict = root.GetProperty("verdict").GetString();
            Assert.Contains(verdict, new[] { "A", "B", "C", "F" });
            Assert.True(root.TryGetProperty("errors_count", out _));
            Assert.True(root.TryGetProperty("warnings_count", out _));
            Assert.True(root.TryGetProperty("silent_starvation_risks", out _));
            Assert.True(root.TryGetProperty("top_fixes", out _));
            Assert.True(root.TryGetProperty("summary_md", out _));

            // Grade A for empty dir
            Assert.Equal("A", verdict);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MafDoctor_JsonFormat_DefaultIsMarkdown()
    {
        var tool = new DoctorTool();
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-md-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Default call (no format param) should return markdown, not JSON
            var output = tool.MafDoctor(tempDir);
            Assert.Contains("MAF health grade", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"schema_version\"", output);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MafDoctor_JsonFormat_TopFixesHasDiscreteFields()
    {
        // Arrange a source that trips MAF-AP-SEC-001
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-fields-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Bad
                {
                    public Bad() { var c = new DefaultAzureCredential(); }
                }
                """);
            var tool = new DoctorTool();
            var output = tool.MafDoctor(tempDir, format: "json");

            using var doc = JsonDocument.Parse(output);
            var fixes = doc.RootElement.GetProperty("top_fixes");
            Assert.True(fixes.GetArrayLength() > 0, "Expected at least one top_fix");
            var fix = fixes[0];

            Assert.Equal("MAF-AP-SEC-001", fix.GetProperty("rule_id").GetString());
            Assert.Contains("Bad.cs", fix.GetProperty("file").GetString() ?? "");
            Assert.True(fix.GetProperty("line").GetInt32() > 0);
            Assert.False(string.IsNullOrEmpty(fix.GetProperty("issue").GetString()));
            Assert.False(string.IsNullOrEmpty(fix.GetProperty("fix_description").GetString()));
            Assert.True(fix.GetProperty("auto_fixable").GetBoolean());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Phase 3.1 — path exclusion. The drift detector scans the repo, which
    // ships intentional anti-pattern bait under samples/ + test fixtures.
    // `Run(..., excludes)` must skip those so the grade reflects product code.
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Tier 1 — richer suggestions: Why + Fix + autofix hint + source line in the
    // human markdown report (previously the fix string lived only in JSON), plus
    // rule-grouped --all output and a `why` field in JSON.
    // -------------------------------------------------------------------------

    [Fact]
    public void Markdown_Report_ShowsWhy_Fix_SourceLine_AndAutofixHint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Bad
                {
                    public Bad() { var c = new DefaultAzureCredential(); }
                }
                """);
            var output = new DoctorTool().MafDoctor(tempDir); // markdown (default)

            Assert.Contains("**Why:**", output, StringComparison.Ordinal);
            Assert.Contains("**Fix:**", output, StringComparison.Ordinal);
            // Autofix one-liner — MAF-AP-SEC-001 is auto-fixable.
            Assert.Contains("auto-fixable", output, StringComparison.Ordinal);
            Assert.Contains("autofix-all", output, StringComparison.Ordinal);
            // The offending SOURCE line is shown (only the source — not the rule name —
            // contains the `new` keyword), proving we read the file, not just the location.
            Assert.Contains("new DefaultAzureCredential", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void AllReport_GroupsRepeatedRule_WithCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Same rule (MAF-AP-SEC-001) in two files → one group ×2 in --all.
            foreach (var name in new[] { "A.cs", "B.cs" })
                File.WriteAllText(Path.Combine(tempDir, name), $$"""
                    using Azure.Identity; using Microsoft.Agents.AI;
                    public class {{name[..1]}}x
                    {
                        public {{name[..1]}}x() { var c = new DefaultAzureCredential(); }
                    }
                    """);

            var output = new DoctorTool().Run(tempDir, "markdown", excludes: null, full: true);

            Assert.Contains("`MAF-AP-SEC-001`", output, StringComparison.Ordinal);
            Assert.Contains("×2", output, StringComparison.Ordinal);
            Assert.Contains("grouped", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // Report readability — the default "Top fixes" list must space findings out
    // and rule them off from one another so the report is scannable. Locks the
    // shape against a silent regression to the old back-to-back rendering.
    // -------------------------------------------------------------------------

    [Fact]
    public void DefaultReport_SeparatesFindings_WithBlankLineAndRule()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-readability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Two findings (same rule, two files) → two entries in the default top-fixes list.
            foreach (var name in new[] { "A.cs", "B.cs" })
                File.WriteAllText(Path.Combine(tempDir, name), $$"""
                    using Azure.Identity; using Microsoft.Agents.AI;
                    public class {{name[..1]}}x
                    {
                        public {{name[..1]}}x() { var c = new DefaultAzureCredential(); }
                    }
                    """);

            var output = new DoctorTool().MafDoctor(tempDir); // default (top-fixes) markdown
            var norm = output.Replace("\r\n", "\n");

            // Bold-number headers (literal text, NOT a `1.`/`2.` ordered-list item, so the
            // rule between findings can't break list numbering).
            Assert.Contains("**1. [", norm, StringComparison.Ordinal);
            Assert.Contains("**2. [", norm, StringComparison.Ordinal);
            // A horizontal rule sits between the two findings, blank-line padded on both
            // sides (the leading blank line is what keeps `---` an <hr>, not a setext <h2>).
            Assert.Contains("\n\n---\n\n", norm, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Json_Finding_CarriesWhy()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Bad
                {
                    public Bad() { var c = new DefaultAzureCredential(); }
                }
                """);
            var output = new DoctorTool().MafDoctor(tempDir, format: "json");

            using var doc = JsonDocument.Parse(output);
            var fix = doc.RootElement.GetProperty("top_fixes")[0];
            Assert.False(string.IsNullOrWhiteSpace(fix.GetProperty("why").GetString()),
                "Tier 1 adds a non-empty `why` rationale to each known-rule finding.");
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void GetWhy_CoversEveryScannerAndPromptRule()
    {
        // Drift guard (registry-driven): every rule the scanner can actually emit,
        // plus the prompt-lint rules and the fan-in registry rule, must have a
        // non-empty Why. A new rule added to AllRules without a GetWhy arm would
        // otherwise ship an empty rationale silently. (Fan-out MAF001 + COST-001
        // carry their Why inline, not via GetWhy, so they're excluded here.)
        var ruleIds = AntiPatternScannerTool.AllRules.Select(r => r.Id)
            .Concat(new[] { "PROMPT-001", "PROMPT-002", "PROMPT-003", "PROMPT-004", "MAF130-FAN-IN-001" })
            .Distinct();
        foreach (var id in ruleIds)
            Assert.False(string.IsNullOrWhiteSpace(DoctorTool.GetWhy(id)),
                $"Rule {id} has no Why rationale — add a GetWhy arm.");
    }

    [Fact]
    public void FanOut_Report_SnippetShowsSignatureNotAttribute()
    {
        // The fan-out finding's line must land on the signature (return type),
        // not the [MessageHandler] attribute line — otherwise the snippet hides
        // the very thing the finding is about (the wrong return type).
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-fanout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Wf.cs"), """
                using System.Threading.Tasks;
                public partial class Inv : Executor
                {
                    [MessageHandler]
                    public ValueTask HandleAsync(string m) => default;
                }
                """);
            var output = new DoctorTool().Run(tempDir, "markdown", excludes: null, full: true);
            // Snippet shows the signature line, not the bare attribute.
            Assert.Contains("public ValueTask HandleAsync", output, StringComparison.Ordinal);
            Assert.DoesNotContain("→ `[MessageHandler]`", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void GroupedHeader_ForFanOut_IsRuleGeneric_NotPerInstance()
    {
        // Two DISTINCT fan-out methods collapse into one MAF001 group ×2; the
        // header must use a rule-generic title, not one method's specifics.
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-grouphdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Wf.cs"), """
                using System.Threading.Tasks;
                public partial class Inv : Executor
                {
                    [MessageHandler]
                    public ValueTask AlphaAsync(string m) => default;
                    [MessageHandler]
                    public ValueTask BetaAsync(string m) => default;
                }
                """);
            var output = new DoctorTool().Run(tempDir, "markdown", excludes: null, full: true);
            // The ×2 MAF001 header is generic — it does NOT name a single method.
            Assert.Contains("`MAF001` — fan-out handler must return `Task<T>` ×2", output, StringComparison.Ordinal);
            Assert.DoesNotContain("`AlphaAsync` returns", output, StringComparison.Ordinal); // not in the header
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void SecretRule_SourceLine_IsRedactedNotEchoed()
    {
        // MAF-AP-SEC-002 (hard-coded key) must NOT echo the secret-bearing line
        // into the report; the file:line locator stays, the snippet is redacted.
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Keys.cs"),
                "public class Keys { const string K = \"sk-ABCDEF0123456789abcdef\"; }");
            var output = new DoctorTool().Run(tempDir, "markdown", excludes: null, full: true);

            Assert.Contains("MAF-AP-SEC-002", output, StringComparison.Ordinal); // finding present
            Assert.Contains("redacted", output, StringComparison.Ordinal);       // redaction marker
            Assert.DoesNotContain("sk-ABCDEF0123456789abcdef", output, StringComparison.Ordinal); // secret NOT echoed
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void SecretOnSharedLine_NotEchoed_EvenWhenSurfacedByAnotherRule()
    {
        // A secret literal co-located with a `.Result` (CONC-002, a non-secret
        // rule) on one physical line: redaction is content-aware, so the line is
        // withheld no matter which rule's locator renders it.
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-coleak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // The `, 3)` (word char before `)`) is what trips CONC-002's `\b\)\.Result`;
            // the secret literal sits earlier on the same physical line.
            File.WriteAllText(Path.Combine(tempDir, "C.cs"),
                "public class C { object Get(string s, int n) => s; public void M() { var r = Get(\"sk-ABCDEF0123456789abcdef\", 3).Result; } }");
            var output = new DoctorTool().Run(tempDir, "markdown", excludes: null, full: true);

            Assert.Contains("MAF-AP-CONC-002", output, StringComparison.Ordinal); // a non-secret rule fired on that line
            Assert.DoesNotContain("sk-ABCDEF0123456789abcdef", output, StringComparison.Ordinal); // secret never echoed
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Json_FullMode_SerializesAllFindings_NotJustTopThree()
    {
        // `--all --json` (full + json) must serialize every finding into top_fixes,
        // not cap at 3. Seed >3 distinct findings and compare full vs default.
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-tier1-fulljson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Five distinct findings: SEC-002 (key), SEC-001 (cred), SEC-003
            // (sensitive data), WF-001 (missing sealed+partial), fan-out (void).
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Inv : Executor
                {
                    const string K = "sk-ABCDEF0123456789abcdef";
                    public Inv()
                    {
                        var c = new DefaultAzureCredential();
                        var o = new O { EnableSensitiveData = true };
                    }
                    [MessageHandler]
                    public void HandleAsync(string m) { }
                }
                """);
            var tool = new DoctorTool();

            int Count(bool full)
            {
                using var doc = JsonDocument.Parse(tool.Run(tempDir, "json", excludes: null, full: full));
                return doc.RootElement.GetProperty("top_fixes").GetArrayLength();
            }

            var defaultCount = Count(false);
            var fullCount = Count(true);
            Assert.Equal(3, defaultCount);            // default caps at 3
            Assert.True(fullCount > 3, $"full mode should serialize all findings, got {fullCount}");
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // Deep-review regressions — prompt warnings listed, SEC-003 syntax-aware,
    // JSON counts reconcile with top_fixes.
    // -------------------------------------------------------------------------

    [Fact]
    public void Grade_PromptWarnings_AreListed_NotJustCounted()
    {
        // Regression: prompt warnings were counted in the grade/metrics but never
        // appeared in AllFixes — so --all/--json/--plan contradicted the metrics.
        var prompts = new[]
        {
            new PromptFinding("PROMPT-001", PromptSeverity.Warning, "Instructions is empty", "A.cs", 5),
            new PromptFinding("PROMPT-003", PromptSeverity.Warning, "no refusal clause", "A.cs", 6),
        };
        var summary = DoctorTool.Grade([], [], prompts, []);
        Assert.Equal(2, summary.PromptWarnings);
        Assert.Equal(2, summary.AllFixes.Count(f => f.RuleId.StartsWith("PROMPT-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Sec003_IsSyntaxAware_DetectsDictionaryForm_IgnoresComment()
    {
        // The dictionary-key form is the shape the rewriter fixes; a comment that
        // merely mentions the rule must NOT fire.
        const string src = """
            using System.Collections.Generic;
            class C { void M() {
                // EnableSensitiveData = true   (just a comment — not a finding)
                var d = new Dictionary<string, bool> { ["EnableSensitiveData"] = true };
            } }
            """;
        var sec003 = AntiPatternScannerTool.ScanFile(src, "C.cs")
            .Where(f => f.RuleId == "MAF-AP-SEC-003").ToList();
        Assert.Single(sec003); // the real dict assignment only — not the comment
    }

    [Fact]
    public void RegexRule_DetectsRealMatch_AfterAStringMatchOnSameLine()
    {
        // Regression: trivia-aware scan must inspect EVERY match on a line, not
        // just the first — a real call after a string-literal mention is real.
        const string src = "using Microsoft.Agents.AI; class C { void M() { var x = \"new DefaultAzureCredential()\"; var y = new DefaultAzureCredential(); } }";
        var sec001 = AntiPatternScannerTool.ScanFile(src, "C.cs")
            .Where(f => f.RuleId == "MAF-AP-SEC-001").ToList();
        Assert.Single(sec001); // the real `new DefaultAzureCredential()`, not skipped
    }

    [Fact]
    public void Sec003_OnlyFlagsRewriterFixableContexts()
    {
        // Statement assignment → fixable → flagged.
        const string stmt = "class C { void M(Opts o) { o.EnableSensitiveData = true; } } class Opts { public bool EnableSensitiveData; }";
        Assert.Single(AntiPatternScannerTool.ScanFile(stmt, "S.cs").Where(f => f.RuleId == "MAF-AP-SEC-003"));
        // Expression-bodied lambda → the rewriter can't fix it → NOT flagged (no false auto-fixable).
        const string lambda = "class C { System.Action F(Opts o) => () => o.EnableSensitiveData = true; } class Opts { public bool EnableSensitiveData; }";
        Assert.Empty(AntiPatternScannerTool.ScanFile(lambda, "L.cs").Where(f => f.RuleId == "MAF-AP-SEC-003"));
    }

    [Fact]
    public void Grade_TopFixes_AreDeterministicWithinPriority()
    {
        // Same priority bucket must order by file then line, so the top-3 headline
        // and JSON top_fixes don't depend on filesystem enumeration order.
        var aps = new[]
        {
            new AntiPatternFinding("MAF-AP-SEC-001", "n", AntiPatternSeverity.Error, "zzz.cs", 9, "m"),
            new AntiPatternFinding("MAF-AP-SEC-001", "n", AntiPatternSeverity.Error, "aaa.cs", 2, "m"),
            new AntiPatternFinding("MAF-AP-SEC-001", "n", AntiPatternSeverity.Error, "aaa.cs", 1, "m"),
        };
        var s = DoctorTool.Grade(aps, []);
        Assert.Equal(new[] { ("aaa.cs", 1), ("aaa.cs", 2), ("zzz.cs", 9) },
            s.AllFixes.Select(f => (f.File, f.Line)).ToArray());
    }

    [Fact]
    public void Conc002_DetectsParameterlessCall_AndIsNotAutoFixable()
    {
        // Regression: leading `\b` made the canonical `Foo().Result` / `Foo().Wait()`
        // miss. One finding per line, so put the two forms on separate lines.
        const string src = "using System.Threading.Tasks;\nclass C {\n  void M() {\n    var a = Foo().Result;\n    Bar().Wait();\n  }\n  Task<int> Foo() => null;\n  Task Bar() => null;\n}";
        var conc = AntiPatternScannerTool.ScanFile(src, "C.cs").Where(f => f.RuleId == "MAF-AP-CONC-002").ToList();
        Assert.Equal(2, conc.Count); // both .Result and .Wait() forms
        // Sync-over-async needs judgment (async-ify the caller) → NOT auto-fixable.
        var summary = DoctorTool.Grade(conc, []);
        Assert.All(summary.AllFixes.Where(f => f.RuleId == "MAF-AP-CONC-002"), f => Assert.False(f.AutoFixable));
    }

    [Fact]
    public void Conc002_StillIgnoresPropertyAccess_NoCloseParen()
    {
        // `task.Result` (no call) must NOT match — only `<call>().Result`.
        const string src = "using System.Threading.Tasks; class C { void M(Task<int> t) { var a = t.Result; } }";
        Assert.Empty(AntiPatternScannerTool.ScanFile(src, "C.cs").Where(f => f.RuleId == "MAF-AP-CONC-002"));
    }

    [Fact]
    public void Agent001_DetectsTargetTypedNew()
    {
        // Regression: target-typed `new()` placement of Instructions was never flagged.
        const string src = "class C { void M() { ChatClientAgentOptions o = new() { Instructions = \"x\" }; } }";
        Assert.Single(AntiPatternScannerTool.ScanFile(src, "C.cs").Where(f => f.RuleId == "MAF-AP-AGENT-001"));
    }

    [Fact]
    public void BareSecretInComment_IsRedacted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-baresecret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "F.cs"),
                "using Microsoft.Agents.AI; public class F { public F() { var c = new DefaultAzureCredential(); } } // rotate sk-BARESKabcdefghij1234567 now");
            var output = new DoctorTool().Run(dir, "markdown", excludes: null, full: true);
            Assert.Contains("MAF-AP-SEC-001", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-BARESKabcdefghij1234567", output, StringComparison.Ordinal); // bare token redacted
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_ExpressionBodiedPropertyWithResult()
    {
        // Regression (blocker): the CONC-002 rewriter inserted `await` into a
        // non-async expression-bodied property → uncompilable. Must skip it.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "P.cs");
            File.WriteAllText(file,
                "using System.Threading.Tasks; public class P { Task<string> Fetch() => Task.FromResult(\"x\"); public string Val => Fetch().Result; }");
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);  // never injected the `(await …)` rewrite
            Assert.Contains(".Result", after, StringComparison.Ordinal);       // left intact
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_OperatorOrConversionBodyWithResult()
    {
        // Round-3 regression: operators / user-defined conversions can't be async,
        // so the CONC-002 rewriter must NOT inject `await` into their bodies.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002op-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Op.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class Op {
                    static Task<int> Foo() => Task.FromResult(1);
                    public static Op operator +(Op a, Op b) { var z = Foo().Result; return a; }
                    public static explicit operator string(Op c) => Foo().Result.ToString();
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal); // no `await` in operator/conversion bodies
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_PrimaryConstructorBaseInitializer()
    {
        // Round-4 regression: a C# 12 primary-ctor base initializer can't be async.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "PC.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class Base { public Base(int x) { } }
                public static class H { public static Task<int> Foo() => Task.FromResult(1); }
                public class Derived(int p) : Base(H.Foo().Result) { }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_LinqQueryClause()
    {
        // Round-5 regression: `await` is illegal in a let/where/select clause
        // (CS1995) even inside an async method — must skip.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002linq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Q.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                using System.Linq;
                public class Q {
                  static Task<int> F() => Task.FromResult(1);
                  public async Task M() {
                    var q = from i in new[] { 1, 2 } let r = F().Result select r;
                    await Task.Yield();
                    _ = q.ToList();
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_NestedQueryJoinSourceInOuterIllegalClause()
    {
        // Round-9 regression: a `.Result` in an INNER query's join source that sits
        // inside an OUTER query's await-illegal `let` clause must be skipped. The
        // old IsInAwaitableQuerySource descended into the nested query and wrongly
        // marked the outer position awaitable → injected `await` → CS1995 corruption.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002nestq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "NestQ.cs");
            File.WriteAllText(file, """
                using System.Linq;
                using System.Collections.Generic;
                using System.Threading.Tasks;
                public class C {
                  Task<List<int>> Foo() => Task.FromResult(new List<int>());
                  public async Task M() {
                    var a = new List<int>();
                    var b = new List<int>();
                    var q = from i in a
                            let k = (from x in b join y in Foo().Result on x equals y select x).Count()
                            select i + k;
                    await Task.Yield();
                    _ = q.ToList();
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await Foo", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_Rewrites_ResultInTopLevelJoinSource()
    {
        // Control / no-over-skip: a `.Result` in the join source of a TOP-LEVEL query
        // inside an async method IS await-legal (CS1995 explicitly permits await in a
        // join collection expression), so it SHOULD rewrite.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002joinok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "JoinOk.cs");
            File.WriteAllText(file, """
                using System.Linq;
                using System.Collections.Generic;
                using System.Threading.Tasks;
                public class C {
                  Task<List<int>> FooAsync() => Task.FromResult(new List<int>());
                  public async Task M() {
                    var b = new List<int>();
                    var q = from x in b join y in FooAsync().Result on x equals y select x;
                    await Task.Yield();
                    _ = q.ToList();
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.Contains("(await Foo", after, StringComparison.Ordinal); // join source is awaitable
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_IsIdempotent_OnSkippedPrimaryCtorBase()
    {
        // Round-5 regression: the skip-TODO comment was re-stacked on a 2nd run
        // for contexts with no enclosing method (primary-ctor base initializer).
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002idem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "PC.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class Base { public Base(int x) { } }
                public static class H { public static Task<int> G() => Task.FromResult(1); }
                public class Derived(int n) : Base(H.G().Result) { }
                """);
            var tool = new AutoFixTool();
            tool.MafAutoFixAll(dir, dryRun: false);
            var after1 = File.ReadAllText(file);
            tool.MafAutoFixAll(dir, dryRun: false);
            var after2 = File.ReadAllText(file);
            Assert.Equal(after1, after2); // 2nd run is a no-op (no duplicate comment)
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_StillRewrites_ResultInsideAsyncMethod()
    {
        // The whitelist must keep rewriting valid async contexts (no over-skip).
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Ok.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C { static Task<int> FooAsync() => Task.FromResult(1);
                  public async Task<int> M() { return FooAsync().Result; } }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.Contains("(await", after, StringComparison.Ordinal); // rewritten in the async method
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_CatchFilter()
    {
        // Round-6 regression: `await` is illegal in a `catch when (...)` filter
        // (CS7094) even inside an async method — must skip the filter expression.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002catch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Catch.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  static Task<int> Foo() => Task.FromResult(1);
                  public async Task M() {
                    try { await Task.Yield(); }
                    catch (System.Exception) when (Foo().Result > 0) { }
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal); // no `await` in the filter
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_StillRewrites_ResultInsideCatchBody()
    {
        // No over-skip: the catch BODY is await-legal in an async method, so the
        // filter guard must not suppress a rewrite there.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002catchbody-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "CatchBody.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  static Task<int> FooAsync() => Task.FromResult(1);
                  public async Task M() {
                    try { await Task.Yield(); }
                    catch (System.Exception) { var x = FooAsync().Result; }
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.Contains("(await Foo", after, StringComparison.Ordinal); // rewritten in the catch body
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_UnsafeBlock()
    {
        // Round-6 regression: `await` is illegal in an unsafe context (CS4004) —
        // an `unsafe { }` block must be skipped even inside an async method.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002unsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Unsafe.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  static Task<int> Foo() => Task.FromResult(1);
                  public async Task M() {
                    unsafe { var x = Foo().Result; }
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_FixedBlock()
    {
        // Round-6 regression: a `fixed ( )` block is an unsafe context (CS4004).
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002fixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Fixed.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  static Task<int> Foo() => Task.FromResult(1);
                  public async Task M() {
                    int[] arr = { 1 };
                    unsafe { fixed (int* p = arr) { var x = Foo().Result; } }
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_AsyncUnsafeMethod()
    {
        // Round-6 regression: the `unsafe` modifier on the enclosing method makes
        // its whole body an unsafe context — `await` is illegal even with `async`.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002unsafemethod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "UnsafeMethod.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  static Task<int> Foo() => Task.FromResult(1);
                  public async unsafe Task M() { var x = Foo().Result; }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_UnsafeType()
    {
        // Round-6 regression: the `unsafe` modifier on the enclosing TYPE makes
        // every member body an unsafe context — walk all type ancestors for it.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002unsafetype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "UnsafeType.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public unsafe class C {
                  static Task<int> Foo() => Task.FromResult(1);
                  public async Task M() { var x = Foo().Result; }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_Rewrites_ResultInAsyncLambdaInsideLock()
    {
        // Round-8 regression: an async lambda is its OWN await context, so a
        // `.Result` inside `async () => {}` nested in a `lock` is await-legal and
        // SHOULD be rewritten. The old unconditional lock pre-pass over-skipped it
        // and injected a factually wrong "cannot await inside a lock" comment.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002lamblock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "LamLock.cs");
            File.WriteAllText(file, """
                using System;
                using System.Threading.Tasks;
                public class C {
                  readonly object _lock = new();
                  static Task<int> FooAsync() => Task.FromResult(1);
                  public async Task M() {
                    lock (_lock) { Func<Task> a = async () => { var x = FooAsync().Result; }; }
                    await Task.Yield();
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.Contains("(await Foo", after, StringComparison.Ordinal);          // rewritten in the async lambda
            Assert.DoesNotContain("cannot await inside a lock", after, StringComparison.Ordinal); // no false comment
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_Rewrites_ResultInAsyncLocalFunctionInsideLock()
    {
        // Round-8 regression: same boundary rule for an async LOCAL FUNCTION nested
        // in a lock — its body is its own await context.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002lflock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "LfLock.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  readonly object _lock = new();
                  static Task<int> FooAsync() => Task.FromResult(1);
                  public void M() {
                    lock (_lock) { async Task Inner() { var x = FooAsync().Result; } _ = Inner(); }
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.Contains("(await Foo", after, StringComparison.Ordinal);
            Assert.DoesNotContain("cannot await inside a lock", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_ResultInAsyncLambdaInsideUnsafe()
    {
        // Round-8 boundary check: UNLIKE lock, an unsafe context propagates into a
        // nested async lambda (CS4004), so it must STILL be skipped there.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002lambunsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "LamUnsafe.cs");
            File.WriteAllText(file, """
                using System;
                using System.Threading.Tasks;
                public class C {
                  static Task<int> Foo() => Task.FromResult(1);
                  public async Task M() {
                    unsafe { Func<Task> a = async () => { var x = Foo().Result; }; }
                    await Task.Yield();
                  }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await Foo", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    // Round-9 regression: the `unsafe` modifier on a NON-method member establishes an
    // unsafe context that propagates into a nested async lambda — so a `.Result` there
    // must be skipped (CS4004). The old guard only checked method/local-fn/type.
    [InlineData("public unsafe int P { get { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return 0; } }")]
    [InlineData("public unsafe int this[int i] { get { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return 0; } }")]
    [InlineData("public static unsafe C operator +(C a, C b) { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return a; }")]
    [InlineData("public static unsafe implicit operator int(C c) { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; return 0; }")]
    [InlineData("public unsafe C() { System.Func<System.Threading.Tasks.Task> f = async () => { var x = Foo().Result; }; }")]
    public void AutofixAll_DoesNotCorrupt_UnsafeMemberWithNestedAsyncLambda(string member)
    {
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002unsafemem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "UnsafeMember.cs");
            File.WriteAllText(file,
                "using System.Threading.Tasks;\npublic class C {\n  static Task<int> Foo() => Task.FromResult(1);\n  " + member + "\n}\n");
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await Foo", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_ParameterDefault()
    {
        // Round-8 (signature-position guard): a `.Result` in a parameter default is
        // await-illegal even inside an async method (it needs a compile-time
        // constant), so the whitelist must skip before reaching the async boundary.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002paramdef-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "ParamDef.cs");
            File.WriteAllText(file, """
                using System.Threading.Tasks;
                public class C {
                  static int Foo() => 1;
                  public async Task M(int x = Foo().Result) { await Task.Yield(); }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await Foo", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AutofixAll_DoesNotCorrupt_AttributeArgument()
    {
        // Round-8 (signature-position guard): a `.Result` in an attribute argument
        // is await-illegal even on an async method.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-conc002attrarg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "AttrArg.cs");
            File.WriteAllText(file, """
                using System;
                using System.Threading.Tasks;
                public class MyAttr : Attribute { public MyAttr(int v) { } }
                public class C {
                  static Task<int> Foo() => null!;
                  [MyAttr(Foo().Result)]
                  public async Task M() { await Task.Yield(); }
                }
                """);
            new AutoFixTool().MafAutoFixAll(dir, dryRun: false);
            var after = File.ReadAllText(file);
            Assert.DoesNotContain("(await Foo", after, StringComparison.Ordinal);
            Assert.Contains(".Result", after, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ScanAntiPatterns_SarifOutput_RedactsSecret()
    {
        // Round-3 regression: the SARIF surface must redact the SEC-002 secret too.
        var dir = Path.Combine(Path.GetTempPath(), "maf-doctor-sarifsecret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "K.cs"),
                "public class K { const string S = \"sk-abcdefghij1234567890\"; }");
            var sarif = new AntiPatternScannerTool().MafScanAntiPatterns(dir, format: "sarif");
            Assert.Contains("MAF-AP-SEC-002", sarif, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-abcdefghij1234567890", sarif, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Wf001_SkipsAbstractExecutor_ButFlagsConcrete()
    {
        // Abstract Executor can never be `sealed partial` → not flagged (no impossible auto-fix promise).
        const string abstractCls = "public abstract class Base : Executor { [MessageHandler] public abstract System.Threading.Tasks.Task<int> H(string s); }";
        Assert.Empty(AntiPatternScannerTool.ScanFile(abstractCls, "B.cs").Where(f => f.RuleId == "MAF-AP-WF-001"));
        // Concrete Executor missing sealed/partial still fires.
        const string concrete = "public class Conc : Executor { [MessageHandler] public System.Threading.Tasks.Task<int> H(string s) => null; }";
        Assert.Single(AntiPatternScannerTool.ScanFile(concrete, "C.cs").Where(f => f.RuleId == "MAF-AP-WF-001"));
    }

    [Fact]
    public void Json_HasUnboundedCostSites_AndCountsReconcileWithTopFixes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-jsonreconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // SEC-001 (error) + a real dictionary SEC-003 (error).
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                using System.Collections.Generic;
                class C { void M() {
                    var c = new DefaultAzureCredential();
                    var d = new Dictionary<string, bool> { ["EnableSensitiveData"] = true };
                } }
                """);
            using var doc = JsonDocument.Parse(new DoctorTool().Run(tempDir, "json", excludes: null, full: true));
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("unbounded_cost_sites", out _)); // new field present
            var sum = root.GetProperty("errors_count").GetInt32()
                + root.GetProperty("warnings_count").GetInt32()
                + root.GetProperty("silent_starvation_risks").GetInt32()
                + root.GetProperty("unbounded_cost_sites").GetInt32();
            Assert.Equal(root.GetProperty("top_fixes").GetArrayLength(), sum); // counts == listed findings
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // Tier 3 — `--plan` / format:"plan": an ordered, checkboxed remediation plan.
    // -------------------------------------------------------------------------

    [Fact]
    public void Plan_MixedFindings_HasBothPhasesAndCheckboxes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Auto-fixable (SEC-001, SEC-003, WF-001) + semantic (SEC-002 key, fan-out void).
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Inv : Executor
                {
                    const string K = "sk-ABCDEF0123456789abcdef";
                    public Inv()
                    {
                        var c = new DefaultAzureCredential();
                        var o = new O { EnableSensitiveData = true };
                    }
                    [MessageHandler]
                    public void HandleAsync(string m) { }
                }
                """);
            var output = new DoctorTool().Run(tempDir, "plan", excludes: null);

            Assert.Contains("remediation plan", output, StringComparison.Ordinal);
            Assert.Contains("## Phase 1", output, StringComparison.Ordinal);
            Assert.Contains("## Phase 2", output, StringComparison.Ordinal);
            Assert.Contains("autofix-all", output, StringComparison.Ordinal);
            Assert.Contains("- [ ]", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Plan_DoesNotEchoSecret()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-plan-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Keys.cs"),
                "public class Keys { const string K = \"sk-ABCDEF0123456789abcdef\"; }");
            var output = new DoctorTool().Run(tempDir, "plan", excludes: null);

            Assert.Contains("MAF-AP-SEC-002", output, StringComparison.Ordinal); // the task is listed
            Assert.DoesNotContain("sk-ABCDEF0123456789abcdef", output, StringComparison.Ordinal); // but never the secret
            // SEC-002 is NOT auto-fixable → Phase-2-only: Phase 1 must be absent.
            Assert.DoesNotContain("## Phase 1", output, StringComparison.Ordinal);
            Assert.Contains("## Phase 2", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Plan_AllAutoFixable_HasPhase1Only()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-plan-p1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Lone DefaultAzureCredential (SEC-001) — auto-fixable → Phase 1 only.
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Bad { public Bad() { var c = new DefaultAzureCredential(); } }
                """);
            var output = new DoctorTool().Run(tempDir, "plan", excludes: null);

            Assert.Contains("## Phase 1", output, StringComparison.Ordinal);
            Assert.DoesNotContain("## Phase 2", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Plan_IgnoresFullFlag()
    {
        // `full` is documented as ignored for format "plan" (always covers all).
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-plan-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Bad.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Inv : Executor
                {
                    public Inv() { var c = new DefaultAzureCredential(); }
                    [MessageHandler]
                    public void HandleAsync(string m) { }
                }
                """);
            var tool = new DoctorTool();
            var withFull = tool.Run(tempDir, "plan", excludes: null, full: true);
            var withoutFull = tool.Run(tempDir, "plan", excludes: null, full: false);
            Assert.Equal(withoutFull, withFull);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Plan_CleanRepo_SaysNothingToDo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "maf-doctor-plan-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Clean.cs"),
                "public class Clean { public int Add(int a, int b) => a + b; }");
            var output = new DoctorTool().Run(tempDir, "plan", excludes: null);
            Assert.Contains("Nothing to do", output, StringComparison.Ordinal);
            Assert.DoesNotContain("## Phase", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    // -------------------------------------------------------------------------
    // Tier 1 — doctor CLI arg parsing (extracted to DoctorCli for testability)
    // -------------------------------------------------------------------------

    [Fact]
    public void DoctorCli_Parse_JsonFlag_SetsJsonFormat()
    {
        var (_, format, _, _, _, _) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--json" });
        Assert.Equal("json", format);
    }

    [Fact]
    public void DoctorCli_Parse_PlanFlag_SetsPlanFormat()
    {
        var (_, format, _, _, _, _) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--plan" });
        Assert.Equal("plan", format);
    }

    [Fact]
    public void DoctorCli_Parse_PlanAndJsonTogether_IsManifest()
    {
        // New contract: `--plan --json` (either order) = the structured remediation
        // manifest ("plan-json"); each flag alone keeps its own format.
        Assert.Equal("plan-json", MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--json", "--plan" }).Format);
        Assert.Equal("plan-json", MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--plan", "--json" }).Format);
        Assert.Equal("plan", MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--plan" }).Format);
        Assert.Equal("json", MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--json" }).Format);
    }

    [Fact]
    public void DoctorCli_Parse_NoFormatFlag_DefaultsToMarkdown()
    {
        var (path, format, _, full, _, _) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", "." });
        Assert.Equal(".", path);
        Assert.Equal("markdown", format);
        Assert.False(full);
    }

    [Fact]
    public void DoctorCli_Parse_FlagBeforePath_StillCapturesPath()
    {
        // The `--` guard means a flag preceding the path isn't mistaken for it.
        var (path, format, _, full, _, _) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", "--all", "--json", "myrepo" });
        Assert.Equal("myrepo", path);
        Assert.Equal("json", format);
        Assert.True(full);
    }

    [Fact]
    public void DoctorCli_Parse_ExcludeIsRepeatable()
    {
        var (_, _, excludes, _, _, _) = MafDoctor.Commands.DoctorCli.Parse(
            new[] { "doctor", ".", "--exclude", "samples/", "--exclude", "tests/" });
        Assert.Equal(new[] { "samples/", "tests/" }, excludes);
    }

    [Fact]
    public void DoctorCli_Parse_UnknownFlag_ReturnsError()
    {
        var (_, _, _, _, _, error) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--nope" });
        Assert.NotNull(error);
        Assert.Contains("--nope", error);
    }

    [Fact]
    public void DoctorCli_Parse_ExtraPositional_ReturnsError()
    {
        var (_, _, _, _, _, error) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", "a", "b" });
        Assert.NotNull(error);
    }

    [Fact]
    public void DoctorCli_Parse_ExcludeSwallowingFlag_ReturnsError()
    {
        // REP-05: `--exclude --json` must NOT eat --json; it errors loudly instead.
        var (_, _, excludes, _, _, error) = MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--exclude", "--json" });
        Assert.NotNull(error);
        Assert.Empty(excludes);
    }

    [Fact]
    public void DoctorCli_Parse_FailOn_ParsesGradeAndRejectsJunk()
    {
        Assert.Equal("C", MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--fail-on", "c" }).FailOn);
        Assert.Null(MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--fail-on", "C" }).Error);
        Assert.NotNull(MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--fail-on", "Z" }).Error);
        Assert.NotNull(MafDoctor.Commands.DoctorCli.Parse(new[] { "doctor", ".", "--fail-on", "--json" }).Error);
    }

    [Fact]
    public void Run_ExcludedPaths_DoNotAffectGrade()
    {
        // NOTE: the AntiPatternScanner already self-skips `samples`/`tests` path
        // segments for skipInTestFiles rules (SourceFileWalker isTestFile). The
        // walker-level --exclude is BROADER — it drops files before ANY scanner
        // (incl. fan-out / prompt / cost, which have no such skip). To exercise
        // it cleanly we put the bait under a non-sample dir the scanner does NOT
        // self-skip, then exclude that dir at the walker level.
        var root = Path.Combine(Path.GetTempPath(), "maf-doctor-exclude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "legacy"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            // Bait under legacy/ — DefaultAzureCredential (MAF-AP-SEC-001 error).
            File.WriteAllText(Path.Combine(root, "legacy", "Bait.cs"), """
                using Azure.Identity; using Microsoft.Agents.AI;
                public class Bait
                {
                    public Bait() { var a = new DefaultAzureCredential(); }
                }
                """);
            // Clean product code under src/.
            File.WriteAllText(Path.Combine(root, "src", "Calc.cs"),
                "public class Calc { public int Add(int a, int b) => a + b; }");

            var tool = new DoctorTool();

            // Unscoped: the legacy bait drags the grade off A.
            using (var doc = JsonDocument.Parse(tool.Run(root, "json", excludes: null)))
                Assert.NotEqual("A", doc.RootElement.GetProperty("verdict").GetString());

            // Scoped: exclude legacy/ → only clean src/ remains → grade A.
            using (var doc = JsonDocument.Parse(tool.Run(root, "json", new[] { "legacy/" })))
                Assert.Equal("A", doc.RootElement.GetProperty("verdict").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Scan truncation — #148 finding 2. AnalyzeRepo takes an optional
    // ScanBudget (test-only param; production always passes null → a fresh,
    // default-capped budget) so the truncation path is exercisable without
    // creating tens of thousands of files.
    // -------------------------------------------------------------------------

    [Fact]
    public void AnalyzeRepo_UnderCap_NotTruncated()
    {
        var root = Path.Combine(Path.GetTempPath(), "maf-doctor-scancap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Clean.cs"), "public class Clean { }");

            var summary = DoctorTool.AnalyzeRepo(root, excludes: null);

            Assert.False(summary.ScanTruncated);
            Assert.False(summary.ScanIncomplete);
            Assert.Equal(0, summary.FilesSkippedOversized);
            Assert.Equal(1, summary.FilesScanned);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnalyzeRepo_OverCap_ReportsTruncatedAndFilesScanned()
    {
        var root = Path.Combine(Path.GetTempPath(), "maf-doctor-scancap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(root, $"File{i}.cs"), "public class C { }");

            var summary = DoctorTool.AnalyzeRepo(root, excludes: null, new SourceFileWalker.ScanBudget { MaxFiles = 2 });

            Assert.True(summary.ScanTruncated);
            Assert.True(summary.ScanIncomplete);
            Assert.Equal(2, summary.FilesScanned);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnalyzeRepo_OversizedFile_SkippedAndReportedWithoutTruncatingTheRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "maf-doctor-scancap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Normal.cs"), "public class Normal { }");
            File.WriteAllText(Path.Combine(root, "Huge.cs"), "public class Huge { }");

            var budget = new SourceFileWalker.ScanBudget { MaxFileBytes = 5 }; // "public class Huge { }" > 5 bytes
            var summary = DoctorTool.AnalyzeRepo(root, excludes: null, budget);

            // The oversized file is skipped, but the walk still covers Normal.cs —
            // ScanTruncated (the file-COUNT cap) stays false; only the oversized
            // signal fires, and ScanIncomplete reflects it either way.
            Assert.False(summary.ScanTruncated);
            Assert.True(summary.FilesSkippedOversized >= 1);
            Assert.True(summary.ScanIncomplete);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FormatReport_Truncated_IncludesWarningNote()
    {
        var summary = DoctorTool.Grade([], []) with { ScanTruncated = true, FilesScanned = 42 };
        var report = DoctorTool.FormatReport("/some/repo", summary);

        Assert.Contains("Scan incomplete", report, StringComparison.Ordinal);
        Assert.Contains("42", report, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReport_OversizedSkipOnly_IncludesWarningNote()
    {
        var summary = DoctorTool.Grade([], []) with { FilesSkippedOversized = 3 };
        var report = DoctorTool.FormatReport("/some/repo", summary);

        Assert.Contains("Scan incomplete", report, StringComparison.Ordinal);
        Assert.Contains("3", report, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReport_NotTruncated_OmitsWarningNote()
    {
        var summary = DoctorTool.Grade([], []);
        var report = DoctorTool.FormatReport("/some/repo", summary);

        Assert.DoesNotContain("Scan incomplete", report, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPlan_Truncated_IncludesWarningNote()
    {
        var finding = new AntiPatternFinding("MAF-AP-SEC-001", "name", AntiPatternSeverity.Error, "f", 1, "m");
        var summary = DoctorTool.Grade([finding], []) with { ScanTruncated = true, FilesScanned = 7 };
        var plan = DoctorTool.FormatPlan("/some/repo", summary);

        Assert.Contains("Scan incomplete", plan, StringComparison.Ordinal);
    }
}
