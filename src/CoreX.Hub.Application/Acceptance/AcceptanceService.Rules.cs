using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Acceptance;

public sealed partial class AcceptanceService
{
    /// <summary>The execution a decision names, and whether it is still the current attempt.</summary>
    private sealed record DecisionTarget(HubTask Task, Execution Execution, Execution Latest)
    {
        public bool IsLatest => Execution.Id == Latest.Id;
    }

    private sealed record MachineTruth(
        IReadOnlyList<MachineCriterionResult> Results,
        IReadOnlyList<string> Problems);

    /// <summary>
    /// Finds the exact execution a decision is about.
    ///
    /// The attempt is named rather than guessed, because a human decision is about a
    /// particular piece of work; silently applying it to whatever ran most recently would
    /// let somebody accept something they never looked at.
    /// </summary>
    private async Task<DecisionTarget> ResolveAsync(
        string taskKey,
        int attemptNo,
        string? organizationSlug,
        CancellationToken ct)
    {
        if (attemptNo <= 0)
        {
            throw new AcceptanceException($"Attempt number {attemptNo} is not valid; attempts start at 1.");
        }

        if (!TaskKey.TryParse(taskKey, out var parsed))
        {
            throw new AcceptanceException(
                $"'{taskKey}' is not a valid task key. Expected <PROJECT>-<3 digit>, for example HUB-001.");
        }

        var matches = await _store.FindTasksByKeyAsync(parsed.Value, ct);

        if (!string.IsNullOrWhiteSpace(organizationSlug))
        {
            var organization = await _store.FindOrganizationBySlugAsync(organizationSlug, ct)
                ?? throw new AcceptanceException($"Organization '{organizationSlug}' was not found.");

            matches = [.. matches.Where(t => t.OrganizationId == organization.Id)];
        }

        var task = matches.Count switch
        {
            0 => throw new AcceptanceException($"Task '{parsed}' was not found."),
            1 => matches[0],
            _ => throw new AcceptanceException(
                $"Task '{parsed}' exists in more than one organization; pass --org to say which."),
        };

        var executions = await _store.ListExecutionsForTaskAsync(task.Id, ct);

        if (executions.Count == 0)
        {
            throw new AcceptanceException($"Task '{parsed}' has no executions, so there is nothing to decide on.");
        }

        // Latest is the highest attempt number, which is what "the current attempt" means.
        // Timestamps and insertion order are properties of when rows were written, not of
        // which attempt came after which.
        var latest = executions.MaxBy(e => e.AttemptNo)!;

        var execution = executions.FirstOrDefault(e => e.AttemptNo == attemptNo)
            ?? throw new AcceptanceException(
                $"Task '{parsed}' has no attempt {attemptNo}; the latest is {latest.AttemptNo}.");

        return new DecisionTarget(task, execution, latest);
    }

    private static void EnsureTargetIsLatest(DecisionTarget target, string what)
    {
        if (!target.IsLatest)
        {
            throw new AcceptanceException(
                $"Attempt {target.Execution.AttemptNo} of '{target.Task.TaskKey}' is not the latest "
                + $"(attempt {target.Latest.AttemptNo} exists), so it cannot receive {what}.");
        }
    }

    /// <summary>I-A9: one decision per execution, and it is never revised.</summary>
    private async Task EnsureUndecidedAsync(Execution execution, CancellationToken ct)
    {
        if (await _store.FindAcceptanceDecisionAsync(execution.Id, ct) is { } existing)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} already has a recorded decision ({existing.Decision} by "
                + $"{existing.Actor} at {existing.DecidedAtUtc:u}). A decision is written once and never revised.");
        }
    }

    /// <summary>
    /// I-A1: the ordinary route. The automatic gate has already decided the work is good;
    /// what remains is a person saying so.
    /// </summary>
    private static void ValidatePlainAcceptance(DecisionTarget target)
    {
        if (target.Task.State != TaskState.AwaitingAcceptance)
        {
            throw new AcceptanceException(
                $"Task '{target.Task.TaskKey}' is {target.Task.State}, not awaiting acceptance.");
        }

        EnsureTargetIsLatest(target, "an acceptance");

        if (target.Execution.State != ExecutionState.Completed)
        {
            throw new AcceptanceException(
                $"Attempt {target.Execution.AttemptNo} is {target.Execution.State}, not Completed.");
        }

        // Closed set, default deny: only an outcome the frozen rules name as plainly
        // acceptable qualifies, so a new outcome is refused until somebody decides otherwise.
        if (target.Execution.Outcome is not { } outcome || !AcceptanceRules.IsPlainAcceptanceEligible(outcome))
        {
            throw new AcceptanceException(
                $"Attempt {target.Execution.AttemptNo} ended as "
                + $"{target.Execution.Outcome?.ToString() ?? "no outcome"}, which plain acceptance does not cover.");
        }
    }

    /// <summary>
    /// I-A2 / I-A3: accepting work a review blocked.
    ///
    /// Every prerequisite is checked against what was persisted, never against how things
    /// are configured now, and every one of them is about the same question: was the
    /// blocking finding the only thing standing in the way? If anything else also failed,
    /// this is not an override, it is a way around a different gate.
    /// </summary>
    private async Task<string> ValidateOverrideAsync(
        DecisionTarget target,
        AcceptanceRequest request,
        CancellationToken ct)
    {
        var execution = target.Execution;

        if (target.Task.State != TaskState.Ready)
        {
            throw new AcceptanceException(
                $"Task '{target.Task.TaskKey}' is {target.Task.State}; a review override starts from Ready.");
        }

        // I-A11: a newer attempt exists, so this one is history. Overriding it would accept
        // work that has already been superseded.
        EnsureTargetIsLatest(target, "a review override");

        if (execution.State != ExecutionState.Completed)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} is {execution.State}, not Completed.");
        }

        // I-A4. The switch is closed and denies by default, so a future outcome is not
        // overridable until somebody writes it down here on purpose.
        //
        // It is also what proves the scope and secret gates passed: each of those blocks
        // with its own outcome, so an execution that reaches ReviewBlocked is one where none
        // of them fired.
        if (execution.Outcome is not { } outcome || !AcceptanceRules.IsReviewOverrideEligible(outcome))
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} ended as {execution.Outcome?.ToString() ?? "no outcome"}; "
                + "only a blocking review may be overridden.");
        }

        var reason = (request.Reason ?? string.Empty).Trim();

        if (reason.Length == 0)
        {
            throw new AcceptanceException("A review override requires a reason; it may never be silent.");
        }

        // K1 stated separately rather than inferred: HEAD moving away from the pinned base
        // is a runtime policy violation, and an override must not quietly cover one.
        if (execution.UnexpectedAgentCommitDetected)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} recorded an unexpected agent commit; that is a runtime policy "
                + "violation, not a review finding.");
        }

        if (execution.ResultCommitSha is null)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} produced no result commit, so there is no work to accept.");
        }

        var steps = await _store.ListExecutionStepsAsync(execution.Id, ct);

        var agent = steps.FirstOrDefault(s => s.Kind == ExecutionStepKind.Agent);

        if (agent is null || agent.State != ExecutionStepState.Succeeded)
        {
            throw new AcceptanceException(
                $"The agent step of attempt {execution.AttemptNo} is "
                + $"{agent?.State.ToString() ?? "missing"}, not Succeeded.");
        }

        await EnsureRequiredVerificationPassedAsync(execution, ct);

        // The same gate the review step is judged by, not a second reading of it. An
        // execution can be marked ReviewBlocked and yet carry findings that do not block -
        // a stale outcome, a hand written row - and overriding that would be answering for
        // a blocker nobody found.
        var findings = await _store.ListFindingsAsync(execution.Id, ct);

        if (!ReviewFindingGate.HasBlockingFindings(findings))
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} has no blocking finding, so there is nothing for an override "
                + $"to answer for: {ReviewFindingGate.Describe(findings)}.");
        }

        return reason;
    }

    /// <summary>
    /// Releases the worktree of an execution whose work is now safely in a commit.
    ///
    /// Runs after the decision is committed and never inside its transaction, because a
    /// locked directory must not be able to unmake a person's answer. It therefore reports
    /// rather than throws: whatever happens here, the decision above already happened.
    ///
    /// Only the case G.6 states plainly is automatic - a passing execution a person has
    /// accepted or rejected. An override leaves the worktree alone, because a blocked review
    /// is a lifecycle Phase 7 owns; sending the work back leaves it alone, because another
    /// iteration was explicitly asked for; and an execution with no commit is never touched,
    /// because its worktree is the only copy of the work (D6).
    /// </summary>
    private async Task<WorkspaceCleanupResult> ReleaseWorkspaceAsync(
        DecisionTarget target,
        AcceptanceDecision decision,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var execution = target.Execution;

        if (decision.OverrideUsed
            || decision.Decision is not (AcceptanceDecisionKind.Accept or AcceptanceDecisionKind.Reject))
        {
            return WorkspaceCleanupResult.NotAttempted(
                $"{decision.Decision}{(decision.OverrideUsed ? " by review override" : string.Empty)} "
                + "does not release a worktree");
        }

        // Written as a positive match: only Passed releases, so a new outcome is left alone
        // by default rather than swept in by an inverted test.
        if (execution.Outcome is not ExecutionOutcome.Passed)
        {
            return WorkspaceCleanupResult.NotAttempted(
                $"only a passing execution is released automatically; this one is "
                + $"{execution.Outcome?.ToString() ?? "without an outcome"}");
        }

        if (execution.ResultCommitSha is null)
        {
            // Unreachable through Passed, which requires a commit. Stated anyway, because
            // D6 is the invariant that must not depend on another rule staying true.
            return new WorkspaceCleanupResult(
                WorkspaceCleanupOutcome.DeniedNoResultCommit,
                "the execution produced no result commit, so its worktree holds the only copy of the work");
        }

        if (_release is null)
        {
            return WorkspaceCleanupResult.NotAttempted("no workspace release port is wired");
        }

        WorkspaceCleanupResult cleanup;

        try
        {
            cleanup = await _release.ReleaseAsync(target.Task.RepositoryId, execution, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure out here is an operational fact about a machine, not a verdict about
            // the work, so it is reported in those terms rather than thrown at somebody who
            // has already decided.
            return new WorkspaceCleanupResult(
                WorkspaceCleanupOutcome.Failed,
                $"the worktree could not be released: {ex.Message}");
        }

        if (!cleanup.Succeeded)
        {
            return cleanup;
        }

        // A second, separate transaction. If this one fails the worktree is gone and the row
        // still says Active, which reconciliation reports - the opposite order would risk a
        // row claiming the work was released while it is still on disk.
        execution.MarkWorkspaceReleased(nowUtc);
        await _store.SaveChangesAsync(ct);

        return cleanup;
    }

    /// <summary>
    /// The required half of verification, read from what was recorded rather than from the
    /// profile as it stands now - which is exactly why requiredness lives on the row.
    ///
    /// Missing rows fail closed. An execution whose verification cannot be read is not one
    /// where "only the review blocked".
    /// </summary>
    private async Task EnsureRequiredVerificationPassedAsync(Execution execution, CancellationToken ct)
    {
        var commands = await _truth.ListAsync(execution.Id, ct);

        if (commands.Count == 0)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} recorded no verification commands, so it cannot be shown that "
                + "the required verification passed.");
        }

        var required = commands.Where(c => c.Required).ToList();

        if (required.Count == 0)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} recorded no required verification step.");
        }

        var notPassed = required.Where(c => !c.IsPass).ToList();

        if (notPassed.Count > 0)
        {
            var detail = string.Join(", ", notPassed.Select(c => $"{c.StepName}={c.Status}"));

            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} has required verification that did not pass ({detail}).");
        }
    }
}
