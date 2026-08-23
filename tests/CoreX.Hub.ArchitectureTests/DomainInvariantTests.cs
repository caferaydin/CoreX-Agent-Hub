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
    public void The_task_surface_is_closed_and_names_the_human_decisions_explicitly()
    {
        // E.1 #6: only AcceptanceService may write Accepted. Phase 5D gives the aggregate
        // the edges a decision needs and Phase 5E adds the operator's abandon, so this guard
        // changes shape rather than relaxing: the list stays closed, and the human decisions
        // are named in it on purpose - a new mutator, or a second way to reach Accepted, has
        // to be argued for here first.
        var mutators = typeof(HubTask)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(
            [
                "Accept", "AcceptByReviewOverride", "AddAcceptanceCriterion", "CancelByOperator",
                "MarkAwaitingAcceptance", "MarkReady", "MarkRunning", "Reject", "ReturnToReady",
                "SetBlockReason",
            ],
            mutators.Order(StringComparer.Ordinal));

        // Exactly two ways into Accepted, and they are the two the frozen model describes:
        // the ordinary route and the review override. Nothing else may appear.
        Assert.Equal(
            ["Accept", "AcceptByReviewOverride"],
            mutators.Where(m => m.StartsWith("Accept", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));

        Assert.Equal(["Reject"], mutators.Where(m => m.StartsWith("Reject", StringComparison.Ordinal)));
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
    public void No_terminal_error_state_can_ever_carry_a_result_commit()
    {
        // Failed, TimedOut, Cancelled and Interrupted are terminal but not Completed, so
        // they carry no outcome - and an attempt with no outcome produced nothing. The
        // invariant lives in the model rather than in each caller, because a pipeline or a
        // reconciler that reached one of these after a commit existed would otherwise write
        // a row contradicting a repository it does not own.
        var terminalErrorStates = Enum.GetValues<ExecutionState>()
            .Where(s => ExecutionStateRules.IsTerminal(s) && !ExecutionStateRules.IsCompleted(s))
            .ToList();

        Assert.Equal(4, terminalErrorStates.Count);

        foreach (var state in terminalErrorStates)
        {
            var execution = NewExecution();
            execution.RecordResultCommit("9876543210fedcba9876543210fedcba98765432", Now);

            Assert.ThrowsAny<Exception>(() => execution.Terminate(state, Now));
            Assert.False(execution.IsTerminal);
            Assert.NotNull(execution.ResultCommitSha);

            // Still reachable when the attempt really did produce nothing.
            var empty = NewExecution();
            empty.Terminate(state, Now);
            Assert.Equal(state, empty.State);
        }
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
