using System.Runtime.Versioning;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Builds the situations a person actually decides about, out of persisted rows.
///
/// A passing execution comes from the real pipeline. A review-blocked one cannot, because
/// Phase 5 has no reviewer - so it is written directly, in exactly the shape the review
/// step would leave behind. That is the honest way to exercise the override without
/// inventing a reviewer to produce it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AcceptanceFixture
{
    public static AcceptanceService ServiceFor(IHubStore store) =>
        new(store, new VerificationTruthReader(store));

    public static AcceptanceService Service(OrchestrationHarness hub) => ServiceFor(hub.Store);

    /// <summary>The ordinary happy path: a real run that passed and is waiting for a person.</summary>
    public static async Task<Guid> RunPassingAsync(OrchestrationHarness hub)
    {
        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
        Assert.Equal(TaskState.AwaitingAcceptance, (await hub.TaskAsync()).State);

        return report.ExecutionId;
    }

    /// <summary>How a review-blocked execution should look when everything else went well.</summary>
    public sealed record ReviewBlockedOptions
    {
        public int AttemptNo { get; init; } = 1;

        public bool AgentSucceeded { get; init; } = true;

        public bool WithResultCommit { get; init; } = true;

        public bool WithBlockingFinding { get; init; } = true;

        /// <summary>A finding anchored outside the diff, which never blocks.</summary>
        public bool FindingIsOutOfScope { get; init; }

        public bool UnexpectedAgentCommit { get; init; }

        public VerificationCommandStatus RequiredVerification { get; init; } = VerificationCommandStatus.Passed;

        /// <summary>When false, no verification command rows are written at all.</summary>
        public bool WithVerificationCommands { get; init; } = true;

        /// <summary>Any outcome may be persisted, so the closed set can be tested against all of them.</summary>
        public ExecutionOutcome Outcome { get; init; } = ExecutionOutcome.ReviewBlocked;

        public TaskState TaskState { get; init; } = TaskState.Ready;
    }

    /// <summary>
    /// Persists a completed execution with the structure a review would have left, and
    /// leaves the task where that outcome would have left it.
    /// </summary>
    public static async Task<DomainExecution> PersistCompletedAsync(
        OrchestrationHarness hub,
        ReviewBlockedOptions options)
    {
        var now = hub.Clock.GetUtcNow();
        var task = await hub.TaskAsync();

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            hub.OrganizationId,
            hub.TaskId,
            options.AttemptNo,
            new string('a', 40),
            $"agent/HUB-001/{options.AttemptNo}",
            Path.Combine(hub.WorkspaceRoot, $"HUB-001-{options.AttemptNo}"),
            null,
            now);

        var steps = new List<ExecutionStep>();
        var order = 0;

        foreach (var kind in new[]
                 {
                     ExecutionStepKind.PrepareWorkspace, ExecutionStepKind.BuildContext,
                     ExecutionStepKind.Agent, ExecutionStepKind.CaptureChanges,
                     ExecutionStepKind.Verify, ExecutionStepKind.Review,
                 })
        {
            var step = new ExecutionStep(
                Guid.CreateVersion7(), hub.OrganizationId, execution.Id, ++order, kind, now);

            if (kind == ExecutionStepKind.Agent && !options.AgentSucceeded)
            {
                step.Fail("the agent did not finish", now);
            }
            else
            {
                step.Succeed(now);
            }

            steps.Add(step);
        }

        if (options.UnexpectedAgentCommit)
        {
            execution.RecordUnexpectedCommits([new string('b', 40)], now);
        }

        if (options.WithResultCommit && ExecutionStateRules.ProducesResultCommit(options.Outcome))
        {
            execution.RecordResultCommit(new string('c', 40), now);
        }

        execution.Complete(options.Outcome, now);

        hub.Store.AddExecution(execution);

        foreach (var step in steps)
        {
            hub.Store.AddExecutionStep(step);
        }

        if (options.WithVerificationCommands)
        {
            var verify = steps.Single(s => s.Kind == ExecutionStepKind.Verify);

            hub.Store.AddCommandResult(new CommandResult(
                Guid.CreateVersion7(),
                hub.OrganizationId,
                verify.Id,
                1,
                "test",
                "fixture",
                ["report", "results.trx"],
                options.RequiredVerification,
                required: true,
                exitCode: options.RequiredVerification == VerificationCommandStatus.Passed ? 0 : 1,
                durationMilliseconds: 900,
                testsPassed: 11,
                testsFailed: 0,
                testsSkipped: 0,
                now));
        }

        if (options.WithBlockingFinding)
        {
            var review = steps.Single(s => s.Kind == ExecutionStepKind.Review);

            hub.Store.AddFinding(new Finding(
                Guid.CreateVersion7(),
                hub.OrganizationId,
                review.Id,
                FindingSeverity.Major,
                FindingCategory.Correctness,
                "Empty input is not handled",
                "The new branch throws on an empty collection.",
                "src/existing.cs",
                12,
                "Guard the empty case.",
                FindingConfidence.High,
                options.FindingIsOutOfScope,
                now));
        }

        // The task ends where this outcome would have left it.
        if (task.State == TaskState.Ready)
        {
            task.MarkRunning(now);
        }

        switch (options.TaskState)
        {
            case TaskState.Ready:
                task.ReturnToReady("review blocked the work", now);
                break;
            case TaskState.AwaitingAcceptance:
                task.MarkAwaitingAcceptance(now);
                break;
            case TaskState.Running:
                break;
            default:
                throw new InvalidOperationException($"The fixture does not place a task in {options.TaskState}.");
        }

        await hub.Store.SaveChangesAsync(CancellationToken.None);
        return execution;
    }

    /// <summary>Answers every human criterion the task declares with one verdict.</summary>
    public static async Task<IReadOnlyList<HumanCriterionVerdictRecord>> HumanVerdictsAsync(
        OrchestrationHarness hub,
        HumanCriterionVerdict verdict = HumanCriterionVerdict.Met,
        string? note = null)
    {
        var task = await hub.TaskAsync();

        return
        [
            .. task.AcceptanceCriteria
                .Where(c => c.Kind == VerificationKind.Human)
                .Select(c => new HumanCriterionVerdictRecord(c.Id, verdict, note)),
        ];
    }
}
