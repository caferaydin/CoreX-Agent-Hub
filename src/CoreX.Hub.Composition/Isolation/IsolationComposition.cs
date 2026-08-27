using System.Runtime.Versioning;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Reads hard isolation profiles out of the configuration file.
///
/// The database is deliberately not involved. None of this is catalogue truth - it is which
/// distribution, which Linux account and which native paths this particular machine provides -
/// and persisting it would create a row that can outlive the thing it describes. A stored row
/// claiming an environment exists, read on a machine where it does not, is precisely the
/// failure the readiness checks exist to catch, so there is nothing to store.
/// </summary>
public sealed class ConfiguredHardIsolationProfiles : IHardIsolationProfileSource
{
    private readonly IReadOnlyDictionary<string, OrganizationConfig> _organizations;

    public ConfiguredHardIsolationProfiles(HubConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var organizations = new Dictionary<string, OrganizationConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var organization in config.Organizations)
        {
            if (!string.IsNullOrWhiteSpace(organization.Slug))
            {
                organizations.TryAdd(organization.Slug.Trim(), organization);
            }
        }

        _organizations = organizations;
    }

    public bool TryGetProfile(string organizationSlug, out HardIsolationProfile? profile, out string? problem)
    {
        profile = null;
        problem = null;

        if (!_organizations.TryGetValue(organizationSlug, out var organization))
        {
            problem =
                $"organization '{organizationSlug}' declares hard isolation but is not present in the "
                + "configuration file, so there is nothing describing where its environment is.";
            return false;
        }

        if (!HardIsolationProfile.TryRead(organization.HardIsolation, out profile, out var detail))
        {
            problem = $"organization '{organizationSlug}': {detail}";
            return false;
        }

        return true;
    }
}

/// <summary>
/// Builds the isolation authority the orchestrator and <c>hub doctor</c> both use.
///
/// One construction, so that the gate on the execution path and the diagnostic an operator
/// reads cannot be assembled differently and disagree about the same machine.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IsolationComposition
{
    public static HardIsolationAdmissionService Create(IHubStore store, HubConfig config, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        return new HardIsolationAdmissionService(
            store,
            new ConfiguredHardIsolationProfiles(config),
            WslReadinessProbe.Create(dataDirectory),

            // Still the production truth after Phase 10C-2. A contained process can now be run
            // inside the environment (see CreateProcessExecutor below), but a whole task cannot,
            // and admitting one because part of it could be contained is how work ends up half
            // outside the boundary.
            new UnavailableHardExecutionPlane());
    }

    /// <summary>
    /// Builds the hard process primitive.
    ///
    /// Deliberately not reachable from <see cref="Create"/>. The admission authority above
    /// answers a question about the whole task workflow, and nothing about this executor
    /// existing changes that answer - wiring it into admission is Phase 10C-3's work, once the
    /// workspace, evidence, authentication and control plane surfaces move inside the boundary
    /// too. Until then it is available to be exercised and proven, and to nothing else.
    /// </summary>
    public static IHardProcessExecutor CreateProcessExecutor(string dataDirectory) =>
        WslHardProcessExecutor.Create(dataDirectory);

    /// <summary>
    /// Builds the hard workspace.
    ///
    /// Deliberately not reachable from <see cref="Create"/> either, and for the same reason the
    /// process executor is not. A workspace inside the boundary is one of the things a hard task
    /// needs; its context, its evidence, its provider authentication and its network are still on
    /// the host, and admitting a task because part of it could be isolated is how work ends up
    /// half outside the boundary.
    /// </summary>
    public static HardWorkspace.HardWorkspaceGateway CreateWorkspace(
        CoreX.Hub.Workspace.WorkspaceManager windows,
        Func<Guid, Orchestration.RepositoryLocation> locate,
        CoreX.Hub.Workspace.Abstractions.IGitCommandExecutor hostGit,
        HardIsolationProfile profile,
        string dataDirectory) =>
        HardWorkspace.HardWorkspaceGateway.Create(
            windows, locate, hostGit, profile, dataDirectory, dataDirectory);
}
