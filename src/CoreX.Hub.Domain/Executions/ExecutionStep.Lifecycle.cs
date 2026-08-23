using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// How one pipeline step records what happened to it.
///
/// A step never decides an execution outcome; it records its own fate so the timeline
/// stays readable. Skipped and Unsupported are first class on purpose: "we did not run
/// this" and "this runtime cannot do that" are answers, not silences.
/// </summary>
public sealed partial class ExecutionStep
{
    public void Start(DateTimeOffset nowUtc)
    {
        if (State != ExecutionStepState.Pending)
        {
            throw new DomainException($"Step {Kind} has already left Pending ({State}).");
        }

        State = ExecutionStepState.Running;
        StartedAtUtc = nowUtc;
    }

    public void Succeed(DateTimeOffset nowUtc) => Finish(ExecutionStepState.Succeeded, null, nowUtc);

    public void Fail(string reason, DateTimeOffset nowUtc) =>
        Finish(ExecutionStepState.Failed, reason, nowUtc);

    public void Timeout(string reason, DateTimeOffset nowUtc) =>
        Finish(ExecutionStepState.TimedOut, reason, nowUtc);

    public void Cancel(string reason, DateTimeOffset nowUtc) =>
        Finish(ExecutionStepState.Cancelled, reason, nowUtc);

    /// <summary>Deliberately not run. The reason is required: a silent skip is not evidence.</summary>
    public void Skip(string reason, DateTimeOffset nowUtc) =>
        Finish(ExecutionStepState.Skipped, Guard.NotBlankMax(reason, nameof(reason), 400), nowUtc);

    /// <summary>
    /// The runtime declared it cannot do this (ADR-0008). Never emulated, never quietly
    /// downgraded to a success.
    /// </summary>
    public void Unsupported(string reason, DateTimeOffset nowUtc) =>
        Finish(ExecutionStepState.Unsupported, Guard.NotBlankMax(reason, nameof(reason), 400), nowUtc);

    private void Finish(ExecutionStepState state, string? reason, DateTimeOffset nowUtc)
    {
        if (State is not (ExecutionStepState.Pending or ExecutionStepState.Running))
        {
            throw new DomainException($"Step {Kind} is already {State}.");
        }

        State = state;
        StateReason = Guard.OptionalMax(reason, nameof(reason), 400);
        StartedAtUtc ??= nowUtc;
        EndedAtUtc = nowUtc;
    }
}
