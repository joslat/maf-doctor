using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MafDoctor.Tools;

/// <summary>
/// MCP tool: MafDoctorStatus
///
/// Reports whether the installed global tool package is current and whether the
/// current workspace's MCP init/steering files match this installed binary.
/// </summary>
[McpServerToolType]
public sealed class StatusTool
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("""
        Check maf-doctor package freshness, MAF knowledge freshness, and workspace
        init freshness. Use this when the user asks whether the MCP server itself
        needs an update, whether its registry knows the latest MAF releases, or
        whether they only need to rerun `maf-doctor init` for this workspace.

        Input:
          - repoPath: repository/workspace root to inspect. Defaults to the
            configured workspace root (falling back to the server current directory).

        Returns markdown with exact update/init commands when action is needed.
        """)]
    public async Task<string> MafDoctorStatus(
        [Description("Repository/workspace root to inspect. Defaults to the configured workspace root (falling back to the server current directory).")]
        string? repoPath = null)
    {
        // WM-08: the documented default was the server cwd — but in MCP mode the host
        // spawns with cwd = the user's HOME, which the workspace policy rejects, so the
        // no-arg call always errored. Prefer cwd ONLY when it passes policy; otherwise
        // fall back to the first configured workspace root that exists on disk.
        var cwd = Directory.GetCurrentDirectory();
        var rawPath = !string.IsNullOrWhiteSpace(repoPath)
            ? repoPath
            : WorkspacePolicy.Validate(cwd) is null
                ? cwd
                : WorkspacePolicy.ConfiguredRoots.FirstOrDefault(Directory.Exists) ?? cwd;

        if (PathGuard.ValidateRepoPath(rawPath) is { } error)
            return error;

        var targetDir = InitCommand.FindInitializedWorkspaceRoot(rawPath);

        var update = await UpdateAdvisor.CheckForUpdateAsync(timeout: TimeSpan.FromSeconds(3));
        var init = InitCommand.GetWorkspaceInitStatus(targetDir);
        var markdown = UpdateAdvisor.BuildMarkdown(update, init, targetDir);

        // U-02: also report whether the installed *knowledge* is current with MAF,
        // and whether this workspace is past it (coverage horizon, U-01).
        var knowledge = string.Empty;
        string? registryTarget = null;
        try { registryTarget = new Data.RegistryService().TargetVersion; }
        catch { /* registry unavailable: skip the knowledge section */ }
        if (!string.IsNullOrWhiteSpace(registryTarget))
        {
            var status = await MafKnowledgeAdvisor.CheckAsync(registryTarget, timeout: TimeSpan.FromSeconds(3));
            var gap = CoverageHorizon.Evaluate(targetDir, registryTarget);
            knowledge = MafKnowledgeAdvisor.BuildMarkdown(status, gap) + "\n\n";
        }

        return markdown + "\n\n" + knowledge + BuildWorkspacePolicySection();
    }

    /// <summary>
    /// Surfaces the current F-04 workspace policy so a user can see (without
    /// reading env vars themselves) whether MAF Doctor is currently unrestricted
    /// or scoped to specific roots. Reports the operator's own configured value —
    /// not anything derived from a tool call — so this is not a disclosure risk.
    /// </summary>
    internal static string BuildWorkspacePolicySection()
    {
        var roots = WorkspacePolicy.ConfiguredRoots;
        if (roots.Count == 0)
        {
            return "### Workspace policy\n\n"
                + $"No `{WorkspacePolicy.WorkspaceRootsEnvName}` configured — repository-path tools accept "
                + "any absolute path the MCP client requests (filesystem roots and your home directory are "
                + $"always rejected). Set `{WorkspacePolicy.WorkspaceRootsEnvName}` in the MCP server's "
                + "`env` config to scope MAF Doctor to specific repositories.";
        }

        return "### Workspace policy\n\n"
            + $"Restricted to: {string.Join(", ", roots.Select(r => $"`{r}`"))}";
    }
}