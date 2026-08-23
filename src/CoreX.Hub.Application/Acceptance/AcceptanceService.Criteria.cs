using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Acceptance;

public sealed partial class AcceptanceService
{
    /// <summary>
    /// Rebuilds what the machine found, from the durable record and nothing else.
    ///
    /// Three outcomes, not two. A step that passed makes its criterion met. A step that ran
    /// and did not pass makes it unmet, and the reason it did not pass is carried along
    /// rather than flattened. A step whose truth cannot be established at all is neither -
    /// it is a problem, collected separately, because turning missing evidence into "not
    /// met" would quietly make an unanswerable question answerable.
    /// </summary>
    private async Task<MachineTruth> ReadMachineTruthAsync(
        HubTask task,
        Execution execution,
        CancellationToken ct)
    {
        var results = new List<MachineCriterionResult>();
        var problems = new List<string>();

        var criteria = task.AcceptanceCriteria
            .Where(c => c.Kind == VerificationKind.Machine)
            .OrderBy(c => c.Order);

        foreach (var criterion in criteria)
        {
            var lookup = await _truth.ResolveAsync(execution.Id, criterion.VerificationRef, ct);

            if (lookup.Command is not { } command)
            {
                problems.Add($"criterion {criterion.Order} ('{criterion.Text}'): {lookup.Detail}");
                continue;
            }

            // A row written before machine truth was durable. It says nothing happened that
            // anyone observed, which is not the same as saying something failed.
            if (command.Status == VerificationCommandStatus.Unavailable)
            {
                problems.Add(
                    $"criterion {criterion.Order} ('{criterion.Text}'): no status was recorded for "
                    + $"verification step '{command.StepName}'");
                continue;
            }

            results.Add(new MachineCriterionResult
            {
                CriterionId = criterion.Id,
                Order = criterion.Order,
                Text = criterion.Text,
                VerificationRef = command.StepName,
                Verdict = command.IsPass ? MachineCriterionVerdict.Met : MachineCriterionVerdict.NotMet,
                VerificationStatus = command.Status,
                Required = command.Required,
                ExitCode = command.ExitCode,
                TestsPassed = command.TestsPassed,
                TestsFailed = command.TestsFailed,
                TestsSkipped = command.TestsSkipped,
            });
        }

        return new MachineTruth(results, problems);
    }

    /// <summary>
    /// Refuses to decide while the machine's part of the answer is unknown.
    ///
    /// Note what this does not say: it does not require every machine criterion to be met.
    /// An optional step that failed is a real, readable result and the execution's own
    /// outcome already accounted for it. What cannot be tolerated is not knowing.
    /// </summary>
    private static void EnsureMachineTruthEstablished(MachineTruth machine)
    {
        if (machine.Problems.Count == 0)
        {
            return;
        }

        throw new AcceptanceException(
            "Machine truth could not be established, so there is nothing to accept against: "
            + string.Join("; ", machine.Problems));
    }

    private static IReadOnlyList<HumanCriterionPrompt> HumanCriteriaOf(HubTask task) =>
        [.. task.AcceptanceCriteria
            .Where(c => c.Kind == VerificationKind.Human)
            .OrderBy(c => c.Order)
            .Select(c => new HumanCriterionPrompt(c.Id, c.Order, c.Text, c.HumanCheckHint))];

    /// <summary>
    /// I-A5: every Human criterion gets exactly one verdict, and only Human criteria do.
    ///
    /// The verdict itself is not judged. Met, NotMet and NotApplicable are all answers a
    /// person is entitled to give, and requiring them all to be Met would turn a checklist
    /// into a formality nobody reads. What is enforced is that each question was asked once
    /// and answered once, and that nothing was answered on the machine's behalf.
    /// </summary>
    private static IReadOnlyList<HumanCriterionVerdictRecord> ValidateHumanVerdicts(
        HubTask task,
        IReadOnlyList<HumanCriterionVerdictRecord> supplied)
    {
        var expected = task.AcceptanceCriteria
            .Where(c => c.Kind == VerificationKind.Human)
            .OrderBy(c => c.Order)
            .ToList();

        var byId = task.AcceptanceCriteria.ToDictionary(c => c.Id);
        var seen = new HashSet<Guid>();

        foreach (var verdict in supplied)
        {
            if (!byId.TryGetValue(verdict.CriterionId, out var criterion))
            {
                throw new AcceptanceException(
                    $"Criterion {verdict.CriterionId} does not belong to task '{task.TaskKey}'.");
            }

            if (criterion.Kind != VerificationKind.Human)
            {
                throw new AcceptanceException(
                    $"Criterion {criterion.Order} ('{criterion.Text}') is "
                    + $"{criterion.Kind?.ToString() ?? "undeclared"}, so a human verdict is not what decides it.");
            }

            if (!seen.Add(verdict.CriterionId))
            {
                throw new AcceptanceException(
                    $"Criterion {criterion.Order} ('{criterion.Text}') was answered more than once.");
            }
        }

        var missing = expected.Where(c => !seen.Contains(c.Id)).ToList();

        if (missing.Count > 0)
        {
            var detail = string.Join(", ", missing.Select(c => $"{c.Order} ('{c.Text}')"));
            throw new AcceptanceException($"Every human criterion needs a verdict; missing: {detail}.");
        }

        // Recorded in the task's own order so the stored decision reads the way the
        // checklist does, with the person's note carried through untouched.
        return
        [
            .. expected
                .Select(c => supplied.First(v => v.CriterionId == c.Id))
                .Select(v => new HumanCriterionVerdictRecord(v.CriterionId, v.Verdict, v.Note)),
        ];
    }
}
