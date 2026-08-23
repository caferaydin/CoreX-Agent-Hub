using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Acceptance;

/// <summary>
/// The only place a person's decision about an execution is recorded.
///
/// It writes three things and nothing else: the task's new state, one immutable
/// <see cref="AcceptanceDecision"/>, and an <see cref="AuditEvent"/> - together, in one
/// transaction. It never touches the execution, the findings, the verification rows or git,
/// because none of those are the person's to change. Accepting means somebody took
/// responsibility; it does not mean anything was merged, pushed or released.
///
/// Machine criteria are read here and never written. Human criteria are the opposite: the
/// person supplies a verdict for each, and this service records whichever verdict they gave
/// rather than insisting they all be positive - "I looked and it is not met" is an answer,
/// and pretending otherwise would make the checklist a formality.
/// </summary>
public sealed partial class AcceptanceService
{
    private const string HubActor = "hub:acceptance";

    private readonly IHubStore _store;
    private readonly VerificationTruthReader _truth;
    private readonly IExecutionWorkspaceRelease? _release;

    /// <summary>
    /// <paramref name="release"/> is optional because cleanup is not part of the decision.
    /// Without it a decision is still recorded in full and simply reports that no worktree
    /// was released - which is the honest description of a Hub that has no workspace
    /// attached, and never a reason to refuse a person's answer.
    /// </summary>
    public AcceptanceService(
        IHubStore store,
        VerificationTruthReader truth,
        IExecutionWorkspaceRelease? release = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _truth = truth ?? throw new ArgumentNullException(nameof(truth));
        _release = release;
    }

    /// <summary>
    /// Everything a person needs before deciding: what the machine found, what they still
    /// have to answer, and whether the machine truth could be established at all.
    /// </summary>
    public async Task<AcceptancePreview> PreviewAsync(
        string taskKey,
        int attemptNo,
        string? organizationSlug,
        CancellationToken ct)
    {
        var target = await ResolveAsync(taskKey, attemptNo, organizationSlug, ct);
        var machine = await ReadMachineTruthAsync(target.Task, target.Execution, ct);

        return new AcceptancePreview
        {
            TaskKey = target.Task.TaskKey.Value,
            TaskState = target.Task.State,
            ExecutionId = target.Execution.Id,
            AttemptNo = target.Execution.AttemptNo,
            IsLatestExecution = target.IsLatest,
            ExecutionState = target.Execution.State,
            ExecutionOutcome = target.Execution.Outcome,
            ResultCommitSha = target.Execution.ResultCommitSha,
            MachineCriteria = machine.Results,
            MachineTruthProblems = machine.Problems,
            HumanCriteria = HumanCriteriaOf(target.Task),
            ExistingDecision = (await _store.FindAcceptanceDecisionAsync(target.Execution.Id, ct))?.Decision,
        };
    }

    /// <summary>
    /// Accepts an execution, by the ordinary route or by explicitly overriding a blocking
    /// review. The two are different decisions with different prerequisites and are never
    /// allowed to stand in for each other.
    /// </summary>
    public async Task<AcceptanceResult> AcceptAsync(
        AcceptanceRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveAsync(request.TaskKey, request.AttemptNo, request.OrganizationSlug, ct);

        await EnsureUndecidedAsync(target.Execution, ct);

        // Machine truth first, for both routes: a decision taken while the Hub cannot say
        // what the machine did is a decision taken on nothing.
        var machine = await ReadMachineTruthAsync(target.Task, target.Execution, ct);
        EnsureMachineTruthEstablished(machine);

        var verdicts = ValidateHumanVerdicts(target.Task, request.HumanVerdicts);

        var reason = request.UseOverride
            ? await ValidateOverrideAsync(target, request, ct)
            : ValidatePlainAcceptance(target, request);

        // One transaction, nothing external inside it.
        if (request.UseOverride)
        {
            target.Task.AcceptByReviewOverride(nowUtc);
        }
        else
        {
            target.Task.Accept(nowUtc);
        }

        var decision = new AcceptanceDecision(
            Guid.CreateVersion7(),
            target.Task.OrganizationId,
            target.Execution.Id,
            AcceptanceDecisionKind.Accept,
            request.Actor,
            reason,
            request.UseOverride,
            verdicts,
            nowUtc);

        _store.AddAcceptanceDecision(decision);

        _store.AddAuditEvent(AuditFor(
            target,
            request.UseOverride ? "AcceptanceOverridden" : "AcceptanceAccepted",
            request.Actor,
            Summarise(target, AcceptanceDecisionKind.Accept, request.UseOverride, reason, verdicts),
            nowUtc));

        await _store.SaveChangesAsync(ct);

        // The decision is now recorded. Everything below is housekeeping, and nothing it
        // does can take that back.
        var cleanup = await ReleaseWorkspaceAsync(target, decision, nowUtc, ct);

        return ResultFor(target, decision, machine.Results, cleanup);
    }

    /// <summary>
    /// Sends the work back. Not an acceptance route: no full human checklist is demanded,
    /// because a person who has seen enough to ask for changes should not have to answer
    /// every criterion first.
    /// </summary>
    public Task<AcceptanceResult> RequestChangesAsync(
        TaskDecisionRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct) =>
        DecideAsync(request, AcceptanceDecisionKind.RequestChanges, nowUtc, ct);

    /// <summary>Ends the task. Terminal, and the execution keeps whatever outcome it earned.</summary>
    public Task<AcceptanceResult> RejectAsync(
        TaskDecisionRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct) =>
        DecideAsync(request, AcceptanceDecisionKind.Reject, nowUtc, ct);

    private async Task<AcceptanceResult> DecideAsync(
        TaskDecisionRequest request,
        AcceptanceDecisionKind kind,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveAsync(request.TaskKey, request.AttemptNo, request.OrganizationSlug, ct);

        await EnsureUndecidedAsync(target.Execution, ct);

        if (target.Task.State != TaskState.AwaitingAcceptance)
        {
            throw new AcceptanceException(
                $"Task '{target.Task.TaskKey}' is {target.Task.State}; only a task awaiting acceptance can be "
                + $"{(kind == AcceptanceDecisionKind.Reject ? "rejected" : "sent back")}.");
        }

        EnsureTargetIsLatest(target, "a decision");

        if (kind == AcceptanceDecisionKind.Reject)
        {
            target.Task.Reject(request.Reason, nowUtc);
        }
        else
        {
            target.Task.ReturnToReady(request.Reason, nowUtc);
        }

        var decision = new AcceptanceDecision(
            Guid.CreateVersion7(),
            target.Task.OrganizationId,
            target.Execution.Id,
            kind,
            request.Actor,
            request.Reason,
            overrideUsed: false,
            [],
            nowUtc);

        _store.AddAcceptanceDecision(decision);

        _store.AddAuditEvent(AuditFor(
            target,
            kind == AcceptanceDecisionKind.Reject ? "AcceptanceRejected" : "AcceptanceChangesRequested",
            request.Actor,
            Summarise(target, kind, overrideUsed: false, request.Reason, []),
            nowUtc));

        await _store.SaveChangesAsync(ct);

        var cleanup = await ReleaseWorkspaceAsync(target, decision, nowUtc, ct);

        return ResultFor(target, decision, [], cleanup);
    }

    private static AcceptanceResult ResultFor(
        DecisionTarget target,
        AcceptanceDecision decision,
        IReadOnlyList<MachineCriterionResult> machine,
        WorkspaceCleanupResult cleanup) => new()
    {
        TaskKey = target.Task.TaskKey.Value,
        ExecutionId = target.Execution.Id,
        AttemptNo = target.Execution.AttemptNo,
        Decision = decision.Decision,
        OverrideUsed = decision.OverrideUsed,
        TaskState = target.Task.State,
        ExecutionState = target.Execution.State,
        ExecutionOutcome = target.Execution.Outcome,
        Reason = decision.Reason,
        MachineCriteria = machine,
        HumanVerdicts = decision.HumanCriterionVerdicts,
        WorkspaceCleanup = cleanup,
    };

    private static AuditEvent AuditFor(
        DecisionTarget target,
        string eventType,
        string actor,
        string summary,
        DateTimeOffset nowUtc) =>
        new(
            Guid.CreateVersion7(),
            target.Task.OrganizationId,
            eventType,
            actor,
            summary,
            target.Task.Id,
            target.Execution.Id,
            target.Execution.Id.ToString("D"),
            nowUtc);

    /// <summary>
    /// The audit line. An override says why in it, because ADR-0014 does not allow a silent
    /// one; the verdicts are counted rather than pasted in, since the decision row already
    /// holds them and an audit summary is not a place for a document.
    /// </summary>
    private static string Summarise(
        DecisionTarget target,
        AcceptanceDecisionKind kind,
        bool overrideUsed,
        string? reason,
        IReadOnlyList<HumanCriterionVerdictRecord> verdicts)
    {
        var head = $"{kind} {target.Task.TaskKey}#{target.Execution.AttemptNo}"
                   + $" ({target.Execution.State}"
                   + (target.Execution.Outcome is { } outcome ? $"/{outcome}" : string.Empty)
                   + ")";

        if (verdicts.Count > 0)
        {
            var met = verdicts.Count(v => v.Verdict == HumanCriterionVerdict.Met);
            head += $"; {met}/{verdicts.Count} human criteria met";
        }

        if (overrideUsed)
        {
            head += "; review override";
        }

        return reason is { Length: > 0 } ? head + ": " + reason : head;
    }
}
