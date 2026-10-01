namespace MafDoctor.Tools;

/// <summary>
/// A project references a newer MAF release than the registry shipped in this
/// maf-doctor covers, so verdicts about APIs introduced or changed after
/// <see cref="RegistryMafVersion"/> are unknown, not "safe".
/// </summary>
internal sealed record CoverageGap(
    string RegistryMafVersion,
    string ProjectMafVersion,
    IReadOnlyList<DetectedPackage> NewerPackages)
{
    internal const string UpdateCommand = "dotnet tool update -g maf-doctor";

    /// <summary>One-paragraph markdown explanation, shared by every tool that reports it.</summary>
    internal string ToMarkdown() =>
        $"> ⚠️ **Coverage horizon:** this project references MAF **{ProjectMafVersion}**, but this "
        + $"maf-doctor's registry covers MAF up to **{RegistryMafVersion}**. Changes after "
        + $"{RegistryMafVersion} are **not checked**: treat \"no known issues\" for them as "
        + $"UNKNOWN, not safe. Update with `{UpdateCommand}`.";
}

/// <summary>
/// Compares the MAF packages a repository references with the registry's target
/// MAF version (ROADMAP U-01). Pure XML via <see cref="PackageVersionScanner"/>;
/// no build, no network.
/// </summary>
internal static class CoverageHorizon
{
    /// <summary>MAF packages are <c>Microsoft.Agents.AI</c> and <c>Microsoft.Agents.AI.*</c>.</summary>
    internal static bool IsMafPackage(string id) =>
        id.Equals("Microsoft.Agents.AI", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.Agents.AI.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a gap when any MAF package the repo references belongs to a newer
    /// release train than <paramref name="registryTargetVersion"/>; otherwise
    /// <see langword="null"/> (also when no MAF version can be read).
    /// </summary>
    internal static CoverageGap? Evaluate(string repoPath, string registryTargetVersion)
    {
        if (!TryParseTrain(registryTargetVersion, out var target))
            return null;
        return Evaluate(PackageVersionScanner.Detect(repoPath, IsMafPackage), target, registryTargetVersion);
    }

    internal static CoverageGap? Evaluate(
        IReadOnlyList<DetectedPackage> packages, Version target, string registryTargetVersion)
    {
        Version? highest = null;
        var newer = new List<DetectedPackage>();
        foreach (var package in packages)
        {
            Version? packageHighest = null;
            // Detect() joins disagreeing pins with ", ". A version RANGE also contains a
            // comma; its upper-bound half ends with ')' or ']' and is never the version
            // in use, so it is skipped.
            foreach (var raw in package.Version.Split(',', StringSplitOptions.TrimEntries))
            {
                var isUpperBound = (raw.EndsWith(')') || raw.EndsWith(']'))
                    && !(raw.StartsWith('[') || raw.StartsWith('('));
                if (isUpperBound || !TryParseTrain(raw, out var v))
                    continue;
                if (packageHighest is null || v > packageHighest)
                    packageHighest = v;
            }
            if (packageHighest is null)
                continue;
            if (packageHighest > target)
                newer.Add(package);
            if (highest is null || packageHighest > highest)
                highest = packageHighest;
        }

        if (highest is null || highest <= target)
            return null;
        return new CoverageGap(
            registryTargetVersion,
            $"{highest.Major}.{highest.Minor}.{highest.Build}",
            newer);
    }

    /// <summary>
    /// Parses the release train of a NuGet version: <c>1.23.0</c>,
    /// <c>1.23.0-preview.260928.1</c> and the lower bound <c>[1.23.0, )</c> all give
    /// 1.23.0. Floating versions, upper-bound-only ranges and other text give false.
    /// </summary>
    internal static bool TryParseTrain(string? text, out Version train)
    {
        train = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim().TrimStart('[', '(').Trim();
        var end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.'))
            end++;
        var parts = s[..end].Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length == 0 || p.Length > 9))
            return false;
        var major = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var patch = parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        train = new Version(major, minor, patch);
        return true;
    }
}
