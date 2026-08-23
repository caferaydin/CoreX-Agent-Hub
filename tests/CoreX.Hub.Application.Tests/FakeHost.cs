using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A clock the test moves by hand.
///
/// Lease staleness is a statement about time passing, and proving it with a real sleep
/// would make the suite slow and flaky at once.
/// </summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Process liveness the test decides.
///
/// The owner is a process id together with the moment its host started, because operating
/// systems reuse ids: this makes "same number, different program" expressible without
/// waiting for one to actually happen.
/// </summary>
public sealed class FakeLiveness : IProcessLivenessProbe
{
    public int CurrentProcessId { get; set; } = 4242;

    public DateTimeOffset CurrentHostStartedAtUtc { get; set; } =
        DateTimeOffset.Parse("2026-01-01T08:00:00Z");

    /// <summary>Whether the owner recorded on an execution is still considered running.</summary>
    public bool OwnerIsAlive { get; set; } = true;

    public bool IsAlive(int processId, DateTimeOffset hostStartedAtUtc) =>
        OwnerIsAlive
        && processId == CurrentProcessId
        && hostStartedAtUtc == CurrentHostStartedAtUtc;
}
