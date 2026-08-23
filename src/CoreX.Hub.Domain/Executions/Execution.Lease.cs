namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Who owns this execution right now.
///
/// A lease is how the Hub tells a live execution from a zombie one after a crash. A
/// process id alone proves nothing - operating systems reuse them - so the owner is the
/// pair of the process id and the moment that host started. A different host start means
/// a different owner even at the same pid.
/// </summary>
public sealed partial class Execution
{
    /// <summary>Claims the execution for a live owner and starts its heartbeat.</summary>
    public void ClaimLease(int processId, DateTimeOffset hostStartedAtUtc, DateTimeOffset nowUtc)
    {
        LeaseProcessId = processId;
        LeaseHostStartedAtUtc = hostStartedAtUtc;
        LeaseHeartbeatUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Says the owner is still alive. Cheap on purpose: it writes one column.</summary>
    public void Heartbeat(DateTimeOffset nowUtc)
    {
        LeaseHeartbeatUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>True when this execution is held by exactly this owner.</summary>
    public bool IsOwnedBy(int processId, DateTimeOffset hostStartedAtUtc) =>
        LeaseProcessId == processId
        && LeaseHostStartedAtUtc is { } started
        && started == hostStartedAtUtc;

    /// <summary>
    /// True when nobody appears to be driving this any more: the owner is gone, or it has
    /// not said anything for longer than the allowed silence.
    /// </summary>
    public bool IsLeaseStale(bool ownerIsAlive, DateTimeOffset nowUtc, TimeSpan maximumSilence)
    {
        if (IsTerminal)
        {
            return false;
        }

        if (LeaseProcessId is null || LeaseHostStartedAtUtc is null)
        {
            // Claimed by nobody while still running is itself abandoned.
            return true;
        }

        return !ownerIsAlive || LeaseHeartbeatUtc is null || nowUtc - LeaseHeartbeatUtc.Value > maximumSilence;
    }

    private void ClearLease()
    {
        LeaseProcessId = null;
        LeaseHostStartedAtUtc = null;
        LeaseHeartbeatUtc = null;
    }
}
