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

            // The real plane. Every stage of a hard workflow now has an implementation that runs
            // inside the boundary rather than beside it - workspace, agent, verification, review
            // and evidence - so reporting no plane would be reporting something untrue.
            //
            // It says only that this machine could run a hard execution. Whether this organization's
            // environment is really established, and whether the runtime this execution selected can
            // be placed inside it, are separate questions asked elsewhere; folding them in here would
            // let an available machine imply an admissible execution.
            CreatePlane(dataDirectory));
    }

    /// <summary>
    /// Whether this machine can run a whole task inside a boundary.
    ///
    /// Shared by admission and by routing so the gate and the composition cannot disagree about the
    /// same machine. A platform that is not Windows has no hard plane at all, which is a refusal for
    /// a hard organization rather than a reason to run it anywhere weaker.
    /// </summary>
    public static IHardExecutionPlane CreatePlane(string dataDirectory) =>
        OperatingSystem.IsWindows()
            ? HardExecutionPlane.Create(dataDirectory)
            : new UnavailableHardExecutionPlane();

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
    /// <summary>
    /// Builds one organization's hard evidence store.
    ///
    /// Which organization's store this is comes from the profile and from nothing else - not a
    /// provider, not a task, not a request. There is deliberately no argument here through which
    /// a caller could name a different root.
    /// </summary>
    internal static HardEvidence.HardEvidenceGateway CreateEvidence(
        HardIsolationProfile profile,
        string dataDirectory,
        IHardExecutionBinding binding) =>
        HardEvidence.HardEvidenceGateway.Create(profile, dataDirectory, binding);

    /// <summary>Resolves where a hard isolated agent's own state may live.</summary>
    public static HardEvidence.HardAgentHome CreateAgentHome(
        HardIsolationProfile profile,
        string dataDirectory) =>
        HardEvidence.HardAgentHome.Create(profile, dataDirectory);

    internal static HardWorkspace.HardWorkspaceGateway CreateWorkspace(
        CoreX.Hub.Workspace.WorkspaceManager windows,
        Func<Guid, Orchestration.RepositoryLocation> locate,
        CoreX.Hub.Workspace.Abstractions.IGitCommandExecutor hostGit,
        HardIsolationProfile profile,
        string dataDirectory,
        IHardExecutionBinding binding) =>
        HardWorkspace.HardWorkspaceGateway.Create(
            windows, locate, hostGit, profile, dataDirectory, dataDirectory, binding);
}
