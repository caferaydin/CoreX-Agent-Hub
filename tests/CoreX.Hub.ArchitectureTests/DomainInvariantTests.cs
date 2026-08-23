using System.Reflection;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Persistence;

// The Phase 2 assembly introduces a CoreX.Hub.Execution namespace, so the domain type of
// the same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Architecture invariants that can be proven from the types that exist today
/// (E.1 #6, #10, #11) plus the index/domain agreement the execution model depends on.
/// </summary>
public sealed class DomainInvariantTests
{
    [Fact]
    public void No_type_can_move_a_task_into_accepted_yet()
    {
        // E.1 #6: only AcceptanceService may write Accepted, and that service is Phase 5.
        // What is provable today: HubTask exposes no mutator that could reach the state.
        var mutators = typeof(HubTask)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        // Phase 5C added the lifecycle edges an orchestrator drives. None of them is a path
        // to Accepted, and the list stays closed so a new one cannot appear unnoticed.
        Assert.Equal(
            [
                "AddAcceptanceCriterion", "MarkAwaitingAcceptance", "MarkReady", "MarkRunning",
                "ReturnToReady", "SetBlockReason",
            ],
            mutators.Order(StringComparer.Ordinal));

        // Whatever the list contains, nothing in it transitions the task to a human's
        // decision: acceptance and rejection remain unreachable from the domain surface.
        Assert.DoesNotContain(mutators, m => m.StartsWith("Accept", StringComparison.Ordinal));
        Assert.DoesNotContain(mutators, m => m.StartsWith("Reject", StringComparison.Ordinal));
        Assert.DoesNotContain(mutators, m => m.Contains("MarkAccepted", StringComparison.Ordinal));

        var stateSetter = typeof(HubTask).GetProperty(nameof(HubTask.State))!.SetMethod;
        Assert.True(stateSetter is null || !stateSetter.IsPublic);
    }

    [Fact]
    public void Execution_outcome_cannot_be_written_from_outside_the_execution_aggregate()
    {
        // E.1 #11: acceptance must never rewrite an execution outcome.
        var outcomeSetter = typeof(DomainExecution).GetProperty(nameof(DomainExecution.Outcome))!.SetMethod;
        Assert.True(outcomeSetter is null || !outcomeSetter.IsPublic);

        // The acceptance record does not even model an outcome, so it cannot carry one back.
        Assert.DoesNotContain(
            typeof(AcceptanceDecision).GetProperties(),
            p => p.PropertyType == typeof(ExecutionOutcome) || p.PropertyType == typeof(ExecutionOutcome?));
    }

    [Fact]
    public void The_review_override_set_is_closed_and_defaults_to_deny()
    {
        // E.1 #10 / ADR-0014.
        var eligible = Enum.GetValues<ExecutionOutcome>()
            .Where(AcceptanceRules.IsReviewOverrideEligible)
            .ToList();

        Assert.Equal([ExecutionOutcome.ReviewBlocked], eligible);

        // An outcome value that does not exist yet must default to deny.
        Assert.False(AcceptanceRules.IsReviewOverrideEligible((ExecutionOutcome)9999));
        Assert.False(AcceptanceRules.IsPlainAcceptanceEligible((ExecutionOutcome)9999));
    }

    [Fact]
    public void The_active_execution_index_filter_matches_the_domain_state_classification()
    {
        foreach (var state in Enum.GetValues<ExecutionState>())
        {
            var inFilter = ExecutionModel.ActiveExecutionIndexFilter
                .Contains($"'{state}'", StringComparison.Ordinal);

            Assert.Equal(!ExecutionStateRules.IsTerminal(state), inFilter);
        }
    }

    [Fact]
    public void Blocked_outcomes_never_produce_a_result_commit()
    {
        // ADR-0016 / errata E1.
        Assert.False(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.ScopeViolation));
        Assert.False(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.SecretGuardBlocked));
        Assert.False(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.RuntimePolicyViolation));

        Assert.True(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.Passed));
        Assert.True(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.VerificationFailed));
        Assert.True(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.ReviewBlocked));
        Assert.True(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.ReviewUnusable));
    }

    [Fact]
    public void A_terminal_cancelled_execution_can_never_carry_a_result_commit()
    {
        // Cancelled means "no commit was produced", so the aggregate refuses to be told
        // otherwise. The invariant lives in the model rather than in the caller, because a
        // pipeline that reached for Cancelled after committing would otherwise produce a row
        // that contradicts itself and no reader could resolve.
        var execution = NewExecution();
        execution.RecordResultCommit("9876543210fedcba9876543210fedcba98765432", Now);

        Assert.ThrowsAny<Exception>(() => execution.Terminate(ExecutionState.Cancelled, Now));
        Assert.False(execution.IsTerminal);

        // The other terminal states stay reachable: a host that died after committing really
        // does hold a commit, and hiding it would be the worse lie.
        execution.Terminate(ExecutionState.Interrupted, Now);
        Assert.Equal(ExecutionState.Interrupted, execution.State);
    }

    [Fact]
    public void An_execution_cannot_be_based_on_a_commit_that_names_no_object()
    {
        // The null object id is what a placeholder looks like when a row has to exist before
        // a commit has been resolved. Everything downstream - the baseline key, the context
        // pack, the captured diff - would then be keyed on nothing.
        Assert.ThrowsAny<Exception>(() => NewExecution("0000000000000000000000000000000000000000"));
    }

    private static DateTimeOffset Now { get; } = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static DomainExecution NewExecution(
        string baseCommitSha = "1f0c2b9a4d6e8f10325476981badc0ffee1234ab") =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            baseCommitSha,
            "agent/HUB-001/1",
            "(not created yet)",
            null,
            Now);

    [Fact]
    public void The_accepted_execution_outcome_vocabulary_is_complete()
    {
        string[] expected =
        [
            "Passed", "VerificationFailed", "ReviewBlocked", "ReviewUnusable",
            "ScopeViolation", "SecretGuardBlocked", "RuntimePolicyViolation",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetNames<ExecutionOutcome>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_accepted_task_state_vocabulary_is_complete()
    {
        string[] expected =
        [
            "Draft", "Ready", "Running", "AwaitingAcceptance", "Accepted", "Rejected", "Cancelled",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetNames<TaskState>().Order(StringComparer.Ordinal));
    }
}
