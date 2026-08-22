namespace CoreX.Hub.Domain.Tasks;

/// <summary>
/// Coarse task lifecycle (architecture P.1).
/// There is deliberately no <c>Failed</c> task state: failure belongs to an execution,
/// while the task itself remains work that can still be done.
/// There is no <c>Blocked</c> state either: a blocked task is <see cref="Ready"/> and
/// carries a block reason.
/// </summary>
public enum TaskState
{
    Draft = 0,
    Ready = 1,
    Running = 2,
    AwaitingAcceptance = 3,

    /// <summary>Terminal. Only <c>AcceptanceService</c> may produce this (ADR-0014).</summary>
    Accepted = 4,

    /// <summary>Terminal.</summary>
    Rejected = 5,

    /// <summary>Terminal.</summary>
    Cancelled = 6,
}

/// <summary>
/// How an acceptance criterion is verified. ADR-0015: declaring the method is mandatory;
/// declaring <see cref="Human"/> is a completely valid answer.
/// </summary>
public enum VerificationKind
{
    /// <summary>Resolved from machine evidence via a verification step reference.</summary>
    Machine = 0,

    /// <summary>Resolved by an explicit human verdict at acceptance time.</summary>
    Human = 1,
}
