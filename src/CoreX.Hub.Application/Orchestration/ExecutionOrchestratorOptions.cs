namespace CoreX.Hub.Application.Orchestration;

/// <summary>Small, explicit orchestration tuning. Not a settings framework.</summary>
public sealed record ExecutionOrchestratorOptions
{
    /// <summary>
    /// MVP frozen architecture: one execution at a time on this machine. Enforced locally,
    /// with no distributed lock, no queue and no worker cluster.
    /// </summary>
    public int MaxConcurrentExecutions { get; init; } = 1;

    /// <summary>How often the lease says the owner is still alive while a long stage runs.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an execution may say nothing before it is considered abandoned.</summary>
    public TimeSpan MaximumLeaseSilence { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the sections that must run after the agent - capturing the changes, storing
    /// the evidence, writing the row that closes the execution - may take once the caller
    /// has already cancelled. Being immune to the caller's token is not being immune to
    /// time.
    /// </summary>
    public TimeSpan MandatoryWorkBudget { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>What one orchestrated execution produced, for the caller to report.</summary>
public sealed record ExecutionRunReport
{
    public required Guid ExecutionId { get; init; }

    public required Domain.Executions.ExecutionState State { get; init; }

    public Domain.Executions.ExecutionOutcome? Outcome { get; init; }

    public string? ResultCommitSha { get; init; }

    public string? ContextPackHash { get; init; }

    public bool UnexpectedAgentCommitDetected { get; init; }

    public IReadOnlyList<string> UnexpectedCommitShas { get; init; } = [];

    public string Detail { get; init; } = string.Empty;

    /// <summary>Set when a disposable baseline snapshot could not be cleaned up.</summary>
    public string? SnapshotResidual { get; init; }

    public string Describe() =>
        Outcome is { } outcome ? $"{State} ({outcome})" : State.ToString();
}

/// <summary>Why an execution could not even be started.</summary>
public sealed class ExecutionStartException : Exception
{
    public ExecutionStartException(string message) : base(message)
    {
    }
}
