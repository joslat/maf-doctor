using System;
using System.IO;
using System.Text.Json;
using MafDoctor.Commands;
using MafDoctor.Data;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// #5 polish — tests for <see cref="VerifyRegistryCommand"/>.
///
/// The command is exit-code-based for CI consumption. Tests focus on the
/// pure helpers (<c>BraceImbalance</c>, <c>LooksLikePlaceholder</c>,
/// <c>CheckEntry</c>) plus an integration check that the LIVE embedded
/// registry passes verification.
/// </summary>
public class VerifyRegistryCommandTests
{
    // -------------------------------------------------------------------------
    // Cross-language placeholder-grammar lock (single source of truth)
    // -------------------------------------------------------------------------

    [Fact]
    public void LooksLikePlaceholder_MatchesSharedFixture()
    {
        // placeholder-fixtures.json is asserted by BOTH this test and the Python
        // test (test_placeholder_fixtures.py) so the two gates can never drift.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string Rel() => Path.Combine(".github", "skills", "maf-obsolete-api-registry", "placeholder-fixtures.json");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Rel())))
            dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, Rel())));
        var cases = doc.RootElement.GetProperty("cases");
        Assert.True(cases.GetArrayLength() > 0);
        foreach (var c in cases.EnumerateArray())
        {
            var value = c.GetProperty("value").GetString();
            var expected = c.GetProperty("placeholder").GetBoolean();
            Assert.True(VerifyRegistryCommand.LooksLikePlaceholder(value) == expected,
                $"LooksLikePlaceholder(\"{value}\") == {!expected}, expected {expected} (placeholder-fixtures.json)");
        }
    }

    [Fact]
    public void LooksLikePlaceholder_EmDashTodo_IsPlaceholder()
    {
        // The historical divergence: this used to pass C# but fail Python.
        Assert.True(VerifyRegistryCommand.LooksLikePlaceholder("TODO — review the diff entry below"));
    }

    // -------------------------------------------------------------------------
    // BraceImbalance
    // -------------------------------------------------------------------------

#pragma warning disable CS0618 // BraceImbalance is obsolete; tests preserve legacy contract.
    [Theory]
    [InlineData("{ }", 0)]
    [InlineData("( )", 0)]
    [InlineData("{ ( ) }", 0)]
    [InlineData("{ ", 1)]            // missing close
    [InlineData(" }", -1)]            // missing open
    [InlineData("{ { } ", 1)]         // 1 open unbalanced
    [InlineData("class C { void M() { var x = 1; } }", 0)]
    [InlineData("", 0)]
    public void BraceImbalance_CountsCorrectly(string input, int expected)
    {
        Assert.Equal(expected, VerifyRegistryCommand.BraceImbalance(input));
    }
#pragma warning restore CS0618

    // -------------------------------------------------------------------------
    // BraceBalanceFailure — Phase 2.10 depth-aware check.
    //
    // The new helper catches `}{` and `}; evil(); {` patterns that the
    // count-only BraceImbalance v1 returned 0 for. It also returns a
    // human-readable failure description rather than a raw int.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("{ }")]
    [InlineData("( )")]
    [InlineData("{ ( ) }")]
    [InlineData("class C { void M() { var x = 1; } }")]
    [InlineData("")]
    [InlineData(null)]
    public void BraceBalanceFailure_AcceptsBalancedInput(string? input)
    {
        Assert.Null(VerifyRegistryCommand.BraceBalanceFailure(input));
    }

    [Theory]
    [InlineData("}{", "'}' before matching")]
    [InlineData(")(", "')' before matching")]
    [InlineData("}; evil(); {", "'}' before matching")]
    [InlineData("{", "1 unmatched")]
    [InlineData("(", "1 unmatched")]
    [InlineData("({)}", "before matching")]   // mismatched closing order
    public void BraceBalanceFailure_RejectsAdversarialInput(string input, string expectedFragment)
    {
        var failure = VerifyRegistryCommand.BraceBalanceFailure(input);
        Assert.NotNull(failure);
        Assert.Contains(expectedFragment, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckEntry_NestingAwareBraceImbalance_FlagsAdversarialPattern()
    {
        // The v1 count-only check returned delta=0 for `}{` — it passed.
        // The Phase 2.10 depth-aware check catches it.
        var entry = MakeEntry(exampleBefore: "}; evil(); {");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("unbalanced braces"));
    }

    // -------------------------------------------------------------------------
    // LooksLikePlaceholder
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("TODO", true)]
    [InlineData("tbd", true)]
    [InlineData("xxx", true)]
    [InlineData("TODO: replace with real fix", true)]
    [InlineData("TBD: figure out", true)]
    [InlineData("Replace `DefaultAzureCredential` with `ManagedIdentityCredential`.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikePlaceholder_DetectsLeftovers(string? input, bool expected)
    {
        Assert.Equal(expected, VerifyRegistryCommand.LooksLikePlaceholder(input));
    }

    // -------------------------------------------------------------------------
    // CheckEntry — failure modes
    // -------------------------------------------------------------------------

    [Fact]
    public void CheckEntry_EmptyFixDescription_FlagsAsIssue()
    {
        var entry = MakeEntry(fixDescription: "");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("fix_description is empty"));
    }

    [Fact]
    public void CheckEntry_TodoFixDescription_FlagsAsIssue()
    {
        var entry = MakeEntry(fixDescription: "TODO: write something");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("placeholder"));
    }

    [Fact]
    public void CheckEntry_ShortFixDescription_FlagsAsTooShort()
    {
        var entry = MakeEntry(fixDescription: "fix this");  // 8 chars
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("too short"));
    }

    [Fact]
    public void CheckEntry_ExampleBeforeEqualsAfter_FlagsAsNoop()
    {
        var entry = MakeEntry(
            exampleBefore: "var x = Foo();",
            exampleAfter: "var x = Foo();");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("example_before == example_after"));
    }

    [Fact]
    public void CheckEntry_UnbalancedBraces_FlagsAsIssue()
    {
        var entry = MakeEntry(
            exampleBefore: "var x = Foo( ;",   // open paren not closed
            exampleAfter:  "var x = Foo();");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("unbalanced braces"));
    }

    [Fact]
    public void CheckEntry_TbdGuideSection_FlagsAsPlaceholder()
    {
        var entry = MakeEntry(guideSection: "TBD");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Contains(issues, i => i.Contains("guide_section"));
    }

    [Fact]
    public void CheckEntry_NaGuideSection_PassesValidation()
    {
        var entry = MakeEntry(guideSection: "N/A");
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        // "N/A" is the canonical "no parallel section" marker — must not flag.
        Assert.DoesNotContain(issues, i => i.Contains("guide_section"));
    }

    [Fact]
    public void CheckEntry_FullyValidEntry_FlagsNothing()
    {
        var entry = MakeEntry();
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(entry, issues);
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("CS0618")]
    [InlineData("CS0246")]
    [InlineData("CS1234")]
    [InlineData("RUNTIME_SILENT")]
    [InlineData("BINARY_BREAK")]
    public void CheckEntry_AllowedCsWarning_Passes(string warning)
    {
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(MakeEntry(csWarning: warning), issues);
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TODO")]
    [InlineData("CS618")]
    [InlineData("CS06180")]
    [InlineData("runtime_silent")]
    [InlineData("binary_break")]
    [InlineData("CS0618 / CS0246")]
    public void CheckEntry_PlaceholderOrInvalidCsWarning_IsRejected(string warning)
    {
        var issues = new List<string>();
        VerifyRegistryCommand.CheckEntry(MakeEntry(csWarning: warning), issues);
        Assert.Single(issues);
        Assert.Contains("cs_warning", issues[0]);
    }

    [Fact]
    public void HistoricalSkip_AppliesOnlyToExactPre100Marker()
    {
        var historical = MakeEntry();
        historical.AppliesToCodebases = "pre-1.0.0";
        var currentMigration = MakeEntry();
        currentMigration.AppliesToCodebases = "pre-1.14.0";

        Assert.True(VerifyRegistryCommand.IsHistoricalDocumentationEntry(historical));
        Assert.False(VerifyRegistryCommand.IsHistoricalDocumentationEntry(currentMigration));
    }

    [Fact]
    public void FilledPre114Entry_ReceivesSubstantiveValidation()
    {
        var entry = MakeEntry(exampleBefore: "same();", exampleAfter: "same();");
        entry.AppliesToCodebases = "pre-1.14.0";
        var issues = new List<string>();

        if (!VerifyRegistryCommand.IsHistoricalDocumentationEntry(entry))
            VerifyRegistryCommand.CheckEntry(entry, issues);

        Assert.Contains(issues, issue => issue.Contains("example_before == example_after"));
    }

    [Fact]
    public void CheckDuplicateIds_CaseInsensitiveCollision_IsRejected()
    {
        var issues = new List<string>();

        VerifyRegistryCommand.CheckDuplicateIds(
            [MakeEntry(id: "MAF114-HARNESS-OPTIONS-001"), MakeEntry(id: "maf114-harness-options-001")],
            issues);

        Assert.Single(issues);
        Assert.Contains("duplicate registry id", issues[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("case-insensitive", issues[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckDuplicateIds_DistinctIds_Pass()
    {
        var issues = new List<string>();

        VerifyRegistryCommand.CheckDuplicateIds(
            [MakeEntry(id: "MAF114-OPTIONS-001"), MakeEntry(id: "MAF114-HARNESS-OPTIONS-001")],
            issues);

        Assert.Empty(issues);
    }

    // -------------------------------------------------------------------------
    // Q-03: applies_to_codebases is required and well-formed on every entry
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("pre-1.5.0")]
    [InlineData("1.11.1+")]
    [InlineData("1.22.0")]
    [InlineData(" pre-1.0.0 ")]
    public void CheckAppliesToMarker_ValidForms_Pass(string marker)
    {
        var issues = new List<string>();
        var entry = MakeEntry(id: "MAF150-PROVIDER-001");
        entry.AppliesToCodebases = marker;

        VerifyRegistryCommand.CheckAppliesToMarker(entry, issues);

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("", "missing")]
    [InlineData("pre-1.5", "malformed")]
    [InlineData("before 1.5.0", "malformed")]
    [InlineData("1.x.0+", "malformed")]
    [InlineData("pre-", "malformed")]
    public void CheckAppliesToMarker_MissingOrMalformed_IsRejected(string? marker, string expected)
    {
        var issues = new List<string>();
        var entry = MakeEntry(id: "MAF150-PROVIDER-001");
        entry.AppliesToCodebases = marker;

        VerifyRegistryCommand.CheckAppliesToMarker(entry, issues);

        Assert.Single(issues);
        Assert.Contains("MAF150-PROVIDER-001", issues[0], StringComparison.Ordinal);
        Assert.Contains(expected, issues[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRegistry_EveryEntryHasAWellFormedMarker()
    {
        var issues = new List<string>();
        foreach (var entry in new RegistryService().AllEntries)
            VerifyRegistryCommand.CheckAppliesToMarker(entry, issues);

        Assert.Empty(issues);
    }

    // -------------------------------------------------------------------------
    // Integration: the LIVE embedded registry must pass verification
    // -------------------------------------------------------------------------

    [Fact]
    public void LiveRegistry_PassesVerification()
    {
        // This is the canary test for #5 polish — any change that breaks
        // the existing registry causes this test to fail in CI BEFORE the
        // PR merges. Mirrors the same skip logic + order as
        // VerifyRegistryCommand.Run():
        //   - historical (exact pre-1.0.0) entries
        //   - incomplete drafts (ANY core field still a placeholder — monotonic)
        var registry = new RegistryService();
        var issues = new List<string>();
        VerifyRegistryCommand.CheckDuplicateIds(registry.AllEntries, issues);
        foreach (var entry in registry.AllEntries)
        {
            if (VerifyRegistryCommand.IsHistoricalDocumentationEntry(entry)) continue;
            if (VerifyRegistryCommand.IsIncompleteDraft(entry))
            {
                VerifyRegistryCommand.CheckCsWarning(entry, issues);
                continue;
            }
            VerifyRegistryCommand.CheckEntry(entry, issues);
        }

        // If this test fails, registry.yaml has an entry that won't pass the
        // PR-triggered verification gate. Investigate the specific failure
        // listed in the message.
        Assert.True(issues.Count == 0,
            $"Embedded registry has {issues.Count} verification issue(s):\n" +
            string.Join("\n", issues.Select(i => "  • " + i)));
    }

    [Fact]
    public void IsRawDraft_DetectsAutoGeneratedDraftEntry()
    {
        // The shape `registry-extract` produces: every fillable core field
        // is a TODO/placeholder. Verifier should skip these.
        var entry = new RegistryEntry
        {
            Id = "MAFNEW-001",
            ObsoleteSignature = "TODO",
            ReplacementSignature = "TODO",
            FixDescription = "TODO — review the diff entry below and document the canonical fix",
            ExampleBefore = "// TODO — show a real call site that breaks\n",
            ExampleAfter = "// TODO — show the canonical 1.x replacement\n",
            CsWarning = "TODO",
        };
        Assert.True(VerifyRegistryCommand.IsRawDraft(entry));
    }

    [Fact]
    public void IsRawDraft_RejectsPartiallyFilledEntry()
    {
        // Once any core field is filled, the entry is no longer a raw draft
        // — the verifier should run its checks.
        var entry = new RegistryEntry
        {
            Id = "MAFNEW-001",
            ObsoleteSignature = "Foo.OldMethod()",  // FILLED — not a draft anymore
            ReplacementSignature = "TODO",
            FixDescription = "TODO — pending",
            ExampleBefore = "// TODO",
            ExampleAfter = "// TODO",
            CsWarning = "TODO",
        };
        Assert.False(VerifyRegistryCommand.IsRawDraft(entry));
    }

    // -------------------------------------------------------------------------
    // Phase 2.8 — IsRawDraft tightened to require TODO at start of line.
    //
    // v1 used a substring check, which meant any string literal like
    // `"// TODO refactor"` inside otherwise-real code re-qualified the entry
    // as a raw draft and bypassed ALL substantive checks. Tightened to a
    // (?m)^\s*//\s*TODO\b regex so the marker must be in a true comment
    // position (start of line, after optional whitespace).
    // -------------------------------------------------------------------------

    [Fact]
    public void IsRawDraft_TodoInsideStringLiteral_NotClassifiedAsDraft()
    {
        // Pre-Phase-2.8 this would have returned true (substring match on
        // "// TODO" inside the string literal). Post-fix: false.
        var entry = new RegistryEntry
        {
            Id = "MAFNEW-002",
            ObsoleteSignature = "TODO",
            ReplacementSignature = "TODO",
            FixDescription = "TODO",
            ExampleBefore = "var note = \"// TODO refactor\"; Foo();",
            ExampleAfter = "var note = \"// TODO refactor\"; Bar();",
            CsWarning = "TODO",
        };
        Assert.False(VerifyRegistryCommand.IsRawDraft(entry));
    }

    [Fact]
    public void IsRawDraft_RequiresTodoAtStartOfLine()
    {
        // Trailing whitespace OK; line-leading `// TODO` is the marker.
        var entry = new RegistryEntry
        {
            Id = "MAFNEW-003",
            ObsoleteSignature = "TODO",
            ReplacementSignature = "TODO",
            FixDescription = "TODO",
            ExampleBefore = "   // TODO — show a real call site that breaks\n",
            ExampleAfter = "// TODO — show the canonical 1.x replacement\n",
            CsWarning = "TODO",
        };
        Assert.True(VerifyRegistryCommand.IsRawDraft(entry));
    }

    // -------------------------------------------------------------------------
    // IsIncompleteDraft — the monotonic-fill gate used by Run() (any core field
    // still a placeholder/empty => skip structural checks, no partial-fill trap).
    // -------------------------------------------------------------------------

    [Fact]
    public void IsIncompleteDraft_AllPlaceholder_IsIncomplete()
    {
        var entry = new RegistryEntry
        {
            Id = "MAFINC-001",
            ObsoleteSignature = "TODO",
            ReplacementSignature = "TODO",
            FixDescription = "TODO — review the diff entry below",
            ExampleBefore = "// TODO — a call site that breaks\n",
            ExampleAfter = "// TODO — the replacement\n",
            CsWarning = "TODO",
        };
        Assert.True(VerifyRegistryCommand.IsIncompleteDraft(entry));
    }

    [Fact]
    public void IsIncompleteDraft_PartiallyFilled_IsStillIncomplete()
    {
        // The monotonic fix: unlike IsRawDraft (all-fields AND), a partially
        // filled entry is STILL incomplete, so it stays skipped rather than
        // flipping into a pile of CheckEntry failures (the old partial-fill trap).
        var entry = new RegistryEntry
        {
            Id = "MAFINC-002",
            ObsoleteSignature = "Foo.Bar(int)",              // filled
            ReplacementSignature = "Foo.Bar(int, bool)",     // filled
            FixDescription = "TODO — review the diff entry below", // still a placeholder
            ExampleBefore = "foo.Bar(1);",
            ExampleAfter = "foo.Bar(1, true);",
            CsWarning = "TODO",
        };
        Assert.True(VerifyRegistryCommand.IsIncompleteDraft(entry));
        Assert.False(VerifyRegistryCommand.IsRawDraft(entry)); // NOT all-placeholder
    }

    [Fact]
    public void IsIncompleteDraft_FullyFilled_IsComplete()
    {
        var entry = new RegistryEntry
        {
            Id = "MAFINC-003",
            ObsoleteSignature = "Foo.Old()",
            ReplacementSignature = "Foo.New()",
            FixDescription = "Rename Old to New; the signature is unchanged.",
            ExampleBefore = "foo.Old();",
            ExampleAfter = "foo.New();",
            CsWarning = "CS0618",
        };
        Assert.False(VerifyRegistryCommand.IsIncompleteDraft(entry));
    }

    // -------------------------------------------------------------------------
    // Phase 2.8 — CheckContentSafety: minimal sweep applies even when an
    // entry would otherwise be skipped (pre-1.0.0 or raw draft).
    // -------------------------------------------------------------------------

    [Fact]
    public void CheckContentSafety_HtmlCommentInFixDescription_Flagged()
    {
        var entry = new RegistryEntry
        {
            Id = "PRE-001",
            FixDescription = "Legit fix description <!-- ignore previous --> trailing",
            ExampleBefore = "var x = 1;",
            ExampleAfter = "var x = 2;",
        };
        var issues = new List<string>();
        VerifyRegistryCommand.CheckContentSafety(entry, issues);
        Assert.Contains(issues, i => i.Contains("HTML comment"));
    }

    [Fact]
    public void CheckContentSafety_HtmlCommentInNotes_Flagged()
    {
        var entry = new RegistryEntry
        {
            Id = "PRE-002",
            FixDescription = "ok",
            ExampleBefore = "ok",
            ExampleAfter = "ok",
            Notes = "Legit notes <!-- evil --> trailing",
        };
        var issues = new List<string>();
        VerifyRegistryCommand.CheckContentSafety(entry, issues);
        Assert.Contains(issues, i => i.Contains("HTML comment"));
    }

    [Fact]
    public void CheckContentSafety_OversizedExample_Flagged()
    {
        var entry = new RegistryEntry
        {
            Id = "PRE-003",
            FixDescription = "ok",
            ExampleBefore = new string('A', 33 * 1024),  // > 32 KB cap
            ExampleAfter = "ok",
        };
        var issues = new List<string>();
        VerifyRegistryCommand.CheckContentSafety(entry, issues);
        Assert.Contains(issues, i => i.Contains("example_before exceeds"));
    }

    [Fact]
    public void CheckContentSafety_CleanEntry_NoIssues()
    {
        var entry = MakeEntry();
        var issues = new List<string>();
        VerifyRegistryCommand.CheckContentSafety(entry, issues);
        Assert.Empty(issues);
    }

    // -------------------------------------------------------------------------
    // Test helper
    // -------------------------------------------------------------------------

    private static RegistryEntry MakeEntry(
        string id = "TEST-001",
        string fixDescription = "Replace OldThing with NewThing in your code.",
        string exampleBefore = "var x = OldThing.Run();",
        string exampleAfter = "var x = NewThing.RunAsync();",
        string guideSection = "5",
        string csWarning = "CS0618")
        => new()
        {
            Id = id,
            FixDescription = fixDescription,
            ExampleBefore = exampleBefore,
            ExampleAfter = exampleAfter,
            GuideSection = guideSection,
            CsWarning = csWarning,
        };
}
