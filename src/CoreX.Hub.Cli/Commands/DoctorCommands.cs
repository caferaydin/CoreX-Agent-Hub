using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Cli.Commands;

/// <summary>
/// Reports what this machine actually provides, and changes nothing.
///
/// Deliberately not a repair tool. It does not install WSL, import a distribution, create a
/// Linux account, edit <c>wsl.conf</c>, adjust a mount or log in to a provider. Every one of
/// those is a decision about the operator's machine that an operator should make deliberately,
/// and a diagnostic that quietly fixes what it measures is a diagnostic nobody can trust the
/// next time it says something is fine.
///
/// It shares its computation with the gate on the execution path - both go through
/// <see cref="HardIsolationAdmissionService"/> - so an organization this command calls ready is
/// ready by exactly the rule that would admit it, and not by a second implementation that
/// happens to agree today.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DoctorCommands
{
    public static async Task<int> IsolationAsync(
        HubSession session,
        TextWriter output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(output);

        if (!File.Exists(session.ConfigPath))
        {
            throw new UsageException(
                $"no configuration file at {session.ConfigPath}; hub doctor reads hard isolation "
                + "profiles from it and has nothing to check without one.");
        }

        var config = new YamlHubConfigReader().Read(session.ConfigPath);
        var admission = IsolationComposition.Create(session.Store, config, session.DataDirectory);

        var organizations = await session.Store.ListOrganizationsAsync(ct);
        var hard = organizations.Where(o => o.IsolationLevel == IsolationLevel.Hard).ToList();

        Output.WriteField(output, "data directory", session.DataDirectory);
        Output.WriteField(output, "configuration", session.ConfigPath);
        Output.WriteField(output, "organizations", organizations.Count.ToString());
        Output.WriteField(output, "hard isolated", hard.Count.ToString());
        output.WriteLine();

        if (hard.Count == 0)
        {
            output.WriteLine("No organization declares hard isolation; there is nothing to establish.");
            return ExitCodes.Success;
        }

        var ready = true;

        foreach (var organization in hard)
        {
            ready &= await ReportAsync(admission, organization, output, ct);
        }

        return ready ? ExitCodes.Success : ExitCodes.IsolationNotReady;
    }

    /// <summary>Reports one organization, and says whether its machine is in order.</summary>
    private static async Task<bool> ReportAsync(
        HardIsolationAdmissionService admission,
        Organization organization,
        TextWriter output,
        CancellationToken ct)
    {
        var assessment = await admission.AssessAsync(organization, ct);

        output.WriteLine($"organization {organization.Slug} (hard isolation)");

        var rows = assessment.Readiness.Checks.Select(c => new[]
        {
            c.Status switch
            {
                ReadinessStatus.Pass => "pass",
                ReadinessStatus.Fail => "FAIL",
                _ => "UNKNOWN",
            },
            c.Id,
            c.Detail,
        });

        Output.WriteTable(output, ["STATUS", "CHECK", "DETAIL"], rows);

        foreach (var problem in assessment.Topology.Problems)
        {
            output.WriteLine($"topology: {problem}");
        }

        // The readiness verdict and the admission verdict are separate lines on purpose. A
        // machine can be entirely correct and executions still be refused, and an operator who
        // was told only "not ready" would go looking for a fault that is not there.
        var machineReady = assessment.Readiness.IsReady && assessment.Topology.IsReady;

        output.WriteLine(machineReady
            ? "environment: ready"
            : $"environment: NOT READY ({assessment.Readiness.UnmetCheckIds.Count} check(s) unmet, "
              + $"{assessment.Topology.Problems.Count} topology problem(s))");

        output.WriteLine(assessment.Decision.IsAllowed
            ? "executions: allowed"
            : $"executions: refused [{assessment.Decision.ReasonCode}] {assessment.Decision.Detail}");

        output.WriteLine();

        return machineReady;
    }
}
