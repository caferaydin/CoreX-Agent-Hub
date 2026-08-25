using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

/// <summary>How an abandoned post-commit execution was closed.</summary>
public enum PostCommitRecoveryState
{
    /// <summary>Verification was re-run against the commit that already existed.</summary>
    Reverified,

    /// <summary>Durable verification truth was already there and was used as it stood.</summary>
    Reused,

    /// <summary>Nothing could be established honestly; the execution was left alone.</summary>
    FailedClosed,
}

public sealed record PostCommitRecovery
{
    public required Guid ExecutionId { get; init; }

    public required Guid TaskId { get; init; }

    public required PostCommitRecoveryState State { get; init; }

    public required string ResultCommitSha { get; init; }

    public ExecutionOutcome? Outcome { get; init; }

    public TaskState? TaskState { get; init; }

    public required string Detail { get; init; }
}

public sealed record PostCommitRecoveryReport(IReadOnlyList<PostCommitRecovery> Recovered)
{
    public string Describe() => Recovered.Count == 0
        ? "no execution needed recovery after its result commit"
        : string.Join("; ", Recovered.Select(r =>
            $"{r.ExecutionId:D} {r.State} at {r.ResultCommitSha[..12]}: {r.Detail}"));
}

/// <summary>
/// Closes out an execution whose host died after the Hub had already committed its result.
///
/// This is the one crash the stale reconciler deliberately refuses to touch. Every terminal
/// error state asserts that the attempt produced nothing, and this attempt produced a
/// commit that is really in the repository, so writing Interrupted over it would make the
/// row contradict the repository - and the only ways to make Interrupted true are dropping
/// the commit, resetting the ref or rewriting history, which the Hub never does.
///
/// So recovery goes forward from what is already true rather than backwards into a tidier
/// story. The commit stays. The branch stays. The worktree stays. What is missing is the
/// answer to "how did this commit measure", and that has exactly two honest sources: truth
/// already persisted, or the commit measured again. Anything else - a Verify step that
/// claims an outcome with no commands behind it, commands with no step - is a contradiction
/// in the record, and is reported rather than resolved by guessing.
///
/// It cannot create a second result commit, and not because it is careful: there is no
/// workspace port here at all, so no code path from this class reaches one.
/// </summary>
public sealed class PostCommitRecoveryService
{
    private const string HubActor = "hub:recovery";

    private readonly Func<IHubStore> _open;
    private readonly IExecutionVerifier _verifier;
    private readonly TimeProvider _clock;
    private readonly bool _reviewRequired;

    /// <summary>
    /// <paramref name="reviewRequired"/> says whether this repository is one that gets an
    /// independent review.
    ///
    /// Recovery deliberately cannot perform one - it holds no reviewer and no workspace - so
    /// the only thing it can honestly do with the answer is refuse to call an unreviewed
    /// result Passed. A recovered execution that a reviewer never saw is exactly what
    /// ReviewUnusable means, and calling it Passed would let a dead host be the way around
    /// the review.
    /// </summary>
    public PostCommitRecoveryService(
        Func<IHubStore> open,
        IExecutionVerifier verifier,
        TimeProvider clock,
        bool reviewRequired = false)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reviewRequired = reviewRequired;
    }

    public async Task<PostCommitRecoveryReport> RecoverAsync(
        IReadOnlyList<PostCommitRecoveryRequired> pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var recovered = new List<PostCommitRecovery>();

        foreach (var item in pending)
        {
            recovered.Add(await RecoverOneAsync(item, ct));
        }

        return new PostCommitRecoveryReport(recovered);
    }

    private async Task<PostCommitRecovery> RecoverOneAsync(PostCommitRecoveryRequired item, CancellationToken ct)
    {
        var store = _open();

        var execution = await store.FindExecutionAsync(item.ExecutionId, ct);

        if (execution is null || execution.IsTerminal || execution.Outcome is not null)
        {
            return Closed(item, PostCommitRecoveryState.FailedClosed, "the execution is already finished");
        }

        if (execution.ResultCommitSha is not { } resultCommit)
        {
            // Not this path's business: an execution without a commit is the ordinary
            // interrupted case and belongs to the stale reconciler.
            return Closed(item, PostCommitRecoveryState.FailedClosed, "the execution has no result commit");
        }

        var task = await store.FindTaskAsync(execution.TaskId, ct);

        if (task is null)
        {
            return Closed(item, PostCommitRecoveryState.FailedClosed, "the task no longer exists");
        }

        var steps = await store.ListExecutionStepsAsync(execution.Id, ct);
        var verify = steps.FirstOrDefault(s => s.Kind == ExecutionStepKind.Verify);

        if (verify is null)
        {
            return Closed(item, PostCommitRecoveryState.FailedClosed, "the execution has no verify step to read");
        }

        var commands = await store.ListVerificationCommandsAsync(execution.Id, ct);

        var reading = Read(verify, commands);

        return reading.Kind switch
        {
            ReadingKind.Reuse => await FinishAsync(
                store, execution, task, resultCommit, reading.Outcome!.Value,
                PostCommitRecoveryState.Reused, reading.Detail, ct),

            ReadingKind.Remeasure => await RemeasureAsync(store, execution, task, resultCommit, verify, ct),

            _ => Closed(item, PostCommitRecoveryState.FailedClosed, reading.Detail),
        };
    }

    private enum ReadingKind
    {
        Reuse,
        Remeasure,
        Contradictory,
    }

    private sealed record Reading(ReadingKind Kind, ExecutionOutcome? Outcome, string Detail);

    /// <summary>
    /// Decides what the persisted record already establishes about the result commit.
    ///
    /// The step's state and its command rows are written in one transaction, which is what
    /// makes this readable at all: there is no legitimate crash that leaves one without the
    /// other. So the combinations that can occur are few, and the ones that cannot occur are
    /// treated as a damaged record rather than quietly reinterpreted.
    /// </summary>
    private static Reading Read(ExecutionStep verify, IReadOnlyList<CommandResult> commands)
    {
        var stepIsTerminal = verify.State
            is ExecutionStepState.Succeeded
            or ExecutionStepState.Failed
            or ExecutionStepState.TimedOut
            or ExecutionStepState.Cancelled
            or ExecutionStepState.Skipped
            or ExecutionStepState.Unsupported;

        if (!stepIsTerminal)
        {
            return commands.Count == 0
                ? new Reading(ReadingKind.Remeasure, null,
                    $"the verify step was still {verify.State} and nothing was recorded")

                // Commands without a finished step cannot happen: they are saved together.
                : new Reading(ReadingKind.Contradictory, null,
                    $"{commands.Count} verification command(s) are recorded but the verify step is still "
                    + $"{verify.State}, so the record contradicts itself");
        }

        if (verify.State == ExecutionStepState.Skipped)
        {
            // The pipeline had already decided not to measure this commit - the tree was
            // not the commit, or a gate stopped it - and died before completing. The reason
            // is durable, so the decision is reused rather than second guessed.
            return new Reading(ReadingKind.Reuse, ExecutionOutcome.VerificationFailed,
                "the verify step was already skipped: " + (verify.StateReason ?? "no reason recorded"));
        }

        if (commands.Count == 0)
        {
            return new Reading(ReadingKind.Contradictory, null,
                $"the verify step is {verify.State} but no verification command was recorded, so there is "
                + "nothing behind the claim");
        }

        var requiredFailures = commands.Count(c => c.Required && !c.IsPass);
        var passed = requiredFailures == 0;

        // A step that says it succeeded over a failing required command, or the reverse, is
        // not a verdict this may pick a side of.
        var stepSaysPassed = verify.State == ExecutionStepState.Succeeded;

        if (stepSaysPassed != passed)
        {
            return new Reading(ReadingKind.Contradictory, null,
                $"the verify step is {verify.State} but {requiredFailures} required command(s) did not pass");
        }

        return new Reading(
            ReadingKind.Reuse,
            passed ? ExecutionOutcome.Passed : ExecutionOutcome.VerificationFailed,
            $"{commands.Count} recorded verification command(s) were used as they stand");
    }

    /// <summary>
    /// Measures the commit again, in the same disposable tree pinned to it that the pipeline
    /// would have used. The agent is not run again, nothing is committed again, and the
    /// commit being measured is the one already written down.
    /// </summary>
    private async Task<PostCommitRecovery> RemeasureAsync(
        IHubStore store,
        Execution execution,
        HubTask task,
        string resultCommit,
        ExecutionStep verify,
        CancellationToken ct)
    {
        var verification = await _verifier.VerifyAsync(
            task.RepositoryId, execution.BaseCommitSha, resultCommit, ct);

        var now = _clock.GetUtcNow();
        var passed = verification.Result?.RequiredPassed == true;
        var order = 0;

        foreach (var command in verification.Result?.Steps ?? [])
        {
            store.AddCommandResult(new CommandResult(
                Guid.CreateVersion7(),
                execution.OrganizationId,
                verify.Id,
                ++order,
                command.StepName,
                command.Executable,
                command.Arguments,
                command.Status,
                command.Required,
                command.ExitCode,
                command.DurationMilliseconds,
                command.TestsPassed,
                command.TestsFailed,
                command.TestsSkipped,
                now));
        }

        if (passed)
        {
            verify.Succeed(now);
        }
        else
        {
            verify.Fail(ExecutionCompletion.Bounded(verification.Detail), now);
        }

        store.AddOrchestrationEvent(new OrchestrationEvent(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            execution.Id,
            "RecoveryVerification",
            ExecutionCompletion.Bounded(verification.Detail),
            now));

        // The commands and the step state are saved together, exactly as the pipeline saves
        // them, so a crash inside recovery leaves the same readable record as a crash inside
        // the run.
        await store.SaveChangesAsync(ct);

        return await FinishAsync(
            store,
            execution,
            task,
            resultCommit,
            passed ? ExecutionOutcome.Passed : ExecutionOutcome.VerificationFailed,
            PostCommitRecoveryState.Reverified,
            verification.Detail,
            ct);
    }

    /// <summary>
    /// Runs the ordinary end of the pipeline: review is recorded as skipped because nobody
    /// looked, the execution completes with its outcome, and a passing run waits for a
    /// person exactly as it would have - unless this repository is reviewed, in which case a
    /// result no reviewer saw is not one that passed.
    /// </summary>
    private async Task<PostCommitRecovery> FinishAsync(
        IHubStore store,
        Execution execution,
        HubTask task,
        string resultCommit,
        ExecutionOutcome outcome,
        PostCommitRecoveryState state,
        string detail,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var steps = await store.ListExecutionStepsAsync(execution.Id, ct);
        var review = steps.FirstOrDefault(s => s.Kind == ExecutionStepKind.Review);

        var reviewed = review is { State: ExecutionStepState.Succeeded };

        if (review is { State: ExecutionStepState.Pending or ExecutionStepState.Running })
        {
            review.Skip(_reviewRequired ? ReviewNotRunAfterHostLoss : ReviewNotConfigured, now);
        }

        if (_reviewRequired && outcome == ExecutionOutcome.Passed)
        {
            if (reviewed)
            {
                // The review really happened before the host died, and its findings are on
                // the record. They are read rather than re-derived, and the frozen gate is
                // asked exactly as the pipeline would have asked it - because a second place
                // that decided what blocks is how two answers start to exist.
                var findings = await store.ListFindingsAsync(execution.Id, ct);

                if (ReviewFindingGate.HasBlockingFindings(findings))
                {
                    outcome = ExecutionOutcome.ReviewBlocked;
                    detail = detail + "; " + ReviewFindingGate.Describe(findings);
                }
            }
            else
            {
                // The machine verification stands and is recorded; what is missing is the
                // review, and its absence is stated rather than stepped over. Recovery holds
                // no reviewer, so it cannot obtain one - and it never invents a pass.
                outcome = ExecutionOutcome.ReviewUnusable;
                detail = detail + "; the host was lost before this result was reviewed, so no review exists";
            }
        }

        ExecutionCompletion.Apply(execution, task, outcome, detail, now);

        store.AddOrchestrationEvent(new OrchestrationEvent(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            execution.Id,
            "RecoveredAfterResultCommit",
            ExecutionCompletion.Bounded($"{state}: {outcome}: {detail}"),
            now));

        store.AddAuditEvent(new AuditEvent(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            "ExecutionRecovered",
            HubActor,
            ExecutionCompletion.Bounded($"{state} at {resultCommit[..12]}: {outcome}"),
            execution.TaskId,
            execution.Id,
            execution.Id.ToString("D"),
            now));

        await store.SaveChangesAsync(ct);

        return new PostCommitRecovery
        {
            ExecutionId = execution.Id,
            TaskId = execution.TaskId,
            State = state,
            ResultCommitSha = resultCommit,
            Outcome = execution.Outcome,
            TaskState = task.State,
            Detail = detail,
        };
    }

    private const string ReviewNotConfigured = "ReviewNotConfigured";

    private const string ReviewNotRunAfterHostLoss = "ReviewNotRunAfterHostLoss";

    private static PostCommitRecovery Closed(
        PostCommitRecoveryRequired item,
        PostCommitRecoveryState state,
        string detail) =>
        new()
        {
            ExecutionId = item.ExecutionId,
            TaskId = item.TaskId,
            State = state,
            ResultCommitSha = item.ResultCommitSha,
            Detail = detail,
        };
}
