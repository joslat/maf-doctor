using System.Text;
using System.Text.Json;
using MafDoctor.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MafDoctor;

internal sealed class UpdateNoticeHostedService(ILogger<UpdateNoticeHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => CheckAndLogAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task CheckAndLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var update = await UpdateAdvisor.CheckForUpdateAsync(
                timeout: TimeSpan.FromSeconds(3),
                ignoreCache: false,
                cancellationToken: cancellationToken);
            // WM-07: resolve the REAL workspace, not the server's cwd — MCP hosts spawn
            // stdio servers with cwd = the user's home directory, so deriving staleness
            // from cwd produced a false "init is stale" nag on every startup. Prefer a
            // configured workspace root that exists; else walk up from cwd. If the
            // resolved directory is not actually an initialized workspace (no .mcp.json /
            // .vscode/mcp.json), skip the init-staleness half of the notice entirely.
            var cwd = Directory.GetCurrentDirectory();
            var root = WorkspacePolicy.ConfiguredRoots.FirstOrDefault(Directory.Exists)
                       ?? InitCommand.FindInitializedWorkspaceRoot(cwd);
            var initialized = File.Exists(Path.Combine(root, ".mcp.json"))
                              || File.Exists(Path.Combine(root, ".vscode", "mcp.json"));
            var init = initialized ? InitCommand.GetWorkspaceInitStatus(root) : (InitCommand.InitStatus?)null;
            var notice = UpdateAdvisor.BuildStartupNotice(update, init);
            if (!string.IsNullOrWhiteSpace(notice))
                logger.LogWarning("{Notice}", notice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "maf-doctor update check failed during MCP startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class UpdateAdvisor
{
    private const string PackageId = "maf-doctor";
    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/maf-doctor/index.json";
    private const long CacheMaxBytes = 64 * 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static UpdateStatus? LastStatus { get; private set; }

    public static async Task<UpdateStatus> CheckForUpdateAsync(
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        bool ignoreCache = false,
        CancellationToken cancellationToken = default)
    {
        var currentVersion = InitCommand.CurrentVersion;
        if (IsDisabled())
            return Remember(new UpdateStatus(currentVersion, LatestVersion: null, State: UpdateState.Disabled, FromCache: false, Error: null));

        if (!ignoreCache && TryReadFreshCache(out var cachedLatest, out var checkedAtUtc))
            return Remember(BuildStatus(currentVersion, cachedLatest, fromCache: true, checkedAtUtc));

        var ownsClient = httpClient is null;
        using var client = ownsClient ? new HttpClient() : null;
        var effectiveClient = httpClient ?? client!;
        if (!effectiveClient.DefaultRequestHeaders.UserAgent.Any(v => v.Product?.Name == "maf-doctor-update-check"))
            effectiveClient.DefaultRequestHeaders.UserAgent.ParseAdd("maf-doctor-update-check/1.0");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));

        try
        {
            var json = await effectiveClient.GetStringAsync(NuGetIndexUrl, cts.Token);
            var latest = ParseLatestStableVersion(json);
            if (latest is null)
                return Remember(new UpdateStatus(currentVersion, LatestVersion: null, State: UpdateState.CheckFailed, FromCache: false, Error: "NuGet returned no stable maf-doctor versions."));

            WriteCache(latest);
            return Remember(BuildStatus(currentVersion, latest, fromCache: false, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            return Remember(new UpdateStatus(currentVersion, LatestVersion: null, State: UpdateState.CheckFailed, FromCache: false, Error: ex.Message));
        }
    }

    public static string BuildMarkdown(UpdateStatus update, InitCommand.InitStatus init, string repoPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# maf-doctor status");
        sb.AppendLine();
        sb.AppendLine($"Current tool version: `{update.CurrentVersion}`");
        if (!string.IsNullOrWhiteSpace(update.LatestVersion))
            sb.AppendLine($"Latest NuGet version: `{update.LatestVersion}`{(update.FromCache ? " (cached)" : string.Empty)}");
        sb.AppendLine($"Workspace: `{repoPath}`");
        sb.AppendLine();

        switch (update.State)
        {
            case UpdateState.UpdateAvailable:
                sb.AppendLine("## Update available");
                sb.AppendLine();
                sb.AppendLine("Run:");
                sb.AppendLine();
                sb.AppendLine("```powershell");
                sb.AppendLine("dotnet tool update --global maf-doctor");
                sb.AppendLine("maf-doctor init");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("Then reload the editor or MCP host so it starts the new binary.");
                break;
            case UpdateState.UpToDate:
                sb.AppendLine("## Tool package");
                sb.AppendLine();
                sb.AppendLine("The installed maf-doctor package is up to date.");
                break;
            case UpdateState.Disabled:
                sb.AppendLine("## Tool package");
                sb.AppendLine();
                sb.AppendLine("Update checks are disabled by `MAF_DOCTOR_UPDATE_CHECK`.");
                break;
            case UpdateState.CheckFailed:
                sb.AppendLine("## Tool package");
                sb.AppendLine();
                sb.AppendLine("Could not check NuGet for updates. The MCP server is still usable.");
                if (!string.IsNullOrWhiteSpace(update.Error))
                    sb.AppendLine($"Error: `{update.Error}`");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("## Workspace init");
        sb.AppendLine();
        if (init.IsCurrent)
        {
            sb.AppendLine("This workspace's MCP config and steering sidecars are current for the installed maf-doctor.");
        }
        else
        {
            sb.AppendLine("This workspace should be refreshed with:");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("maf-doctor init");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Findings:");
            foreach (var finding in init.Findings)
                sb.AppendLine($"- {finding}");
        }

        return sb.ToString();
    }

    // WM-07: `init` is nullable — a null value means "the workspace couldn't be
    // resolved / isn't initialized", so the init-staleness half is skipped entirely
    // (the update-available half still fires).
    public static string? BuildStartupNotice(UpdateStatus update, InitCommand.InitStatus? init)
    {
        var lines = new List<string>();
        if (update.State == UpdateState.UpdateAvailable && update.LatestVersion is not null)
        {
            lines.Add($"maf-doctor {update.LatestVersion} is available; this MCP server is running {update.CurrentVersion}.");
            lines.Add("Update when ready: dotnet tool update --global maf-doctor");
            lines.Add("Then run: maf-doctor init");
            lines.Add("Reload the editor or MCP host so it starts the new binary.");
        }

        if (update.State != UpdateState.UpdateAvailable && init is { NeedsRefresh: true })
        {
            lines.Add("maf-doctor is running, but this workspace's MCP/steering init is stale or incomplete.");
            lines.Add("Refresh it with: maf-doctor init");
            lines.Add("Then reload the editor or MCP host.");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    internal static string? ParseLatestStableVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            return null;

        string? latest = null;
        foreach (var versionElement in versions.EnumerateArray())
        {
            var version = versionElement.GetString();
            if (string.IsNullOrWhiteSpace(version) || version.Contains('-', StringComparison.Ordinal))
                continue;
            if (latest is null || CompareSemanticVersions(version, latest) > 0)
                latest = version;
        }

        return latest;
    }

    internal static int CompareSemanticVersions(string left, string right)
    {
        var leftVersion = ParseVersion(left);
        var rightVersion = ParseVersion(right);
        for (var i = 0; i < Math.Max(leftVersion.Parts.Length, rightVersion.Parts.Length); i++)
        {
            var leftPart = i < leftVersion.Parts.Length ? leftVersion.Parts[i] : 0;
            var rightPart = i < rightVersion.Parts.Length ? rightVersion.Parts[i] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
                return comparison;
        }

        if (leftVersion.IsPrerelease != rightVersion.IsPrerelease)
            return leftVersion.IsPrerelease ? -1 : 1;

        return 0;
    }

    private static ParsedVersion ParseVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith('v')) normalized = normalized[1..];
        var plus = normalized.IndexOf('+');
        if (plus >= 0) normalized = normalized[..plus];
        var dash = normalized.IndexOf('-');
        var isPrerelease = dash >= 0;
        if (isPrerelease) normalized = normalized[..dash];
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();
        return new ParsedVersion(parts, isPrerelease);
    }

    private sealed record ParsedVersion(int[] Parts, bool IsPrerelease);

    private static UpdateStatus BuildStatus(string currentVersion, string latestVersion, bool fromCache, DateTimeOffset checkedAtUtc)
        => new(
            currentVersion,
            latestVersion,
            CompareSemanticVersions(latestVersion, currentVersion) > 0 ? UpdateState.UpdateAvailable : UpdateState.UpToDate,
            fromCache,
            Error: null,
            CheckedAtUtc: checkedAtUtc);

    private static UpdateStatus Remember(UpdateStatus status)
    {
        LastStatus = status;
        return status;
    }

    internal static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable("MAF_DOCTOR_UPDATE_CHECK");
        return value is not null && value.Trim().ToLowerInvariant() is "0" or "false" or "off" or "no";
    }

    private static bool TryReadFreshCache(out string latestVersion, out DateTimeOffset checkedAtUtc)
    {
        latestVersion = string.Empty;
        checkedAtUtc = default;
        var path = CachePath;
        if (!File.Exists(path))
            return false;
        if (new FileInfo(path).Length > CacheMaxBytes)
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            latestVersion = document.RootElement.GetProperty("latestVersion").GetString() ?? string.Empty;
            checkedAtUtc = document.RootElement.GetProperty("checkedAtUtc").GetDateTimeOffset();
            return !string.IsNullOrWhiteSpace(latestVersion) && DateTimeOffset.UtcNow - checkedAtUtc < CacheTtl;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCache(string latestVersion)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new CacheRecord(latestVersion, DateTimeOffset.UtcNow), CacheJsonOptions);
            // F-24 — reject a pre-placed symlink at the predictable cache path rather
            // than following it.
            SafeWorkspaceWriter.WriteAtomic(CacheRoot, CachePath, payload);
        }
        catch
        {
            // Cache failures should never affect MCP startup or tool calls.
        }
    }

    internal static string CacheRoot
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            return Path.Combine(root, PackageId);
        }
    }

    private static string CachePath => Path.Combine(CacheRoot, "update-check.json");

    private sealed record CacheRecord(string LatestVersion, DateTimeOffset CheckedAtUtc);
}

internal enum UpdateState
{
    UpToDate,
    UpdateAvailable,
    Disabled,
    CheckFailed,
}

internal sealed record UpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    UpdateState State,
    bool FromCache,
    string? Error,
    DateTimeOffset? CheckedAtUtc = null)
{
    public bool UpdateAvailable => State == UpdateState.UpdateAvailable;
}