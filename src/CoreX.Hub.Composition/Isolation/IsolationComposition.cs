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

            // Phase 10C-1 measures a hard environment and cannot execute inside one. Stating
            // that here, rather than letting an execution find out, is what stops the only
            // remaining alternative - running the work on the host instead.
            new UnavailableHardExecutionPlane());
    }
}
