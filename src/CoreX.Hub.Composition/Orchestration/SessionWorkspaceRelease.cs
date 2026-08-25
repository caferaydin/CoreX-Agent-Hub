using System.Runtime.Versioning;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Workspace;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Builds the workspace machinery only when a decision actually releases something.
///
/// Most invocations never need git at all, and a CLI that resolved a toolchain, read the
/// configuration and created a control directory on every command would fail for reasons
/// unrelated to what was asked. So this resolves lazily, and if it cannot - no git, no
/// configuration, no repository row - it reports that as a cleanup outcome rather than
/// letting it reach a person who has already decided.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionWorkspaceRelease : IExecutionWorkspaceRelease
{
    private readonly IHubStore _store;
    private readonly string _dataDirectory;
    private readonly string _configPath;

    public SessionWorkspaceRelease(IHubStore store, string dataDirectory, string configPath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dataDirectory = dataDirectory;
        _configPath = configPath;
    }

    public async Task<WorkspaceCleanupResult> ReleaseAsync(
        Guid repositoryId,
        DomainExecution execution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var repository = await _store.FindRepositoryAsync(repositoryId, ct);

        if (repository is null)
        {
            return new WorkspaceCleanupResult(
                WorkspaceCleanupOutcome.Failed, "the execution's repository is no longer in the catalogue");
        }

        var organization = await _store.FindOrganizationAsync(repository.OrganizationId, ct);

        if (organization is null)
        {
            return new WorkspaceCleanupResult(
                WorkspaceCleanupOutcome.Failed, "the execution's organization is no longer in the catalogue");
        }

        if (!GitToolchain.TryResolve(out var gitPath, out var diagnostic))
        {
            return new WorkspaceCleanupResult(WorkspaceCleanupOutcome.Failed, diagnostic);
        }

        if (!File.Exists(_configPath))
        {
            return new WorkspaceCleanupResult(
                WorkspaceCleanupOutcome.Failed,
                $"no configuration at '{_configPath}', so the workspace root is unknown");
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

        var gateway = new WorkspaceReleaseGateway(
            manager,
            _ => new RepositoryLocation(
                repository.LocalPath,
                organization.FilesystemRoot,
                config.Workspace.Root,
                organization.Slug.Value,
                repository.Slug.Value,
                repository.DefaultBranch));

        return await gateway.ReleaseAsync(repositoryId, execution, ct);
    }
}
