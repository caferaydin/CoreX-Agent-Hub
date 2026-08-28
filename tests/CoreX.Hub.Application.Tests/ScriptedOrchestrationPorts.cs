using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The production workspace port with a seam at each moment a test needs to interfere with.
///
/// Everything still goes through the real gateway, the real workspace module and real git.
/// What this adds is the ability to make the world change at an exact point - the branch
/// moving between pinning and materialising, a host dying before the worktree exists - and
/// to observe which token the orchestrator handed to the sections that must not be skipped.
/// </summary>
internal sealed class ScriptedWorkspace(IExecutionWorkspace inner) : IExecutionWorkspace
{
    /// <summary>Runs once the commit has been pinned and before any worktree exists.</summary>
    public Action? AfterPreflight { get; set; }

    /// <summary>Thrown instead of materialising the worktree: a host that died mid preparation.</summary>
    public Exception? FailPreparation { get; set; }

    /// <summary>
    /// Runs after the execution row exists and while the worktree is being prepared.
    ///
    /// The seam a test needs to press stop inside PreparingWorkspace, which is the earliest
    /// stage an execution can be cancelled in at all.
    /// </summary>
    public Action? BeforePrepare { get; set; }

    /// <summary>Whether preparation was reached at all.</summary>
    public bool PrepareAttempted { get; private set; }

    /// <summary>
    /// Makes preparation report that it could not produce a worktree.
    ///
    /// A refusal rather than a throw, because that is the shortest honest route to a
    /// terminal transition: the pipeline closes the step and terminates, which is exactly
    /// two writes and nothing external in between.
    /// </summary>
    public bool RefusePreparation { get; set; }

    /// <summary>Makes preparation claim a different base commit than the one that was pinned.</summary>
    public string? ReportPreparedBaseAs { get; set; }

    /// <summary>Runs immediately before the changes are captured.</summary>
    public Action? BeforeCapture { get; set; }

    public string? PinnedBaseCommitSha { get; private set; }

    /// <summary>Whether the token the capture was given had already been cancelled.</summary>
    public bool CaptureSawCancellation { get; private set; }

    public string? CapturedHeadSha { get; private set; }

    public bool ResultCommitAttempted { get; private set; }

    public async Task<IExecutionWorkspacePreflight> PreflightAsync(Guid repositoryId, CancellationToken ct)
    {
        var preflight = await inner.PreflightAsync(repositoryId, ct);
        PinnedBaseCommitSha = preflight.BaseCommitSha;

        AfterPreflight?.Invoke();

        return preflight;
    }

    public async Task<PreparedExecutionWorkspace?> PrepareAsync(
        Guid executionId,
        Guid repositoryId,
        IExecutionWorkspacePreflight preflight,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        PrepareAttempted = true;
        BeforePrepare?.Invoke();

        if (FailPreparation is { } failure)
        {
            throw failure;
        }

        if (RefusePreparation)
        {
            return null;
        }

        var prepared = await inner.PrepareAsync(executionId, repositoryId, preflight, taskKey, attemptNo, ct);

        return prepared is null || ReportPreparedBaseAs is null
            ? prepared
            : prepared with { BaseCommitSha = ReportPreparedBaseAs };
    }

    public Task<ExecutionCapture> CaptureAsync(PreparedExecutionWorkspace workspace, CancellationToken ct)
    {
        // Cancelled first, then observed: if the orchestrator had handed the caller's token
        // down here, this is where it would show.
        BeforeCapture?.Invoke();
        CaptureSawCancellation = ct.IsCancellationRequested;

        return Capture(workspace, ct);
    }

    public Task<ResultCommitOutcomeView> CreateResultCommitAsync(
        PreparedExecutionWorkspace workspace,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        ResultCommitAttempted = true;
        return inner.CreateResultCommitAsync(workspace, taskKey, attemptNo, ct);
    }

    private async Task<ExecutionCapture> Capture(PreparedExecutionWorkspace workspace, CancellationToken ct)
    {
        var capture = await inner.CaptureAsync(workspace, ct);
        CapturedHeadSha = capture.ObservedHeadSha;

        return capture;
    }
}

/// <summary>The production verification port, with a seam for cancelling while it runs.</summary>
internal sealed class ScriptedVerifier(IExecutionVerifier inner) : IExecutionVerifier
{
    public Action? BeforeVerify { get; set; }

    /// <summary>Runs once the measurement is done and before anything about it is written.</summary>
    public Action? AfterVerify { get; set; }

    /// <summary>Whether verification was given a token that had already been cancelled.</summary>
    public bool VerifySawCancellation { get; private set; }

    /// <summary>The commits the run was asked to measure, so a test can check which they were.</summary>
    public string? BaseCommitSha { get; private set; }

    public string? ResultCommitSha { get; private set; }

    public Task<VerificationOutcomeView> VerifyAsync(
        Guid executionId,
        Guid repositoryId,
        string baseCommitSha,
        string resultCommitSha,
        CancellationToken ct)
    {
        BaseCommitSha = baseCommitSha;
        ResultCommitSha = resultCommitSha;

        BeforeVerify?.Invoke();
        VerifySawCancellation = ct.IsCancellationRequested;

        var outcome = inner.VerifyAsync(executionId, repositoryId, baseCommitSha, resultCommitSha, ct);

        AfterVerify?.Invoke();

        return outcome;
    }
}

/// <summary>
/// A verification command that cancels the caller and reports itself cancelled, without
/// launching anything.
///
/// It stands in for the real thing being interrupted part way through: the point of the
/// scenario is what the Hub does with a half finished measurement, and waiting for a real
/// process to be killed would only make that slower to find out.
/// </summary>
internal sealed class CancellingCommandExecutor(CancellationTokenSource trigger, TimeProvider clock)
    : IVerificationCommandExecutor
{
    public int Invocations { get; private set; }

    public Task<VerificationCommandResult> ExecuteAsync(
        VerificationCommand command,
        CancellationToken cancellationToken)
    {
        Invocations++;
        trigger.Cancel();

        var now = clock.GetUtcNow();

        return Task.FromResult(new VerificationCommandResult
        {
            TerminationReason = VerificationTerminationReason.Cancelled,
            StartedAtUtc = now,
            EndedAtUtc = now,
        });
    }
}

/// <summary>
/// The production context builder, with a seam for cancelling while it runs.
///
/// BuildingContext is the second stage an execution can be stopped in, and the only one
/// between a prepared worktree and a launched agent.
/// </summary>
internal sealed class ScriptedContextBuilder(IExecutionContextBuilder inner) : IExecutionContextBuilder
{
    public Action? BeforeBuild { get; set; }

    public bool BuildAttempted { get; private set; }

    public Task<BuiltContextPack?> BuildAsync(
        Guid executionId,
        Guid repositoryId,
        CoreX.Hub.Domain.Tasks.HubTask task,
        int attemptNo,
        string baseCommitSha,
        CancellationToken ct)
    {
        BuildAttempted = true;
        BeforeBuild?.Invoke();

        return inner.BuildAsync(executionId, repositoryId, task, attemptNo, baseCommitSha, ct);
    }
}
