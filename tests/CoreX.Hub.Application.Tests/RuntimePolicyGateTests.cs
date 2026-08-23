using System.Reflection;
using CoreX.Hub.Application.Gates;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// K1. HEAD is the authority, and the Hub never repairs what it finds there.
/// </summary>
public sealed class RuntimePolicyGateTests
{
    [Fact]
    public void Head_at_the_base_commit_passes()
    {
        var verdict = RuntimePolicyGate.Evaluate(GateFixtures.Changes());

        Assert.Equal(GateKind.RuntimePolicy, verdict.Kind);
        Assert.Equal(GateStatus.Passed, verdict.Status);
        Assert.False(verdict.UnexpectedAgentCommitDetected);
        Assert.Empty(verdict.UnexpectedCommitShas);
    }

    [Fact]
    public void Head_away_from_the_base_commit_is_blocked()
    {
        var verdict = RuntimePolicyGate.Evaluate(
            GateFixtures.Changes(head: GateFixtures.Moved, unexpectedCommits: [GateFixtures.Moved]));

        Assert.True(verdict.Blocked);
        Assert.True(verdict.UnexpectedAgentCommitDetected);
        Assert.Contains(RuntimePolicyGate.UnexpectedAgentCommit, verdict.Reason, StringComparison.Ordinal);
        Assert.Equal([GateFixtures.Moved], verdict.UnexpectedCommitShas);
    }

    [Fact]
    public void The_observed_commit_shas_are_preserved_as_captured()
    {
        string[] observed = ["aaaa111122223333444455556666777788889999", GateFixtures.Moved];

        var verdict = RuntimePolicyGate.Evaluate(
            GateFixtures.Changes(head: GateFixtures.Moved, unexpectedCommits: observed));

        // Reported exactly as the capture produced them, in the same order.
        Assert.Equal(observed, verdict.UnexpectedCommitShas);
    }

    [Fact]
    public void An_empty_commit_graph_cannot_turn_a_head_mismatch_into_a_pass()
    {
        // An unusual topology can leave the base..HEAD range empty. HEAD is still not the
        // base commit, and that alone is the violation: diagnostic richness never softens
        // the primary rule.
        var verdict = RuntimePolicyGate.Evaluate(
            GateFixtures.Changes(head: GateFixtures.Moved, unexpectedCommits: []));

        Assert.True(verdict.Blocked);
        Assert.True(verdict.UnexpectedAgentCommitDetected);

        // And nothing was manufactured to make the list look convincing.
        Assert.Empty(verdict.UnexpectedCommitShas);
    }

    [Fact]
    public void Case_differences_in_a_sha_are_not_a_violation()
    {
        var verdict = RuntimePolicyGate.Evaluate(
            GateFixtures.Changes(head: GateFixtures.Base.ToUpperInvariant()));

        Assert.Equal(GateStatus.Passed, verdict.Status);
    }

    [Fact]
    public void The_gate_offers_no_way_to_repair_history()
    {
        // The gate reports; it never resets, rewrites or moves a ref. There is no method
        // here that could, and the verdict it returns carries no instruction to.
        var members = typeof(RuntimePolicyGate).GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();

        foreach (var repair in new[] { "Reset", "Rewrite", "Amend", "Rebase", "Force", "Update", "Repair", "Delete" })
        {
            Assert.DoesNotContain(members, m => m.Contains(repair, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(["Evaluate", "UnexpectedAgentCommit"], members.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_verdict_carries_facts_rather_than_an_execution_outcome()
    {
        var verdict = RuntimePolicyGate.Evaluate(GateFixtures.Changes(head: GateFixtures.Moved));

        // Phase 5C decides what an execution outcome becomes, including the frozen rule
        // that a terminal agent failure outranks an unexpected commit. The gate must not
        // pre-empt that, so it exposes no outcome at all.
        foreach (var property in typeof(GateVerdict).GetProperties())
        {
            Assert.DoesNotContain("Outcome", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Execution", property.Name, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(verdict.UnexpectedAgentCommitDetected);
    }

    [Fact]
    public void The_gate_knows_nothing_about_which_runtime_produced_the_commit()
    {
        // The same facts from a local harness and from a hosted assistant are the same
        // verdict: there is nowhere in the input to say who ran.
        foreach (var property in typeof(CapturedChanges).GetProperties())
        {
            foreach (var word in new[] { "Provider", "Runtime", "Agent", "Model", "ApiKey", "Account" })
            {
                Assert.DoesNotContain(word, property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
