namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Execution pipeline state (architecture P.2). Distinct from task state on purpose:
/// failure lives here, never on the task.
/// </summary>
public enum ExecutionState
{
    Created = 0,
    PreparingWorkspace = 1,
    BuildingContext = 2,
    RunningAgent = 3,
    CapturingChanges = 4,
    Verifying = 5,
    Reviewing = 6,

    /// <summary>Pipeline finished; <see cref="Execution.Outcome"/> carries the verdict.</summary>
    Completed = 7,

    Failed = 8,
    TimedOut = 9,
    Cancelled = 10,

    /// <summary>Host process died while the execution was still running.</summary>
    Interrupted = 11,
}

/// <summary>
/// Verdict of a <see cref="ExecutionState.Completed"/> execution (architecture P.2).
/// Only <see cref="ReviewBlocked"/> is eligible for a human acceptance override
/// (ADR-0014); every other value is default-deny.
/// </summary>
public enum ExecutionOutcome
{
    Passed = 0,
    VerificationFailed = 1,
    ReviewBlocked = 2,
    ReviewUnusable = 3,
    ScopeViolation = 4,
    SecretGuardBlocked = 5,

    /// <summary>Gate K1: agent created its own commit (errata E1, ADR-0005).</summary>
    RuntimePolicyViolation = 6,
}

/// <summary>Lifecycle of the worktree an execution owns (architecture G.6).</summary>
public enum WorkspaceState
{
    Active = 0,
    Released = 1,
    Orphaned = 2,
}

public enum ExecutionStepKind
{
    PrepareWorkspace = 0,
    BuildContext = 1,
    Agent = 2,
    CaptureChanges = 3,
    Verify = 4,
    Review = 5,
}

public enum ExecutionStepState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    TimedOut = 4,
    Cancelled = 5,

    /// <summary>Not run because an earlier gate blocked the pipeline.</summary>
    Skipped = 6,

    /// <summary>Runtime capabilities do not support this step (ADR-0008). Never silent.</summary>
    Unsupported = 7,
}

/// <summary>
/// How one verification command ended, as the Hub records it for ever.
///
/// The verification module has its own status vocabulary; this is the domain's copy of it,
/// because the domain depends on nothing and a persisted row has to keep meaning something
/// long after the run that produced it. The composition layer converts between the two
/// exhaustively, so a new member on either side is a compile-time and fail-closed problem
/// rather than a silent one.
///
/// The distinctions matter more than they look. <see cref="Passed"/> and
/// <see cref="ParseFailed"/> both exit zero, and collapsing them would report a criterion as
/// met when the Hub could not read the report at all.
/// </summary>
public enum VerificationCommandStatus
{
    /// <summary>
    /// No structured status was recorded. Only rows written before durable machine truth
    /// existed can hold this, and it is never a pass: machine truth nobody captured must
    /// not become evidence that something worked.
    /// </summary>
    Unavailable = 0,

    Passed = 1,

    Failed = 2,

    TimedOut = 3,

    Cancelled = 4,

    /// <summary>Nothing was launched: a rejected executable, a rejected path, a start failure.</summary>
    StartFailed = 5,

    /// <summary>The command exited zero but its report could not be turned into counters.</summary>
    ParseFailed = 6,

    /// <summary>Deterministically not run, because a required step had already failed.</summary>
    Skipped = 7,
}

public enum FindingSeverity
{
    Critical = 0,
    Major = 1,
    Minor = 2,
    Info = 3,
}

public enum FindingCategory
{
    Correctness = 0,
    Security = 1,
    Architecture = 2,
    Performance = 3,
    TestCoverage = 4,
    Convention = 5,
}

public enum FindingConfidence
{
    High = 0,
    Medium = 1,
    Low = 2,
}

/// <summary>Kinds of evidence blob referenced from the content addressed store (ADR-0013).</summary>
public enum EvidenceKind
{
    ContextPack = 0,
    AgentStdout = 1,
    AgentStderr = 2,
    AgentStructuredResult = 3,
    GitStatus = 4,
    GitCommitGraph = 5,
    GitWorktreeDiff = 6,
    GitCommitDiff = 7,
    FileInventory = 8,
    CommandStdout = 9,
    CommandStderr = 10,
    TestReport = 11,
    ReviewFindings = 12,
    DebugLog = 13,
}

public enum AcceptanceDecisionKind
{
    Accept = 0,
    RequestChanges = 1,
    Reject = 2,
}

/// <summary>
/// Who took a decision (ADR-0028).
///
/// A person, or a policy the operator switched on for one repository. Stored on the row
/// rather than inferred from the actor string, because "was this a human" is a question an
/// audit has to be able to answer without trusting a naming convention.
/// </summary>
public enum AcceptanceDecisionSource
{
    Person = 0,

    /// <summary>
    /// The repository's autopilot accepted work its evidence fully covered. Only ever an
    /// Accept, never an override, and never with a human verdict - a policy has none to give.
    /// </summary>
    Policy = 1,
}

public enum HumanCriterionVerdict
{
    Met = 0,
    NotMet = 1,
    NotApplicable = 2,
}
