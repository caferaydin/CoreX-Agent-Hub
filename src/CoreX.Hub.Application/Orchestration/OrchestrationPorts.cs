using CoreX.Hub.Application.Gates;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Orchestration;

/// <summary>Where the Hub prepared a working tree for an execution.</summary>
public sealed record PreparedExecutionWorkspace(
    string WorkspacePath,
    string BranchName,
    string BaseCommitSha);

/// <summary>
/// A commit the workspace resolved, held between deciding it and materialising it.
///
/// It exists because those two things cannot happen at once. The commit has to be decided
/// and written down as the execution's provenance before a worktree is built from it, and
/// the worktree then has to be built from that same commit rather than from wherever the
/// branch has got to by then. Carrying the answer across that gap is the whole job.
///
/// Only the workspace port produces one, and it is handed straight back to that port
/// unmodified. The orchestrator can read the commit but has no way to make one up, which
/// is what makes the second half provably about the commit the first half pinned.
/// </summary>
public interface IExecutionWorkspacePreflight
{
    /// <summary>The commit resolved from the repository, or null when it could not be.</summary>
    string? BaseCommitSha { get; }

    /// <summary>What the inspection found. The reason, whenever there is no commit.</summary>
    string Detail { get; }
}

/// <summary>Everything the workspace observed after the agent finished.</summary>
public sealed record ExecutionCapture
{
    public required string BaseCommitSha { get; init; }

    public required string ObservedHeadSha { get; init; }

    public IReadOnlyList<string> UnexpectedCommitShas { get; init; } = [];

    public string GitStatus { get; init; } = string.Empty;

    public string GitCommitGraph { get; init; } = string.Empty;

    public string GitWorktreeDiff { get; init; } = string.Empty;

    public string FileInventory { get; init; } = string.Empty;

    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    /// <summary>Added diff lines, in memory only, for the secret gate to inspect.</summary>
    public IReadOnlyList<AddedLine> AddedLines { get; init; } = [];
}

/// <summary>
/// What the Hub's own commit did, and what the worktree still holds afterwards.
///
/// <see cref="UncommittedPaths"/> is expected to be empty: everything was staged. When it
/// is not, the tree and the commit are different things, and nothing downstream may treat
/// measuring the tree as measuring the commit.
/// </summary>
public sealed record ResultCommitOutcomeView(
    bool Created,
    string? CommitSha,
    string? CommitDiff,
    string Detail,
    IReadOnlyList<string> UncommittedPaths,
    bool WorktreeStateKnown);

/// <summary>
/// The Application owned port for the workspace.
///
/// The orchestrator sequences; it never builds a git command. Everything here is answered
/// by the Phase 3 workspace module through the composition layer, which is also the only
/// place a Hub result commit can be created.
/// </summary>
public interface IExecutionWorkspace
{
    /// <summary>
    /// Resolves the commit an execution will be based on. Nothing is created and nothing is
    /// written: this only looks.
    ///
    /// It runs before the execution row exists, because a row that names a commit nobody
    /// resolved is a claim the rest of the pipeline would then measure against. A
    /// repository that cannot answer therefore produces no execution at all.
    /// </summary>
    Task<IExecutionWorkspacePreflight> PreflightAsync(Guid repositoryId, CancellationToken ct);

    /// <summary>
    /// Materialises the agent worktree at the commit <paramref name="preflight"/> pinned.
    /// The default branch is not resolved again: if it moved in between, it is no longer
    /// the authority - the captured commit is.
    /// </summary>
    /// <param name="executionId">
    /// The execution this workspace belongs to, already durable when this is called.
    ///
    /// Carried explicitly rather than inferred from call order. An isolated organization binds its
    /// environment - and the durable claim on its organization's network attachment - to this
    /// identity, and the alternative was for the first port invoked to bind anonymously and adopt
    /// an identity later. That leaves a window where a claim exists under no execution, which is
    /// exactly the state crash reconciliation cannot classify.
    ///
    /// It is execution vocabulary and nothing more. No isolation concept crosses this contract.
    /// </param>
    Task<PreparedExecutionWorkspace?> PrepareAsync(
        Guid executionId,
        Guid repositoryId,
        IExecutionWorkspacePreflight preflight,
        string taskKey,
        int attemptNo,
        CancellationToken ct);

    Task<ExecutionCapture> CaptureAsync(PreparedExecutionWorkspace workspace, CancellationToken ct);

    Task<ResultCommitOutcomeView> CreateResultCommitAsync(
        PreparedExecutionWorkspace workspace,
        string taskKey,
        int attemptNo,
        CancellationToken ct);
}

/// <summary>
/// What the context builder produced for one execution.
///
/// The two prompt paths are named separately because they are different kinds of thing.
/// The system prompt is the Hub's own execution contract and is not negotiable; the rules
/// document is what the organisation, project and repository add on top of it. A runtime
/// that can layer them keeps that distinction; one that cannot uses the contract alone.
/// </summary>
public sealed record BuiltContextPack(
    string PackPath,
    string ContextPackHash,
    string SystemPromptPath,
    string RulesPromptPath);

/// <summary>The Application owned port for context materialisation.</summary>
public interface IExecutionContextBuilder
{
    Task<BuiltContextPack?> BuildAsync(
        Guid executionId,
        Guid repositoryId,
        Domain.Tasks.HubTask task,
        int attemptNo,
        string baseCommitSha,
        CancellationToken ct);
}

/// <summary>What a runtime reported, flattened for the orchestrator.</summary>
public sealed record AgentExecutionResult(
    string Outcome,
    int? ExitCode,
    string Stdout,
    string Stderr,
    string? StructuredOutput,
    TimeSpan Duration,
    string? Diagnostic);

/// <summary>
/// The Application owned port for running an agent.
///
/// Which runtime this is has already been decided from AgentProfile.RuntimeKey by the
/// composition layer. The orchestrator never learns the answer: there is nothing here to
/// tell it, which is what keeps a hosted assistant and a local harness identical to it.
/// </summary>
public interface IAgentRuntimeGateway
{
    Task<AgentExecutionResult> RunAsync(
        AgentProfile profile,
        PreparedExecutionWorkspace workspace,
        BuiltContextPack context,
        Domain.Tasks.HubTask task,
        string correlationId,
        CancellationToken ct);
}

/// <summary>
/// One verification step's machine result, flattened.
///
/// Everything a later decision could need is carried here rather than left in the run,
/// because the decision happens in another process: the step's own name, whether it was
/// required at the time, how it ended, and what it ran. The status is the domain's own
/// vocabulary - the composition layer has already converted it exhaustively - so nothing
/// downstream has to interpret a string.
/// </summary>
public sealed record VerificationStepView
{
    public required string StepName { get; init; }

    /// <summary>Whether the step was required for this run, not for the profile as it stands now.</summary>
    public required bool Required { get; init; }

    public required VerificationCommandStatus Status { get; init; }

    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public int? ExitCode { get; init; }

    public long? DurationMilliseconds { get; init; }

    public int? TestsPassed { get; init; }

    public int? TestsFailed { get; init; }

    public int? TestsSkipped { get; init; }
}

/// <summary>What a verification run reported.</summary>
public sealed record VerificationRunView(
    bool RequiredPassed,
    string ProfileHash,
    IReadOnlyList<VerificationStepView> Steps,
    int? TotalTests,
    int? PassedTests,
    int? FailedTests);

/// <summary>Baseline comparison context, or null when no baseline could be established.</summary>
public sealed record BaselineView(string BaseCommitSha, int? PassedTests, int? FailedTests, string Description);

public sealed record VerificationOutcomeView(
    VerificationRunView? Result,
    BaselineView? Baseline,
    string? SnapshotResidual,
    string Detail);

/// <summary>
/// The Application owned port for verification.
///
/// Both measurements are of a commit, and both happen in a disposable tree pinned to it:
/// the baseline at the execution's base commit, the result at the Hub's own commit. The
/// port takes two commit ids and no workspace path at all, which is what makes it
/// impossible to measure the agent's worktree by accident - and what keeps that worktree
/// clean enough to be released once a person has decided.
/// </summary>
public interface IExecutionVerifier
{
    /// <param name="executionId">
    /// The execution being verified.
    ///
    /// Needed in its own right rather than inherited from whoever prepared a workspace, because
    /// recovery re-measures a commit for an execution this process never ran: there was no
    /// workspace preparation in that invocation to have bound anything. Verification runs
    /// repository-controlled commands, so it must be able to resolve its own execution's boundary
    /// without depending on a side effect of an earlier step.
    /// </param>
    Task<VerificationOutcomeView> VerifyAsync(
        Guid executionId,
        Guid repositoryId,
        string baseCommitSha,
        string resultCommitSha,
        CancellationToken ct);
}

/// <summary>A stored evidence blob, ready to be recorded as metadata.</summary>
public sealed record StoredEvidence(string Sha256, long SizeBytes, string MediaType, int RedactionCount);

/// <summary>
/// The Application owned port for evidence.
///
/// Redaction happens inside the implementation, before anything is hashed or written, so
/// the orchestrator cannot persist a raw secret even by mistake.
/// </summary>
public interface IExecutionEvidenceWriter
{
    /// <param name="executionId">
    /// Whose evidence this is. Every write in one execution reaches one store, and the identity is
    /// what makes that checkable rather than conventional - an execution whose agent transcript
    /// went to one backend and whose verification output went to another would be an execution
    /// nobody could account for.
    /// </param>
    Task<StoredEvidence?> WriteAsync(
        Guid executionId, string text, EvidenceKind kind, string mediaType, CancellationToken ct);
}

/// <summary>Whether the process that claimed an execution is still running.</summary>
public interface IProcessLivenessProbe
{
    bool IsAlive(int processId, DateTimeOffset hostStartedAtUtc);

    int CurrentProcessId { get; }

    DateTimeOffset CurrentHostStartedAtUtc { get; }
}

/// <summary>
/// The Application owned port for keeping a lease alive.
///
/// Deliberately separate from the store: the heartbeat runs while the orchestrator is
/// blocked inside an agent or a verification run, and an EF DbContext is not safe to use
/// from two places at once. The implementation opens its own short lived connection per
/// beat, which is exactly what SQLite in WAL mode is good at.
/// </summary>
public interface IExecutionLeaseWriter
{
    /// <summary>Says the owner is still alive. Returns false once the execution is terminal.</summary>
    Task<bool> BeatAsync(Guid executionId, DateTimeOffset nowUtc, CancellationToken ct);
}
