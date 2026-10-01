using System.Xml.Linq;

namespace MafDoctor.Tools;

/// <summary>A package reference found in a repository: NuGet id plus the version text as written.</summary>
internal sealed record DetectedPackage(string Id, string Version);

/// <summary>
/// Reads package references from every <c>.csproj</c> under a repository, resolving
/// Central Package Management pins from <c>Directory.Packages.props</c>. Pure XML:
/// no build, no restore. Shared by the Semantic Kernel detector and the MAF
/// coverage-horizon check.
/// </summary>
internal static class PackageVersionScanner
{
    internal const string Unpinned = "(unpinned)";

    // Round-2 review fixup (F-10/F-20) — matches ExplainFindingTool.MaxFileBytes /
    // DraftIssueTool.MaxCsprojBytes. XDocument.Load(path) reads AND parses the
    // whole file with no size limit; a pathologically large .csproj or
    // Directory.Packages.props would otherwise be fully materialized (twice
    // over — raw bytes, then the XML DOM) before either read below runs.
    internal const long MaxProjectFileBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Returns one entry per matching package id (case-insensitive), with every
    /// distinct pinned version joined by ", " when projects disagree, or
    /// <see cref="Unpinned"/> when no version can be resolved.
    /// </summary>
    internal static IReadOnlyList<DetectedPackage> Detect(string repoPath, Func<string, bool> includeId)
    {
        var packages = new List<DetectedPackage>();
        if (!Directory.Exists(repoPath))
            return packages;

        // Central Package Management: a csproj using CPM omits Version on its
        // PackageReference and the pin lives in a Directory.Packages.props
        // <PackageVersion Include="..." Version="..."/>. Read those first so a
        // CPM-managed package resolves to its real version, not "(unpinned)".
        var central = ReadCentralPackageVersions(repoPath);

        foreach (var csproj in SourceFileWalker.EnumerateCsprojFiles(repoPath))
        {
            if (new FileInfo(csproj).Length > MaxProjectFileBytes) continue;
            XDocument doc;
            try { doc = XDocument.Load(csproj); }
            catch { continue; } // malformed csproj — skip, don't fail the scan
            foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var id = (string?)pr.Attribute("Include") ?? (string?)pr.Attribute("Update");
                if (id is null || !includeId(id))
                    continue;
                // VersionOverride beats the central pin under CPM; then a local
                // Version attr/element; then the central pin; else genuinely unpinned.
                var version = (string?)pr.Attribute("VersionOverride")
                    ?? (string?)pr.Attribute("Version")
                    ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                    ?? (central.TryGetValue(id, out var cv) ? cv : null);
                packages.Add(new DetectedPackage(id, version ?? Unpinned));
            }
        }
        return packages
            // NuGet IDs are case-insensitive — group/order accordingly so casing variants
            // collapse to one entry.
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Prefer a pinned version over "(unpinned)". If projects pin the SAME id to
                // DIFFERENT versions, surface ALL of them (sorted, so the result is
                // deterministic across machines / enumeration order) rather than silently
                // picking whichever csproj happened to be walked first — a version mismatch
                // is exactly what migration planning needs to see.
                var pinned = g.Select(p => p.Version)
                    .Where(v => v != Unpinned)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .ToList();
                var version = pinned.Count switch
                {
                    0 => Unpinned,
                    1 => pinned[0],
                    _ => string.Join(", ", pinned),
                };
                return new DetectedPackage(g.Key, version);
            })
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads every <c>Directory.Packages.props</c> under the repo into an id→version
    /// map (Central Package Management). Used to resolve the version of a CPM-pinned
    /// package whose <c>PackageReference</c> deliberately omits <c>Version</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadCentralPackageVersions(string repoPath)
    {
        // NuGet IDs are case-insensitive, so a `PackageReference Include="microsoft.semantickernel"`
        // still resolves against a `PackageVersion Include="Microsoft.SemanticKernel"` pin.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> propsFiles;
        try
        {
            // Use the hardened shared walker (skips symlinks / hidden / system / bin / obj,
            // ignores inaccessible subdirs) rather than a raw recursive EnumerateFiles.
            // .OrderBy makes "first id wins" deterministic across machines when several
            // Directory.Packages.props exist; .ToList() forces enumeration inside the try.
            propsFiles = SourceFileWalker
                .EnumerateFiles(repoPath, "Directory.Packages.props")
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch { return map; }

        foreach (var props in propsFiles)
        {
            if (new FileInfo(props).Length > MaxProjectFileBytes) continue;
            XDocument doc;
            try { doc = XDocument.Load(props); }
            catch { continue; }
            foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
            {
                var id = (string?)pv.Attribute("Include") ?? (string?)pv.Attribute("Update");
                var ver = (string?)pv.Attribute("Version")
                    ?? pv.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                if (id is not null && ver is not null)
                    map.TryAdd(id, ver);
            }
        }
        return map;
    }
}
