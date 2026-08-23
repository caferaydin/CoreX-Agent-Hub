using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Machine truth for a single verification command (architecture I.1 tier A).
///
/// This is what a Machine acceptance criterion is resolved against, and it is resolved
/// after the process that produced it has exited - so everything the decision needs has to
/// be in the row, not in the run. That includes <see cref="Required"/>: the profile may be
/// edited between the run and the decision, and an execution has to keep being judged by
/// the rules that were in force when it ran.
///
/// Written once and never revised. There is deliberately no setter and no mutator: whatever
/// reads this later can report it, and cannot argue with it.
/// </summary>
public sealed class CommandResult
{
    private List<string> _args = [];

    private CommandResult()
    {
        // EF Core
    }

    public CommandResult(
        Guid id,
        Guid organizationId,
        Guid executionStepId,
        int order,
        string stepName,
        string executable,
        IEnumerable<string> args,
        VerificationCommandStatus status,
        bool required,
        int? exitCode,
        long? durationMilliseconds,
        int? testsPassed,
        int? testsFailed,
        int? testsSkipped,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionStepId = Guard.NotEmpty(executionStepId, nameof(executionStepId));
        Order = Guard.Positive(order, nameof(order));
        StepName = Guard.NotBlankMax(stepName, nameof(stepName), 100);
        Executable = Guard.NotBlankMax(executable, nameof(executable), 400);
        _args = [.. (args ?? []).Select(a => a ?? string.Empty)];
        Status = status;
        Required = required;
        ExitCode = exitCode;
        DurationMilliseconds = durationMilliseconds;
        TestsPassed = testsPassed;
        TestsFailed = testsFailed;
        TestsSkipped = testsSkipped;
        CreatedAtUtc = nowUtc;

        // Recording a command as having no status is how a legacy row reads, never how a
        // run reports itself: something that actually ran knows how it ended.
        if (status == VerificationCommandStatus.Unavailable)
        {
            throw new DomainException(
                $"Verification command '{StepName}' must record how it ended; "
                + $"{nameof(VerificationCommandStatus.Unavailable)} exists only for rows written "
                + "before machine truth was durable.");
        }
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionStepId { get; private set; }

    public int Order { get; private set; }

    /// <summary>
    /// The verification profile step this command ran. A Machine acceptance criterion's
    /// VerificationRef is matched against exactly this.
    /// </summary>
    public string StepName { get; private set; } = null!;

    public string Executable { get; private set; } = null!;

    /// <summary>Argument vector. ADR-0006: never a shell command line.</summary>
    public IReadOnlyList<string> Args => _args;

    /// <summary>How the command ended, structurally, as it was observed.</summary>
    public VerificationCommandStatus Status { get; private set; }

    /// <summary>
    /// Whether this step was required <em>when this execution ran</em>. Kept on the row and
    /// never re-read from the profile: a step that becomes required next week was still
    /// optional for the execution that already happened.
    /// </summary>
    public bool Required { get; private set; }

    public int? ExitCode { get; private set; }

    public long? DurationMilliseconds { get; private set; }

    public int? TestsPassed { get; private set; }

    public int? TestsFailed { get; private set; }

    public int? TestsSkipped { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Whether this command is machine evidence that its step passed.
    ///
    /// Only <see cref="VerificationCommandStatus.Passed"/> counts. The exit code is not
    /// consulted and must not be: <see cref="VerificationCommandStatus.ParseFailed"/> also
    /// exits zero, and a criterion answered from the exit code alone would call that a pass.
    /// The status itself stays readable either way, so a reader can say why rather than only
    /// that it did not pass.
    /// </summary>
    public bool IsPass => Status == VerificationCommandStatus.Passed;
}
