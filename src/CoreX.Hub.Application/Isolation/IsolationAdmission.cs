using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Application.Isolation;

/// <summary>Stable reason codes for a refusal. Safe to log, audit and show.</summary>
public static class IsolationRefusal
{
    public const string NotReady = "hard_isolation_not_ready";
    public const string TopologyNotReady = "isolation_topology_not_ready";
    public const string ExecutionPlaneUnavailable = "hard_execution_plane_unavailable";

    /// <summary>The Hub was never told how to reach a hard environment at all.</summary>
    public const string NotConfigured = "hard_isolation_not_configured";
}

/// <summary>Whether an organization may start an execution, and why not.</summary>
public sealed record IsolationAdmissionDecision
{
    public static IsolationAdmissionDecision Allowed { get; } = new() { IsAllowed = true };

    public required bool IsAllowed { get; init; }

    public string? ReasonCode { get; init; }

    public string? Detail { get; init; }

    public static IsolationAdmissionDecision Refused(string reasonCode, string detail) =>
        new() { IsAllowed = false, ReasonCode = reasonCode, Detail = detail };
}

/// <summary>
/// The authority that decides whether a hard isolated organization may execute.
///
/// One port, consulted from one place, so the CLI and the API cannot disagree - and so the
/// answer cannot be reimplemented in an endpoint or a screen. What sits behind it does the
/// machine probing; nothing above it knows that WSL exists.
///
/// A Hub with no implementation wired is not a Hub where hard organizations run freely: the
/// orchestrator refuses them, because the absence of an authority is not permission.
/// </summary>
public interface IIsolationAdmission
{
    /// <summary>
    /// Evaluates one organization, freshly enough to be trusted for starting a run.
    ///
    /// Called on the execution path, so an implementation may cache briefly but must never
    /// serve a pass whose evidence has gone stale - a machine that was ready ten minutes ago
    /// is not evidence about a machine now.
    /// </summary>
    Task<IsolationAdmissionDecision> EvaluateAsync(Organization organization, CancellationToken ct);
}
