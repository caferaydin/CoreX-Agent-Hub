namespace CoreX.Hub.Application.Orchestration;

/// <summary>
/// A measured execution policy refused this execution before it could do the thing being policed.
///
/// Deliberately generic. The orchestrator is the lifecycle authority and knows nothing about how any
/// particular execution is contained - no namespace, no filesystem, no platform - so a port that
/// measured a policy and found it unmet says exactly that and nothing about the mechanism. What
/// reaches the record is a run that was refused on policy, which is a verdict the pipeline already
/// knows how to record.
///
/// It is not for infrastructure trouble. A port that could not reach a machine, or that threw while
/// preparing one, has established nothing about policy and keeps the ordinary failure path: calling
/// that a policy violation would put a security finding in the record every time a disk was busy.
/// </summary>
public sealed class ExecutionPolicyRefusedException : Exception
{
    public ExecutionPolicyRefusedException(string message)
        : base(message)
    {
    }

    public ExecutionPolicyRefusedException()
        : base("an execution policy refused this execution.")
    {
    }

    public ExecutionPolicyRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
