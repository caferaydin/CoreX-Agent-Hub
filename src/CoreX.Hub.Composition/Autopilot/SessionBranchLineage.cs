using System.Runtime.Versioning;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Composition.Autopilot;

/// <summary>
/// The read-only lineage question, answered through the workspace module like every other
/// git question the Hub asks - same executor, same trust roots, same hardened environment.
///
/// Built per question, as <see cref="Orchestration.SessionWorkspaceRelease"/> is, so a Hub
/// with no autopilot never resolves a toolchain for one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionBranchLineage : IBranchLineage
{
    private readonly string _dataDirectory;
    private readonly string _configPath;

    public SessionBranchLineage(string dataDirectory, string configPath)
    {
        _dataDirectory = dataDirectory;
        _configPath = configPath;
    }

    public async Task<bool?> ContainsAsync(
        Organization organization,
        Repository repository,
        string commitSha,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(repository);

        // A hard isolated execution commits inside its own environment, so the host
        // repository never holds that commit and cannot be asked about it.
        if (organization.IsolationLevel != IsolationLevel.Logical)
        {
            return null;
        }

        if (!GitToolchain.TryResolve(out var gitPath, out _) || !File.Exists(_configPath))
        {
            return null;
        }

        var config = new YamlHubConfigReader().Read(_configPath);
        var hubControlRoot = Path.Combine(_dataDirectory, "git-control");
        Directory.CreateDirectory(hubControlRoot);

        var manager = new WorkspaceManager(
            new ProcessGitCommandExecutor(
                gitPath!,
                [organization.FilesystemRoot, config.Workspace.Root, hubControlRoot]),
            new FileSystemGuardAdapter(),
            new WorkspaceOptions { HubControlRoot = hubControlRoot });

        return await manager.BranchContainsAsync(
            repository.LocalPath, organization.FilesystemRoot, repository.DefaultBranch, commitSha, ct);
    }
}
