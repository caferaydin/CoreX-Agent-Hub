using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Machine truth for a single verification command (architecture I.1 tier A).
/// Phase 1 persists the shape only; ADR-0006 execution arrives in Phase 2/4.
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
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionStepId = Guard.NotEmpty(executionStepId, nameof(executionStepId));
        Order = Guard.Positive(order, nameof(order));
        StepName = Guard.NotBlankMax(stepName, nameof(stepName), 100);
        Executable = Guard.NotBlankMax(executable, nameof(executable), 400);
        _args = [.. (args ?? []).Select(a => a ?? string.Empty)];
        CreatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionStepId { get; private set; }

    public int Order { get; private set; }

    public string StepName { get; private set; } = null!;

    public string Executable { get; private set; } = null!;

    /// <summary>Argument vector. ADR-0006: never a shell command line.</summary>
    public IReadOnlyList<string> Args => _args;

    public int? ExitCode { get; private set; }

    public long? DurationMilliseconds { get; private set; }

    public int? TestsPassed { get; private set; }

    public int? TestsFailed { get; private set; }

    public int? TestsSkipped { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
