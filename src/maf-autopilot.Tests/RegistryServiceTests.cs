using System.Globalization;
using MafDoctor.Data;
using Xunit;

namespace MafDoctor.Tests;

/// <summary>
/// Tests for RegistryService — the single most regression-prone surface in the server.
///
/// The "AgentThread → SAFE" bug fixed in commit 393b7a2 would have been caught by
/// <see cref="SearchByApiName_AgentThread_FindsThreadEntry"/>. Future cross-field gaps are
/// guarded by parametric tests over Notes, FixDescription, ExampleBefore.
/// </summary>
public class RegistryServiceTests
{
    private readonly RegistryService _registry = new();

    // -------------------------------------------------------------------------
    // Load / metadata
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_FromEmbeddedResource_PopulatesEntries()
    {
        Assert.NotEmpty(_registry.AllIds);
        Assert.Matches(@"^\d+\.\d+\.\d+$", _registry.TargetVersion);
        Assert.True(DateOnly.TryParseExact(
            _registry.LastUpdated,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _));
    }

    [Fact]
    public void AllIds_AreSortedAndUnique()
    {
        var ids = _registry.AllIds.ToList();
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // -------------------------------------------------------------------------
    // FindById
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("MAF130-FAN-IN-001")]
    [InlineData("maf130-fan-in-001")]   // case-insensitive
    [InlineData("MAF130-Fan-In-001")]   // mixed case
    public void FindById_KnownEntry_AnyCasing_Returns(string id)
    {
        var entry = _registry.FindById(id);
        Assert.NotNull(entry);
        Assert.Equal("MAF130-FAN-IN-001", entry!.Id, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MAF130-DOES-NOT-EXIST")]
    [InlineData("FAN-IN-001")]    // missing MAF130 prefix
    public void FindById_Unknown_ReturnsNull(string id)
    {
        Assert.Null(_registry.FindById(id));
    }

    // -------------------------------------------------------------------------
    // SearchByApiName — regression-prone surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Direct regression test for commit 393b7a2. Before the fix, searching "AgentThread"
    /// returned SAFE because the term only appeared in `notes` / `fix_description`,
    /// which were not in the OR-chain.
    /// </summary>
    [Fact]
    public void SearchByApiName_AgentThread_FindsThreadEntry()
    {
        var matches = _registry.SearchByApiName("AgentThread");
        Assert.NotEmpty(matches);
        Assert.Contains(matches, e => e.Id.Contains("THREAD", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("AddFanInBarrierEdge")]
    [InlineData("SerializeSession")]
    [InlineData("ChatClientAgent")]
    [InlineData("StreamAsync")]
    [InlineData("AgentRunUpdateEvent")]
    public void SearchByApiName_KnownSurface_FindsAtLeastOne(string term)
    {
        var matches = _registry.SearchByApiName(term);
        Assert.NotEmpty(matches);
    }

    [Fact]
    public void SearchByApiName_GibberishTerm_ReturnsEmpty()
    {
        var matches = _registry.SearchByApiName("zzzNotAnApiAnywhereInTheRegistry");
        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("agentthread")]   // all lowercase
    [InlineData("AGENTTHREAD")]   // all uppercase
    [InlineData("AgentTHREAD")]   // mixed
    public void SearchByApiName_IsCaseInsensitive(string term)
    {
        var matches = _registry.SearchByApiName(term);
        Assert.NotEmpty(matches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SearchByApiName_NullOrEmpty_ReturnsEmpty(string? term)
    {
        var matches = _registry.SearchByApiName(term!);
        Assert.Empty(matches);
    }

    // -------------------------------------------------------------------------
    // Coverage of all string fields — guards the bug class fixed by the
    // 2026-05-11 inverted-haystack refactor (plan #37).
    //
    // Pre-refactor the OR-chain missed ExampleAfter, Package, Id, GuideSection,
    // CsWarning. Each test below targets a field that the original implementation
    // failed to search.
    // -------------------------------------------------------------------------

    /// <summary>"CreateSessionAsync" lives in `example_after` — pre-refactor: missed.</summary>
    [Fact]
    public void SearchByApiName_TermInExampleAfter_FindsEntry()
    {
        var matches = _registry.SearchByApiName("CreateSessionAsync");
        Assert.NotEmpty(matches);
    }

    /// <summary>Searching by package name should find entries from that package.</summary>
    [Theory]
    [InlineData("Microsoft.Agents.AI.Workflows")]
    [InlineData("Microsoft.Agents.AI")]
    public void SearchByApiName_TermInPackage_FindsEntry(string package)
    {
        var matches = _registry.SearchByApiName(package);
        Assert.NotEmpty(matches);
    }

    /// <summary>Searching by ID substring should find that entry.</summary>
    [Theory]
    [InlineData("FAN-IN")]
    [InlineData("THREAD")]
    [InlineData("MAF130")]
    public void SearchByApiName_TermInId_FindsEntry(string fragment)
    {
        var matches = _registry.SearchByApiName(fragment);
        Assert.NotEmpty(matches);
    }

    /// <summary>CS warning codes should be searchable so callers can ask "all CS0246 entries".</summary>
    [Theory]
    [InlineData("CS0618")]
    [InlineData("CS0246")]
    public void SearchByApiName_TermInCsWarning_FindsEntry(string warning)
    {
        var matches = _registry.SearchByApiName(warning);
        Assert.NotEmpty(matches);
    }

    // -------------------------------------------------------------------------
    // AppliesToCodebases — Phase W.1 / Phase T marker field
    // -------------------------------------------------------------------------

    /// <summary>
    /// The Phase T corrections marked these entries with `applies_to_codebases`; a binary
    /// check (2026-10-02) then showed the top-level Instructions property was already gone
    /// before 1.0 GA. Verify the YAML loader round-trips the field as a typed property.
    /// </summary>
    [Theory]
    [InlineData("MAF130-SESSION-001", "pre-1.3.0")]
    [InlineData("MAF130-INSTRUCTIONS-001", "pre-1.0.0")]
    public void AppliesToCodebases_PhaseTEntries_LoadWithTheirMarker(string id, string marker)
    {
        var entry = _registry.FindById(id);
        Assert.NotNull(entry);
        Assert.Equal(marker, entry!.AppliesToCodebases);
    }

    /// <summary>
    /// Q-03: every live entry carries a marker (verify-registry enforces it), so
    /// the former unmarked MAF130/MAF140/MAF150 entries now load with pre-X.
    /// </summary>
    [Theory]
    [InlineData("MAF130-FAN-IN-001", "pre-1.3.0")]
    [InlineData("MAF130-EXEC-001", "pre-1.3.0")]
    [InlineData("MAF140-AGENT-001", "pre-1.4.0")]
    [InlineData("MAF150-PROVIDER-001", "pre-1.5.0")]
    public void AppliesToCodebases_FormerlyUnmarkedEntries_NowCarryMarkers(string id, string marker)
    {
        var entry = _registry.FindById(id);
        Assert.NotNull(entry);
        Assert.Equal(marker, entry!.AppliesToCodebases);
    }

    /// <summary>
    /// "pre-1.3.0"-marked entries apply against a pre-1.3 codebase (e.g. 1.2.5)
    /// AND DON'T apply against a 1.3.0+ codebase.
    /// </summary>
    [Theory]
    [InlineData("1.2.0", true)]   // pre-1.3 → marker applies
    [InlineData("1.2.5", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1.3.0", false)]  // not pre-1.3 → marker excludes
    [InlineData("1.3.5", false)]
    [InlineData("1.4.0", false)]
    public void GetEntriesForCodebaseVersion_Pre130Marker_FiltersCorrectly(string queryVersion, bool shouldInclude)
    {
        var entries = _registry.GetEntriesForCodebaseVersion(queryVersion);
        var sessionEntry = entries.FirstOrDefault(e => e.Id == "MAF130-SESSION-001");

        if (shouldInclude)
            Assert.NotNull(sessionEntry);
        else
            Assert.Null(sessionEntry);
    }

    /// <summary>
    /// Entries without the marker apply to EVERY codebase version
    /// (default = "applies to any"). The live registry has none any more
    /// (Q-03), so the rule is pinned with a synthetic entry.
    /// </summary>
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.3.0")]
    [InlineData("1.5.0")]
    [InlineData("2.0.0")]
    public void AppliesTo_UnmarkedEntry_AppliesToEveryVersion(string queryVersion)
    {
        var entry = new RegistryEntry { Id = "SYNTHETIC-001", AppliesToCodebases = null };
        Assert.True(RegistryService.AppliesTo(entry, Version.Parse(queryVersion)));
    }

    /// <summary>
    /// Invalid version inputs should not throw — defensively return all entries.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("abc")]
    public void GetEntriesForCodebaseVersion_InvalidInput_ReturnsAll(string queryVersion)
    {
        var entries = _registry.GetEntriesForCodebaseVersion(queryVersion);
        Assert.Equal(_registry.AllIds.Count, entries.Count);
    }

    /// <summary>
    /// Prerelease tags should be stripped before comparison so "1.3.0-alpha-5"
    /// compares as 1.3.0 (excluding pre-1.3 entries).
    /// </summary>
    [Fact]
    public void GetEntriesForCodebaseVersion_PrereleaseTag_StrippedCorrectly()
    {
        var entries = _registry.GetEntriesForCodebaseVersion("1.3.0-alpha-5");
        var sessionEntry = entries.FirstOrDefault(e => e.Id == "MAF130-SESSION-001");
        Assert.Null(sessionEntry); // 1.3.0-alpha-5 → 1.3.0, NOT pre-1.3
    }
}
