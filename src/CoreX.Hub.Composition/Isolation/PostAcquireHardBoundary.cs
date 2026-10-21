using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// The placement an execution's own lease established, as something the probe can measure from.
///
/// Everything untrusted in this execution is launched through this executor, into this namespace,
/// under this mount sandbox. Measuring from anywhere else would answer a question about somewhere
/// else - which is exactly what the pre-acquire measurement does, and why its answer cannot stand in
/// for this one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LeasedProbePlacement : IHardProbePlacement
{
    /// <summary>Long enough for a refused connection to be refused, short enough not to hang a run.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly HardExecutionLease _lease;
    private readonly HardIsolationProfile _profile;

    public LeasedProbePlacement(HardExecutionLease lease, HardIsolationProfile profile)
    {
        _lease = lease;
        _profile = profile;
    }

    public async Task<WslCallResult> RunAsAgentAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await _lease.Executor.RunAsync(
            _profile,
            new HardProcessRequest
            {
                Executable = arguments[0],
                Arguments = [.. arguments.Skip(1)],

                // The execution root, which the agent can enter. A probe is not repository work and
                // is given nowhere repository work lives.
                WorkingDirectory = _profile.ExecutionRoot,
                HardTimeout = Deadline,
                CorrelationId = _lease.ExecutionId,
            },
            ct);

        // Translated into the vocabulary the probe already reads. A workload that never started is
        // not a workload that reached nothing: the first is unestablished, the second is evidence,
        // and the probe distinguishes them.
        return new WslCallResult(
            result.Outcome switch
            {
                HardProcessOutcome.Exited => WslCallTermination.Exited,
                HardProcessOutcome.Cancelled => WslCallTermination.Cancelled,
                HardProcessOutcome.StartFailed => WslCallTermination.StartFailed,
                HardProcessOutcome.SoftTimeout => WslCallTermination.SoftTimeout,
                HardProcessOutcome.IdleTimeout => WslCallTermination.IdleTimeout,
                _ => WslCallTermination.HardTimeout,
            },
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.StdoutTruncated,
            result.StderrTruncated,
            result.Outcome is HardProcessOutcome.StartFailed ? result.StartFailureMessage : null);
    }
}

/// <summary>
/// Whether the boundary this execution just built is really there, asked before anything
/// repository controlled runs.
///
/// This is the second of two gates and the reason the first one could be narrowed. Admission says
/// the Hub may attempt to build a boundary; this says the boundary exists and behaves. Splitting
/// them is what removed the circular dependency - the fabric is created by the lease, so nothing
/// before the lease can require it - without weakening a single check: every live property is still
/// required, still measured, and still fails closed.
///
/// It measures from the placement the workload will actually get. A control plane the agent can
/// reach is an agent that can edit the record of its own run, and that question has one honest
/// answer only from inside the namespace the agent will be in.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PostAcquireHardBoundary
{
    /// <summary>
    /// Measures every live boundary property, or explains what is not established.
    ///
    /// Returns null when the boundary is ready. Anything else is a refusal an execution must not
    /// proceed past - including Unknown, because a probe that could not answer has established
    /// nothing and "no probe" is the one thing this must never read as "no problem".
    /// </summary>
    public static async Task<string?> MeasureAsync(
        IHardNetworkFabric fabric,
        WslHardNetworkProbe probe,
        HardExecutionLease lease,
        HardIsolationProfile profile,
        string organizationSlug,
        bool peerApplicable,
        CancellationToken ct)
    {
        var checks = new List<ReadinessCheck>();

        // What the trusted plane says it built, verified rather than assumed. The fabric that
        // prepares an attachment is not permitted to be the only witness that it worked.
        checks.AddRange(await fabric.VerifyAsync(profile, organizationSlug, ct));

        checks.AddRange(await probe.ProbePlacementAsync(
            profile, new LeasedProbePlacement(lease, profile), ct));

        var readiness = HardIsolationReadiness.From(organizationSlug, checks);

        if (readiness.IsLiveBoundaryReady(peerApplicable))
        {
            return null;
        }

        var required = peerApplicable
            ? (IReadOnlyList<string>)
                [.. HardIsolationChecks.PostAcquireRequired, .. HardIsolationChecks.ConditionalOnPeer]
            : HardIsolationChecks.PostAcquireRequired;

        var unmet = readiness.UnmetAmong(required);

        var detail = string.Join(
            "; ",
            readiness.Checks
                .Where(c => unmet.Contains(c.Id, StringComparer.Ordinal))
                .Select(c => $"{c.Id} ({c.Status.ToString().ToLowerInvariant()}): {c.Detail}"));

        return "the hard isolation boundary for this execution was established but is not sound: " + detail;
    }
}
