using System.Text;
using System.Text.Json;
using MafDoctor.Tools;

namespace MafDoctor;

/// <summary>Freshness of the embedded MAF knowledge versus upstream MAF on nuget.org.</summary>
internal sealed record MafKnowledgeStatus(
    string RegistryMafVersion,
    string? LatestMafVersion,
    IReadOnlyList<string> NewerReleases,
    bool FromCache,
    string? Error,
    bool Disabled);

/// <summary>
/// ROADMAP U-02: the status tool reported whether the *tool* was current, but not
/// whether its *knowledge* was. This compares the registry's MAF version with the
/// stable <c>Microsoft.Agents.AI</c> releases on nuget.org, with the same 24-hour
/// cache, 3-second timeout and <c>MAF_DOCTOR_UPDATE_CHECK</c> opt-out as
/// <see cref="UpdateAdvisor"/>.
/// </summary>
internal static class MafKnowledgeAdvisor
{
    private const string MafIndexUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.agents.ai/index.json";
    private const long CacheMaxBytes = 256 * 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<MafKnowledgeStatus> CheckAsync(
        string registryMafVersion,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        bool ignoreCache = false,
        CancellationToken cancellationToken = default)
    {
        if (UpdateAdvisor.IsDisabled())
            return new(registryMafVersion, null, [], FromCache: false, Error: null, Disabled: true);

        if (!ignoreCache && TryReadFreshCache(out var cached))
            return Build(registryMafVersion, cached, fromCache: true);

        var ownsClient = httpClient is null;
        using var client = ownsClient ? new HttpClient() : null;
        var effectiveClient = httpClient ?? client!;
        if (!effectiveClient.DefaultRequestHeaders.UserAgent.Any(v => v.Product?.Name == "maf-doctor-update-check"))
            effectiveClient.DefaultRequestHeaders.UserAgent.ParseAdd("maf-doctor-update-check/1.0");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            var json = await effectiveClient.GetStringAsync(MafIndexUrl, cts.Token);
            var stable = ParseStableVersions(json);
            if (stable.Count == 0)
                return new(registryMafVersion, null, [], false, "nuget.org returned no stable Microsoft.Agents.AI versions.", false);
            WriteCache(stable);
            return Build(registryMafVersion, stable, fromCache: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            return new(registryMafVersion, null, [], false, ex.Message, false);
        }
    }

    internal static IReadOnlyList<string> ParseStableVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            return [];
        return versions.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v) && !v!.Contains('-', StringComparison.Ordinal))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .Order(Comparer<string>.Create(UpdateAdvisor.CompareSemanticVersions))
            .ToList();
    }

    internal static MafKnowledgeStatus Build(string registryMafVersion, IReadOnlyList<string> stableAscending, bool fromCache)
    {
        var newer = stableAscending
            .Where(v => UpdateAdvisor.CompareSemanticVersions(v, registryMafVersion) > 0)
            .ToList();
        return new(registryMafVersion, stableAscending.LastOrDefault(), newer, fromCache, Error: null, Disabled: false);
    }

    internal static string BuildMarkdown(MafKnowledgeStatus status, CoverageGap? projectGap)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## MAF knowledge");
        sb.AppendLine();
        sb.AppendLine($"The installed registry covers MAF up to `{status.RegistryMafVersion}`.");
        sb.AppendLine();
        if (status.Disabled)
        {
            sb.AppendLine("Upstream check disabled by `MAF_DOCTOR_UPDATE_CHECK`.");
        }
        else if (status.Error is not null || status.LatestMafVersion is null)
        {
            sb.AppendLine($"Could not check nuget.org for MAF releases{(status.Error is null ? "." : $": `{status.Error}`")}");
        }
        else if (status.NewerReleases.Count == 0)
        {
            sb.AppendLine($"✅ Current with the latest stable MAF (`{status.LatestMafVersion}`){(status.FromCache ? " (cached)" : string.Empty)}.");
        }
        else
        {
            var list = string.Join(", ", status.NewerReleases.Select(v => $"`{v}`"));
            sb.AppendLine($"⚠️ Upstream MAF is at `{status.LatestMafVersion}`{(status.FromCache ? " (cached)" : string.Empty)}: "
                + $"this registry is **{status.NewerReleases.Count} release(s) behind** ({list}). "
                + "Verdicts about changes in those releases are unknown.");
            sb.AppendLine();
            sb.AppendLine($"Update with `{CoverageGap.UpdateCommand}`. If you already have the latest maf-doctor, "
                + "the knowledge for those releases ships in its next release.");
        }

        if (projectGap is not null)
        {
            sb.AppendLine();
            sb.AppendLine(projectGap.ToMarkdown());
        }
        return sb.ToString();
    }

    private static string CachePath => Path.Combine(UpdateAdvisor.CacheRoot, "maf-releases-check.json");

    private static bool TryReadFreshCache(out IReadOnlyList<string> stable)
    {
        stable = [];
        try
        {
            if (!File.Exists(CachePath) || new FileInfo(CachePath).Length > CacheMaxBytes)
                return false;
            var record = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(CachePath), CacheJsonOptions);
            if (record is null || record.StableVersions is null || record.StableVersions.Count == 0)
                return false;
            if (DateTimeOffset.UtcNow - record.CheckedAtUtc >= CacheTtl)
                return false;
            stable = record.StableVersions;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCache(IReadOnlyList<string> stable)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new CacheRecord(stable.ToList(), DateTimeOffset.UtcNow), CacheJsonOptions);
            // F-24 — reject a pre-placed symlink at the predictable cache path.
            SafeWorkspaceWriter.WriteAtomic(UpdateAdvisor.CacheRoot, CachePath, payload);
        }
        catch
        {
            // Cache failures never affect the status tool.
        }
    }

    private sealed record CacheRecord(List<string> StableVersions, DateTimeOffset CheckedAtUtc);
}
