using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// ADR-0014 / errata E2. The review override closed set is domain knowledge in Phase 1;
/// the service that consumes it arrives in Phase 5. These tests lock the rule now so it
/// cannot drift while the service is written.
/// </summary>
public sealed class AcceptanceRuleTests
{
    [Fact]
    public void Only_ReviewBlocked_is_eligible_for_a_review_override()
    {
        foreach (var outcome in Enum.GetValues<ExecutionOutcome>())
        {
            var expected = outcome == ExecutionOutcome.ReviewBlocked;
            Assert.Equal(expected, AcceptanceRules.IsReviewOverrideEligible(outcome));
        }
    }

    [Theory]
    [InlineData(ExecutionOutcome.VerificationFailed)]
    [InlineData(ExecutionOutcome.ScopeViolation)]
    [InlineData(ExecutionOutcome.SecretGuardBlocked)]
    [InlineData(ExecutionOutcome.RuntimePolicyViolation)]
    [InlineData(ExecutionOutcome.ReviewUnusable)]
    [InlineData(ExecutionOutcome.Passed)]
    public void Machine_truth_and_runtime_integrity_failures_are_never_overridable(ExecutionOutcome outcome)
    {
        Assert.False(AcceptanceRules.IsReviewOverrideEligible(outcome));
    }

    [Fact]
    public void Only_Passed_is_eligible_for_plain_acceptance()
    {
        foreach (var outcome in Enum.GetValues<ExecutionOutcome>())
        {
            Assert.Equal(outcome == ExecutionOutcome.Passed, AcceptanceRules.IsPlainAcceptanceEligible(outcome));
        }
    }

    [Fact]
    public void An_override_decision_requires_a_reason()
    {
        var error = Assert.Throws<DomainException>(() => new AcceptanceDecision(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AcceptanceDecisionKind.Accept,
            "cafer",
            reason: null,
            overrideUsed: true,
            [],
            DomainFixtures.Now));

        Assert.Contains("reason", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_plain_decision_does_not_require_a_reason()
    {
        var decision = new AcceptanceDecision(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AcceptanceDecisionKind.Accept,
            "cafer",
            reason: null,
            overrideUsed: false,
            [new HumanCriterionVerdictRecord(Guid.CreateVersion7(), HumanCriterionVerdict.Met, null)],
            DomainFixtures.Now);

        Assert.False(decision.OverrideUsed);
        Assert.Single(decision.HumanCriterionVerdicts);
    }

    [Fact]
    public void AcceptanceDecision_exposes_no_mutators()
    {
        // ADR-0014: a decision is written once. It also carries no execution outcome,
        // because acceptance must never rewrite one.
        var settable = typeof(AcceptanceDecision)
            .GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);

        var methods = typeof(AcceptanceDecision)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(methods);
    }
}

/// <summary>Architecture P.1 transition table.</summary>
public sealed class TaskStateRuleTests
{
    [Theory]
    [InlineData(TaskState.Draft, TaskState.Ready, true)]
    [InlineData(TaskState.Ready, TaskState.Running, true)]
    [InlineData(TaskState.Running, TaskState.AwaitingAcceptance, true)]
    [InlineData(TaskState.AwaitingAcceptance, TaskState.Accepted, true)]
    [InlineData(TaskState.Ready, TaskState.Accepted, true)]
    [InlineData(TaskState.Draft, TaskState.Accepted, false)]
    [InlineData(TaskState.Draft, TaskState.AwaitingAcceptance, false)]
    [InlineData(TaskState.Accepted, TaskState.Ready, false)]
    [InlineData(TaskState.Rejected, TaskState.Ready, false)]
    [InlineData(TaskState.Cancelled, TaskState.Ready, false)]
    public void Transition_table_matches_the_architecture(TaskState from, TaskState to, bool expected)
    {
        Assert.Equal(expected, TaskStateRules.CanTransition(from, to));
    }

    [Fact]
    public void Ready_to_accepted_is_the_only_edge_that_requires_a_review_override()
    {
        foreach (var (from, to) in TaskStateRules.AllEdges)
        {
            var expected = from == TaskState.Ready && to == TaskState.Accepted;
            Assert.Equal(expected, TaskStateRules.RequiresReviewOverride(from, to));
        }
    }

    [Fact]
    public void Terminal_states_have_no_outgoing_edges()
    {
        foreach (var (from, _) in TaskStateRules.AllEdges)
        {
            Assert.False(TaskStateRules.IsTerminal(from));
        }
    }

    [Fact]
    public void A_decision_is_a_persons_unless_it_says_otherwise()
    {
        var decision = Decision(AcceptanceDecisionKind.Accept, overrideUsed: false, reason: null, verdicts: []);

        Assert.Equal(AcceptanceDecisionSource.Person, decision.Source);
    }

    [Fact]
    public void A_policy_may_only_say_yes_to_what_the_evidence_said()
    {
        // ADR-0028: the narrowest decision there is.
        var accepted = Decision(
            AcceptanceDecisionKind.Accept, overrideUsed: false, "autopilot", [], AcceptanceDecisionSource.Policy);

        Assert.Equal(AcceptanceDecisionSource.Policy, accepted.Source);

        foreach (var kind in new[] { AcceptanceDecisionKind.Reject, AcceptanceDecisionKind.RequestChanges })
        {
            Assert.Throws<DomainException>(() =>
                Decision(kind, overrideUsed: false, "autopilot", [], AcceptanceDecisionSource.Policy));
        }

        Assert.Throws<DomainException>(() =>
            Decision(AcceptanceDecisionKind.Accept, overrideUsed: true, "autopilot", [], AcceptanceDecisionSource.Policy));

        Assert.Throws<DomainException>(() => Decision(
            AcceptanceDecisionKind.Accept,
            overrideUsed: false,
            "autopilot",
            [new HumanCriterionVerdictRecord(Guid.CreateVersion7(), HumanCriterionVerdict.Met, null)],
            AcceptanceDecisionSource.Policy));
    }

    [Fact]
    public void An_unknown_decision_source_is_refused()
    {
        Assert.Throws<DomainException>(() => Decision(
            AcceptanceDecisionKind.Accept, overrideUsed: false, null, [], (AcceptanceDecisionSource)7));
    }

    private static AcceptanceDecision Decision(
        AcceptanceDecisionKind kind,
        bool overrideUsed,
        string? reason,
        HumanCriterionVerdictRecord[] verdicts,
        AcceptanceDecisionSource source = AcceptanceDecisionSource.Person) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            kind,
            "hub:autopilot",
            reason,
            overrideUsed,
            verdicts,
            DomainFixtures.Now,
            source);

    [Fact]
    public void There_is_no_failed_task_state()
    {
        Assert.DoesNotContain(
            Enum.GetNames<TaskState>(),
            n => n.Contains("Fail", StringComparison.OrdinalIgnoreCase));
    }
}
