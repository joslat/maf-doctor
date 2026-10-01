using MafDoctor.Tools;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MafDoctor.Data;

/// <summary>
/// Loads and queries the MAF obsolete-API registry.
///
/// Load order:
///   1. MAF_REGISTRY_PATH environment variable — for local dev override or custom registries.
///   2. Embedded resource — bundled at build time from
///      .github/skills/maf-obsolete-api-registry/registry.yaml.
///      This ensures the tool works standalone as a NuGet global tool without external file access.
/// </summary>
public sealed class RegistryService
{
    private readonly Registry _registry;

    /// <summary>
    /// Pre-built lowercase haystack per entry, covering every searchable string field.
    /// Centralising the field list here means adding a new field is a single-line change
    /// (in <see cref="BuildHaystack"/>) instead of an OR-chain that grew bug-by-bug.
    /// </summary>
    private readonly Dictionary<RegistryEntry, string> _haystacks;

    public RegistryService()
    {
        _registry = Load();
        _haystacks = _registry.Entries.ToDictionary(e => e, BuildHaystack);
        // WM-39: compute ONCE (get-only) instead of re-sorting + re-materializing on
        // every access. StringComparer.Ordinal matches RegistryServiceTests' assertion
        // (the previous culture-sensitive OrderBy coincidentally agreed only for ASCII IDs).
        AllIds = _registry.Entries.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>All entry IDs in the registry, ordinal-sorted. Computed once in the ctor.</summary>
    public IReadOnlyList<string> AllIds { get; }

    /// <summary>Every entry in the registry (in YAML declaration order). Read-only.</summary>
    public IReadOnlyList<RegistryEntry> AllEntries => _registry.Entries;

    /// <summary>MAF version this registry targets (e.g. the latest stable release).</summary>
    public string TargetVersion => _registry.TargetMafVersion;

    /// <summary>Date the registry was last updated.</summary>
    public string LastUpdated => _registry.LastUpdated;

    /// <summary>Find an entry by exact ID (case-insensitive).</summary>
    public RegistryEntry? FindById(string id) =>
        _registry.Entries.FirstOrDefault(e =>
            e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Search for entries where the API name appears anywhere in the entry's textual
    /// content (case-insensitive substring). Covers every string field — see
    /// <see cref="BuildHaystack"/> for the list. To extend, edit that one method.
    /// </summary>
    public IReadOnlyList<RegistryEntry> SearchByApiName(string apiName)
    {
        if (string.IsNullOrWhiteSpace(apiName))
            return [];

        // Phase 5.10 — DoS guard. A 1 GB `apiName` would force `ToLowerInvariant`
        // to materialize a 1 GB copy, then `Contains` to walk it against every
        // haystack. The cap is generous (256 bytes — wider than any real API
        // name our registry contains) but bounded enough to neutralize abuse.
        //
        // Phase 5.G fixup — route through BoundedInput.Validate so the cap is
        // BYTE-count (not UTF-16 char count). The string `apiName.Length > 256`
        // check would have allowed a 256-emoji string = 1024 UTF-8 bytes,
        // inconsistent with the IdentifierBytes contract used elsewhere.
        //
        // Cross-layer dep `Data → Tools` is intentional: BoundedInput is a
        // primitive (length-cap helper), not a feature module. See
        // CONTRIBUTING.md "Helper composition — decision tree" for the
        // rationale. The dependency graph stays acyclic: Tools does not
        // depend on Data (verified by `using` grep).
        Tools.BoundedInput.Validate(apiName, Tools.BoundedInput.IdentifierBytes, nameof(apiName));

        var needle = apiName.Trim().ToLowerInvariant();
        return _registry.Entries
            .Where(e => _haystacks[e].Contains(needle, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Return entries whose <see cref="RegistryEntry.AppliesToCodebases"/> marker
    /// is satisfied by the given codebase version. Entries with a null/empty
    /// marker are always returned (default = "applies to any").
    ///
    /// Marker grammar:
    ///   - <c>"pre-X.Y.Z"</c>  → entry applies when <paramref name="codebaseVersion"/> &lt; X.Y.Z.
    ///   - <c>"X.Y.Z+"</c>     → entry applies when <paramref name="codebaseVersion"/> &gt;= X.Y.Z.
    ///   - <c>"X.Y.Z"</c>      → entry applies when <paramref name="codebaseVersion"/> == X.Y.Z.
    ///   - anything else       → defensively treated as "applies to any" (logs a warning in v2).
    ///
    /// Version comparison uses <see cref="Version"/> semantics (build-revision-aware,
    /// so "1.10.0" sorts AFTER "1.9.0" correctly). Prerelease tags (-alpha, -beta) are stripped
    /// before parsing — close enough for v1; sufficient for SemVer-ish MAF versions.
    /// </summary>
    public IReadOnlyList<RegistryEntry> GetEntriesForCodebaseVersion(string codebaseVersion)
    {
        if (!TryParseVersion(codebaseVersion, out var queryVersion))
            return _registry.Entries.ToList(); // bad input → return all, don't filter

        return _registry.Entries
            .Where(e => AppliesTo(e, queryVersion))
            .ToList();
    }

    internal static bool AppliesTo(RegistryEntry e, Version queryVersion)
    {
        var marker = e.AppliesToCodebases?.Trim();
        if (string.IsNullOrEmpty(marker))
            return true; // no marker → applies to any version

        // "pre-X.Y.Z" — applies when query < X.Y.Z.
        if (marker.StartsWith("pre-", StringComparison.OrdinalIgnoreCase))
        {
            var verPart = marker.Substring(4);
            return TryParseVersion(verPart, out var threshold) && queryVersion < threshold;
        }

        // "X.Y.Z+" — applies when query >= X.Y.Z.
        if (marker.EndsWith('+'))
        {
            var verPart = marker.Substring(0, marker.Length - 1);
            return TryParseVersion(verPart, out var threshold) && queryVersion >= threshold;
        }

        // Exact "X.Y.Z" — applies only at that version.
        if (TryParseVersion(marker, out var exact))
            return queryVersion == exact;

        // Unknown marker shape — defensively include the entry.
        return true;
    }

    /// <summary>
    /// Parses a SemVer-ish string into <see cref="Version"/>. Strips any prerelease
    /// suffix (`-alpha`, `-beta`, `-rc`, `-preview`) before parsing so "1.3.0-alpha-5" → 1.3.0.
    /// </summary>
    private static bool TryParseVersion(string raw, out Version version)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            version = new Version(0, 0);
            return false;
        }

        var dashIdx = raw.IndexOf('-');
        var core = dashIdx > 0 ? raw.Substring(0, dashIdx) : raw;
        return Version.TryParse(core, out version!);
    }

    /// <summary>
    /// Concatenates every searchable string field on a registry entry into a single
    /// lowercase blob. **This is the canonical "what is searchable" definition** — add
    /// new fields here and only here.
    ///
    /// Note: <see cref="RegistryEntry.AppliesToCodebases"/> is intentionally EXCLUDED —
    /// it's a structural marker, not API content, and including it would confuse the
    /// CS0618 hunter's symbol-based matching.
    /// </summary>
    private static string BuildHaystack(RegistryEntry e) =>
        string.Join(
            '\n',
            e.Id,
            e.Package,
            e.Type,
            e.Method,
            e.ObsoleteSignature,
            e.ReplacementSignature,
            e.FixDescription,
            e.ExampleBefore,
            e.ExampleAfter,
            e.CsWarning,
            e.GuideSection,
            e.Notes).ToLowerInvariant();

    // -------------------------------------------------------------------------
    // Formatting helpers used by tools
    // -------------------------------------------------------------------------

    public static string FormatEntry(RegistryEntry e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Registry entry: {e.Id}");
        sb.AppendLine();
        sb.AppendLine($"**Package:** `{e.Package}`  ");
        sb.AppendLine($"**Type:** `{e.Type}`  ");
        sb.AppendLine($"**Method/API:** `{e.Method}`  ");
        sb.AppendLine($"**Warning:** `{e.CsWarning}`  ");
        sb.AppendLine($"**Guide section:** {e.GuideSection}  ");
        sb.AppendLine($"**dotnet-inspect detectable:** {(e.DotnetInspectDetectable ? "Yes" : "**No** — compiler only")}  ");
        if (e.ArgumentOrderChange)
            sb.AppendLine("**⚠️ Argument order change:** Yes — swapping arguments may compile and run silently with wrong behaviour.");
        sb.AppendLine();
        sb.AppendLine($"### Obsolete (broken)");
        sb.AppendLine($"`{e.ObsoleteSignature}`");
        sb.AppendLine();
        sb.AppendLine($"### Replacement (correct)");
        sb.AppendLine($"`{e.ReplacementSignature}`");
        sb.AppendLine();
        sb.AppendLine($"### Fix");
        sb.AppendLine(Sanitize(e.FixDescription));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(e.ExampleBefore))
        {
            sb.AppendLine("### Before");
            sb.AppendLine("```csharp");
            sb.AppendLine(Sanitize(e.ExampleBefore));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(e.ExampleAfter))
        {
            sb.AppendLine("### After");
            sb.AppendLine("```csharp");
            sb.AppendLine(Sanitize(e.ExampleAfter));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(e.Notes))
        {
            sb.AppendLine("### Notes");
            sb.AppendLine(Sanitize(e.Notes));
        }
        return sb.ToString();
    }

    /// <summary>
    /// SEC-07: sanitize an upstream-derived free-text field before markdown embedding.
    /// Strips HTML comments (a smuggle vector — never legitimate in registry text) AND
    /// neutralizes triple-backtick fences. Applied to EVERY upstream free-text field
    /// (fix_description, example_before/after, notes) so none can inject markdown or
    /// escape a code fence — the Example fields previously neutralized fences but not
    /// HTML comments, and fix_description/notes were echoed entirely raw.
    /// </summary>
    private static string Sanitize(string s) =>
        NeutralizeFences(MafDoctor.Tools.LlmFencing.StripHtmlComments(s.Trim()));

    /// <summary>
    /// Neutralize triple-backtick fences embedded inside an example so the
    /// surrounding markdown code-fence cannot be broken out of. Inserts a
    /// zero-width space between the three backticks — invisible to a human
    /// reader but the markdown parser no longer sees a fence terminator.
    ///
    /// Phase 2.9 — without this, an attacker-controlled <c>example_before</c>
    /// containing <c>```yaml\nevil: true\n```</c> would close the enclosing
    /// fence and render the attacker's content as live markdown / executable
    /// instructions in the LLM context.
    /// </summary>
    internal static string NeutralizeFences(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        // U+200B zero-width space between the backticks: humans see ```, the
        // markdown parser sees three separate non-fence backticks.
        return source.Replace("```", "`​`​`");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static Registry Load()
    {
        var yaml = ReadYaml();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance) // we use explicit [YamlMember] aliases
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<Registry>(yaml);
    }

    // Security: cap the env-var-supplied registry size at 5 MB. The embedded registry
    // is ~50 KB; the env-var path is a developer convenience for local registry overrides.
    // A multi-gigabyte file silently dropped at the override path would OOM the host on
    // first MCP request. Real registries are KB-scale.
    private const long RegistryMaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Validate the MAF_REGISTRY_PATH env-var-supplied path before reading.
    /// Phase 5.5 of the v1.1 security hardening release. Rejects shell
    /// metachars, NUL/CR/LF, symlinks, and (optionally) paths outside an
    /// allowlist declared via MAF_REGISTRY_PATH_ROOTS.
    ///
    /// Promoted to <c>internal</c> in Phase 7.G fixup so the test assembly
    /// can exercise the guard directly (Phase 5.5 had no test coverage —
    /// surfaced by the final Opus review).
    /// </summary>
    internal static void ValidateOverridePath(string envPath)
    {
        if (envPath.IndexOfAny(['\0', '\r', '\n', '"', ';', '|', '&']) >= 0)
            throw new InvalidOperationException(
                "MAF_REGISTRY_PATH contains invalid characters (NUL / CR / LF / shell metacharacters). Refusing to load.");

        // Reject reparse-point at the file itself. The env var is meant to
        // point at a LOCAL override file, not a symlink-redirect to an
        // arbitrary location on the host.
        if (File.Exists(envPath))
        {
            var attrs = File.GetAttributes(envPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "MAF_REGISTRY_PATH points to a symlink / reparse point. Refusing to follow — use the canonical absolute path.");
        }

        // Optional allowlist: MAF_REGISTRY_PATH_ROOTS = ";"-separated parent
        // directories the override path must canonicalize under.
        //
        // OS-conditional path comparison — same rationale as PathGuard.PathComparison:
        // POSIX paths are case-sensitive (`/etc/Foo` and `/etc/foo` differ);
        // using OrdinalIgnoreCase unconditionally would let a case-permuted
        // allowlist root accept a different directory.
        var allowlist = Environment.GetEnvironmentVariable("MAF_REGISTRY_PATH_ROOTS");
        if (!string.IsNullOrWhiteSpace(allowlist))
        {
            var pathComparison = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var resolved = Path.GetFullPath(envPath);
            var roots = allowlist.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? matchedRoot = null;
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (resolved.StartsWith(rootFull + Path.DirectorySeparatorChar, pathComparison)
                    || resolved.Equals(rootFull, pathComparison))
                {
                    matchedRoot = rootFull;
                    break;
                }
            }
            if (matchedRoot is null)
                throw new InvalidOperationException(
                    "MAF_REGISTRY_PATH is outside the allowlist declared by MAF_REGISTRY_PATH_ROOTS. Refusing to load.");

            // F-22 — Path.GetFullPath only collapses `.`/`..` syntactically; it does not
            // resolve symlinks. A path like "<allowed-root>/link/registry.yaml", where
            // `link` is a symlinked directory pointing outside the root and the leaf file
            // itself is a regular file, satisfies both the leaf check above and the
            // prefix check just above — but physically resolves outside the declared
            // root. Walk every existing parent segment from the resolved file up to (but
            // not including) the matched root and reject any reparse point in that chain.
            if (ParentChainHasReparsePoint(resolved, matchedRoot, pathComparison))
                throw new InvalidOperationException(
                    "MAF_REGISTRY_PATH traverses a symlink/reparse point inside the allowlisted root. Refusing to load.");
        }
    }

    /// <summary>
    /// Walks every existing directory segment from <paramref name="resolvedLeaf"/>'s
    /// parent up to (but not including) <paramref name="rootFull"/>, returning
    /// <c>true</c> if any segment is a symlink/reparse point. Mirrors the parent-chain
    /// probe in <see cref="PathGuard.ValidateContainment"/>.
    /// </summary>
    private static bool ParentChainHasReparsePoint(string resolvedLeaf, string rootFull, StringComparison pathComparison)
    {
        var startPath = File.Exists(resolvedLeaf)
            ? Path.GetDirectoryName(resolvedLeaf) ?? rootFull
            : resolvedLeaf;
        var probe = new DirectoryInfo(startPath);

        while (probe is not null
            && !probe.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(rootFull, pathComparison))
        {
            if (probe.Exists && (probe.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            probe = probe.Parent;
        }

        return false;
    }

    private static string ReadYaml()
    {
        // 1. Developer override via environment variable.
        var envPath = Environment.GetEnvironmentVariable("MAF_REGISTRY_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            // Phase 5.5 — guard the env-var override path.
            //
            // Pre-Phase-5.5 the only check was a 5 MB byte-size cap. The env
            // var is set by whoever owns the parent process — a malicious VS
            // Code extension, a misconfigured CI runner, a developer who
            // copy-pasted from a tutorial — and the value flows straight
            // into `File.ReadAllText` without any path-hygiene check.
            //
            // Reject conditions (in addition to the existing size cap):
            //   - Shell metacharacters / NUL / CR / LF (defense in depth;
            //     File.ReadAllText doesn't shell-interpret, but the value
            //     also flows into error messages + telemetry).
            //   - Symlink / reparse-point at the file itself. The env-var
            //     path is a developer override for a LOCAL registry override;
            //     resolving through a symlink to anywhere else on the host
            //     is unexpected and a soft path-escape lane.
            //
            // Per §10 decision: an optional MAF_REGISTRY_PATH_ROOTS env var
            // can declare a `;`-separated allowlist of acceptable parent
            // directories. When unset, only the metachar + reparse-point
            // checks apply.
            ValidateOverridePath(envPath);

            var info = new FileInfo(envPath);
            if (info.Length > RegistryMaxBytes)
                throw new InvalidOperationException(
                    $"MAF_REGISTRY_PATH file is {info.Length / 1024} KB — exceeds the {RegistryMaxBytes / 1024 / 1024} MB safety cap. " +
                    "Real MAF registries are KB-scale; refusing to load.");
            return File.ReadAllText(envPath);
        }

        // 2. Embedded resource (bundled at build time — works standalone as a NuGet tool).
        var asm = typeof(RegistryService).Assembly;
        using var stream = asm.GetManifestResourceStream("registry.yaml");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        throw new InvalidOperationException(
            "Cannot locate registry.yaml. " +
            "Set the MAF_REGISTRY_PATH environment variable to the absolute path of the registry file, " +
            "or rebuild the tool so the file is embedded as a resource.");
    }
}
