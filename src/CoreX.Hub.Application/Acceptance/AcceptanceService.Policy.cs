using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Acceptance;

public sealed partial class AcceptanceService
{
    /// <summary>
    /// ADR-0028: what has to be true for there to be nothing left for a person to judge.
    ///
    /// Checked on top of the plain route, never instead of it, and against what was
    /// persisted rather than how the repository is configured now. Each condition closes a
    /// way the plain route can be right for a person and wrong for a policy:
    ///
    /// - a Human criterion is a question only a person can answer (ADR-0015 #6), so a task
    ///   that has one is never accepted on a person's behalf;
    /// - plain acceptance tolerates an optional machine criterion that is not met, because a
    ///   person is looking at it - a policy is not, so every one must be Met;
    /// - an execution passes when no reviewer is configured, with its Review step skipped -
    ///   so "passed" alone does not prove a review happened, and the step must have run.
    /// </summary>
    private async Task ValidatePolicyAcceptanceAsync(
        DecisionTarget target,
        MachineTruth machine,
        CancellationToken ct)
    {
        var task = target.Task;
        var execution = target.Execution;

        var human = task.AcceptanceCriteria.Count(c => c.Kind == VerificationKind.Human);

        if (human > 0)
        {
            throw new AcceptanceException(
                $"Task '{task.TaskKey}' has {human} human criteria; a person answers those, so a policy "
                + "does not accept it.");
        }

        if (machine.Results.Count == 0)
        {
            throw new AcceptanceException(
                $"Task '{task.TaskKey}' has no machine criterion, so there is no evidence for a policy to "
                + "accept on.");
        }

        var unmet = machine.Results.Where(r => r.Verdict != MachineCriterionVerdict.Met).ToList();

        if (unmet.Count > 0)
        {
            var detail = string.Join(
                ", ", unmet.Select(r => $"{r.Order} ('{r.Text}': {r.VerificationRef}={r.VerificationStatus})"));

            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} has machine criteria that are not met ({detail}); a policy "
                + "accepts only work that meets every one.");
        }

        if (execution.UnexpectedAgentCommitDetected)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} recorded an unexpected agent commit.");
        }

        if (execution.ResultCommitSha is null)
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} produced no result commit, so there is no work to accept.");
        }

        var steps = await _store.ListExecutionStepsAsync(execution.Id, ct);

        foreach (var kind in new[] { ExecutionStepKind.Agent, ExecutionStepKind.Verify, ExecutionStepKind.Review })
        {
            var step = steps.FirstOrDefault(s => s.Kind == kind);

            if (step is null || step.State != ExecutionStepState.Succeeded)
            {
                var why = kind == ExecutionStepKind.Review && step?.State == ExecutionStepState.Skipped
                    ? " - no independent review ran, and a policy does not accept unreviewed work"
                    : string.Empty;

                throw new AcceptanceException(
                    $"The {kind} step of attempt {execution.AttemptNo} is "
                    + $"{step?.State.ToString() ?? "missing"}, not Succeeded{why}.");
            }
        }

        await EnsureRequiredVerificationPassedAsync(execution, ct);

        // The one blocking authority, asked again rather than trusted from the outcome: a
        // policy that accepts must be able to say the review gate would not have blocked.
        var findings = await _store.ListFindingsAsync(execution.Id, ct);

        if (ReviewFindingGate.HasBlockingFindings(findings))
        {
            throw new AcceptanceException(
                $"Attempt {execution.AttemptNo} carries blocking review findings: "
                + $"{ReviewFindingGate.Describe(findings)}.");
        }
    }

    /// <summary>
    /// What a policy decision says about itself. Built from the evidence it accepted on, so
    /// the row explains the decision without anyone having to re-derive it.
    /// </summary>
    private static string PolicyReason(MachineTruth machine) =>
        $"autopilot: all {machine.Results.Count} machine criteria met "
        + $"({string.Join(", ", machine.Results.Select(r => r.VerificationRef))}), review clean, "
        + "no human criteria";
}
