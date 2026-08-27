using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// The one place that decides whether a hard isolated organization may start an execution.
///
/// Three questions, asked in this order, each of which can only ever narrow the answer:
///
///   1. is this organization's own environment real, on this machine, now;
///   2. is the machine as a whole arranged so that the environment is worth having;
///   3. can this build actually run a task inside it.
///
/// The order matters for the diagnostic rather than the verdict - an operator whose distro
/// does not exist should be told that, not told about a neighbour - but no step can turn a
/// refusal into a pass, so the sequence is safe to read in any order.
///
/// Everything here is fail closed by construction. There is no branch that treats a missing
/// answer, an unreadable machine or an unconfigured Hub as permission, and there is no path
/// that falls back to running the work outside the environment.
/// </summary>
public sealed class HardIsolationAdmissionService : IIsolationAdmission
{
    private readonly IHubStore _store;
    private readonly IHardIsolationProfileSource _profiles;
    private readonly IHardIsolationReadinessProbe _probe;
    private readonly IHardExecutionPlane _plane;

    public HardIsolationAdmissionService(
        IHubStore store,
        IHardIsolationProfileSource profiles,
        IHardIsolationReadinessProbe probe,
        IHardExecutionPlane plane)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _plane = plane ?? throw new ArgumentNullException(nameof(plane));
    }

    public async Task<IsolationAdmissionDecision> EvaluateAsync(
        Organization organization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(organization);

        // Asked only about organizations that claim a hard boundary. A logical one is governed
        // by the rules it already had, and this service deliberately does not widen them.
        if (organization.IsolationLevel != IsolationLevel.Hard)
        {
            return IsolationAdmissionDecision.Allowed;
        }

        var report = await AssessAsync(organization, ct);

        return report.Decision;
    }

    /// <summary>
    /// The same evaluation, with everything it observed kept.
    ///
    /// <c>hub doctor</c> needs the individual checks and the topology problems in order to say
    /// anything useful, and re-deriving them from a refusal message would be a second
    /// implementation of the rule. So the diagnostic and the gate share one code path, and a
    /// diagnostic that says an organization is ready is the same computation that lets it run.
    /// </summary>
    public async Task<HardIsolationAssessment> AssessAsync(Organization organization, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(organization);

        var slug = organization.Slug.Value;

        if (!_profiles.TryGetProfile(slug, out var profile, out var problem) || profile is null)
        {
            return new HardIsolationAssessment(
                slug,
                HardIsolationReadiness.Unavailable(slug, problem ?? "no hard isolation configuration."),
                IsolationTopologyReadiness.For(slug, []),
                IsolationAdmissionDecision.Refused(
                    IsolationRefusal.NotConfigured,
                    problem ?? "no hard isolation configuration was supplied."));
        }

        var readiness = await _probe.ProbeAsync(slug, profile, ct);

        if (!readiness.IsReady)
        {
            return new HardIsolationAssessment(
                slug,
                readiness,
                IsolationTopologyReadiness.For(slug, []),
                IsolationAdmissionDecision.Refused(
                    IsolationRefusal.NotReady,
                    $"the hard isolated environment is not established: {Describe(readiness)}."));
        }

        var topology = await EvaluateTopologyAsync(slug, ct);

        if (!topology.IsReady)
        {
            return new HardIsolationAssessment(
                slug,
                readiness,
                topology,
                IsolationAdmissionDecision.Refused(
                    IsolationRefusal.TopologyNotReady,
                    "this machine is not arranged safely for a hard isolated organization: "
                    + string.Join("; ", topology.Problems)));
        }

        // Last, and unconditional in this phase. Everything above can pass and this still
        // refuses, which is the intended shape: measuring an environment is not the same
        // capability as executing inside it.
        var decision = _plane.IsAvailable
            ? IsolationAdmissionDecision.Allowed
            : IsolationAdmissionDecision.Refused(
                IsolationRefusal.ExecutionPlaneUnavailable, _plane.UnavailableReason);

        return new HardIsolationAssessment(slug, readiness, topology, decision);
    }

    /// <summary>
    /// Builds the neighbourhood view for one subject.
    ///
    /// Every other organization that can execute is probed, because a neighbour that declares
    /// hard isolation it does not have is exactly as dangerous as one that never declared it -
    /// the code runs under the shared principal either way.
    /// </summary>
    private async Task<IsolationTopologyReadiness> EvaluateTopologyAsync(string subject, CancellationToken ct)
    {
        var organizations = await _store.ListOrganizationsAsync(ct);
        var participants = new List<IsolationParticipant>(organizations.Count);

        foreach (var other in organizations)
        {
            var slug = other.Slug.Value;

            if (string.Equals(slug, subject, StringComparison.Ordinal))
            {
                continue;
            }

            var machineReady = false;

            // Only worth asking the machine about a neighbour that both claims a boundary and
            // is able to run something. Anything else is already decided by the topology rule.
            if (other.IsolationLevel == IsolationLevel.Hard && other.CanStartExecution)
            {
                machineReady =
                    _profiles.TryGetProfile(slug, out var otherProfile, out _)
                    && otherProfile is not null
                    && (await _probe.ProbeAsync(slug, otherProfile, ct)).IsReady;
            }

            participants.Add(IsolationTopologyReadiness.Participant(other, machineReady));
        }

        return IsolationTopologyReadiness.For(subject, participants);
    }

    private static string Describe(HardIsolationReadiness readiness)
    {
        var unmet = readiness.Checks.Where(c => c.Status != ReadinessStatus.Pass).Take(3).ToList();
        var summary = string.Join("; ", unmet.Select(c => $"{c.Id} ({c.Status.ToString().ToLowerInvariant()})"));
        var remaining = readiness.UnmetCheckIds.Count - unmet.Count;

        return remaining > 0 ? $"{summary}; and {remaining} more" : summary;
    }
}

/// <summary>Everything one evaluation observed, plus what it concluded.</summary>
public sealed record HardIsolationAssessment(
    string OrganizationSlug,
    HardIsolationReadiness Readiness,
    IsolationTopologyReadiness Topology,
    IsolationAdmissionDecision Decision);
