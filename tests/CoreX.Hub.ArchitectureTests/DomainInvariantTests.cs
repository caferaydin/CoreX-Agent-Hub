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

        Assert.Equal(["AddAcceptanceCriterion", "MarkReady", "SetBlockReason"], mutators.Order(StringComparer.Ordinal));

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
