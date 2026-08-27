using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The co-tenancy rule.
///
/// Phase 10A established mechanically that a WSL environment registered under a Windows user
/// is not a boundary against untrusted code running as that same Windows user - that code can
/// simply ask for root inside it. So an environment can pass every check about itself and still
/// be exposed by a neighbour, and per organization readiness cannot see that at all.
///
/// The cases below are the arrangements worth being sure about, including the two that make the
/// rule usable rather than merely strict: a logical neighbour that cannot execute is harmless,
/// and the subject is never judged against itself.
/// </summary>
public sealed class IsolationTopologyTests
{
    private static IsolationParticipant Hard(string slug, bool ready = true, bool canExecute = true) =>
        new(slug, DeclaredHard: true, CanStartExecution: canExecute, MachineReady: ready);

    private static IsolationParticipant Logical(string slug, bool canExecute = true) =>
        new(slug, DeclaredHard: false, CanStartExecution: canExecute, MachineReady: false);

    /// <summary>A: the only organization on the machine.</summary>
    [Fact]
    public void A_lone_hard_organization_is_safe()
    {
        var topology = IsolationTopologyReadiness.For("kronos", [Hard("kronos")]);

        Assert.True(topology.IsReady);
        Assert.Empty(topology.Problems);
    }

    /// <summary>B: every organization is hard and every environment is established.</summary>
    [Fact]
    public void An_all_hard_machine_is_safe()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos", [Hard("kronos"), Hard("core-x-logic"), Hard("hub-dev")]);

        Assert.True(topology.IsReady);
    }

    /// <summary>
    /// C: a logical neighbour that can execute.
    ///
    /// The refusal lands on the hard organization, not on the neighbour. A logical organization
    /// keeps working exactly as it did; what it loses is the ability to have a hard tenant
    /// beside it.
    /// </summary>
    [Fact]
    public void A_logical_neighbour_that_can_execute_makes_the_machine_unsafe()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos", [Hard("kronos"), Logical("core-x-logic")]);

        Assert.False(topology.IsReady);
        Assert.Contains("core-x-logic", Assert.Single(topology.Problems));
        Assert.DoesNotContain("kronos", Assert.Single(topology.Problems));
    }

    /// <summary>
    /// D: a neighbour that declares hard isolation and does not have it.
    ///
    /// Exactly as dangerous as one that never declared it, because the code runs under the
    /// shared principal either way - which is why the rule asks the machine about neighbours
    /// rather than reading their configuration.
    /// </summary>
    [Fact]
    public void A_neighbour_that_declares_hard_isolation_it_does_not_have_makes_the_machine_unsafe()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos", [Hard("kronos"), Hard("core-x-logic", ready: false)]);

        Assert.False(topology.IsReady);
        Assert.Contains("is not ready", Assert.Single(topology.Problems));
    }

    /// <summary>
    /// E: a logical neighbour that cannot execute anything.
    ///
    /// Blocked organizations still exist in the catalogue and still hold rows. They are not a
    /// threat to a boundary they cannot run code against, and treating them as one would mean
    /// no machine with any history could ever host a hard tenant.
    /// </summary>
    [Fact]
    public void A_neighbour_that_cannot_execute_is_not_a_threat()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos", [Hard("kronos"), Logical("gate-fixture", canExecute: false)]);

        Assert.True(topology.IsReady);
    }

    [Fact]
    public void A_hard_neighbour_that_is_not_ready_but_cannot_execute_is_not_a_threat()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos", [Hard("kronos"), Hard("half-built", ready: false, canExecute: false)]);

        Assert.True(topology.IsReady);
    }

    /// <summary>The subject is never a problem to itself, whatever its own readiness says.</summary>
    [Fact]
    public void The_subject_is_skipped()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos", [Hard("kronos", ready: false)]);

        Assert.True(topology.IsReady);
    }

    [Fact]
    public void Every_unsafe_neighbour_is_named()
    {
        var topology = IsolationTopologyReadiness.For(
            "kronos",
            [Hard("kronos"), Logical("a"), Logical("b"), Hard("c", ready: false)]);

        Assert.False(topology.IsReady);
        Assert.Equal(3, topology.Problems.Count);
        Assert.Contains(topology.Problems, p => p.Contains("'a'", StringComparison.Ordinal));
        Assert.Contains(topology.Problems, p => p.Contains("'b'", StringComparison.Ordinal));
        Assert.Contains(topology.Problems, p => p.Contains("'c'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_machine_is_safe()
    {
        Assert.True(IsolationTopologyReadiness.For("kronos", []).IsReady);
    }
}
