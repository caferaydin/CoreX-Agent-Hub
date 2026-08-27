using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The gate itself: which organizations may start an execution, and why not.
///
/// Every refusal here is fail closed, and the tests are written to make the difference between
/// "refused" and "allowed" the only thing they measure. The order of the reasons matters for
/// the operator reading them - a distribution that does not exist should be reported as that,
/// not as a complaint about a neighbour - so the sequence is asserted too.
/// </summary>
public sealed class HardIsolationAdmissionTests
{
    private static HardIsolationAdmissionService Service(
        TempCatalog catalog,
        FakeReadinessProbe probe,
        FakeProfileSource profiles,
        bool planeAvailable = false) =>
        new(catalog.Store, profiles, probe, new FakeExecutionPlane { IsAvailable = planeAvailable });

    /// <summary>A logical organization is governed by the rules it already had.</summary>
    [Fact]
    public async Task A_logical_organization_is_not_asked_about_at_all()
    {
        await using var catalog = await TempCatalog.CreateAsync(("hub-dev", IsolationLevel.Logical, false));

        var probe = new FakeReadinessProbe();
        var service = Service(catalog, probe, new FakeProfileSource());

        var decision = await service.EvaluateAsync(await catalog.FindAsync("hub-dev"), CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Empty(probe.Asked);
    }

    /// <summary>
    /// The property the whole slice exists for.
    ///
    /// Writing <c>isolationLevel: Hard</c> into a configuration file produces an organization
    /// the domain calls Active - that has always been true and is unchanged here. What it must
    /// never produce is one that can start an execution.
    /// </summary>
    [Fact]
    public async Task Declaring_hard_isolation_does_not_by_itself_make_an_organization_executable()
    {
        await using var catalog = await TempCatalog.CreateAsync(("kronos", IsolationLevel.Hard, true));

        var organization = await catalog.FindAsync("kronos");

        // The catalogue is perfectly happy with it.
        Assert.True(organization.CanStartExecution);
        Assert.Null(organization.BlockReason);

        var service = Service(catalog, new FakeReadinessProbe(), new FakeProfileSource("kronos"));
        var decision = await service.EvaluateAsync(organization, CancellationToken.None);

        // And it still may not run, because nothing established the environment.
        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.NotReady, decision.ReasonCode);
    }

    [Fact]
    public async Task A_hard_organization_with_no_configuration_is_refused()
    {
        await using var catalog = await TempCatalog.CreateAsync(("kronos", IsolationLevel.Hard, true));

        var probe = new FakeReadinessProbe().Ready("kronos");
        var service = Service(catalog, probe, new FakeProfileSource());

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.NotConfigured, decision.ReasonCode);

        // Nothing was probed: there was nothing to probe.
        Assert.Empty(probe.Asked);
    }

    [Fact]
    public async Task A_hard_organization_whose_machine_is_not_ready_is_refused()
    {
        await using var catalog = await TempCatalog.CreateAsync(("kronos", IsolationLevel.Hard, true));

        var probe = new FakeReadinessProbe().NotReady("kronos", HardIsolationChecks.HostInteropDisabled);
        var service = Service(catalog, probe, new FakeProfileSource("kronos"));

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.NotReady, decision.ReasonCode);
        Assert.Contains(HardIsolationChecks.HostInteropDisabled, decision.Detail);
    }

    [Fact]
    public async Task A_ready_environment_beside_a_logical_neighbour_is_refused()
    {
        await using var catalog = await TempCatalog.CreateAsync(
            ("kronos", IsolationLevel.Hard, true),
            ("core-x-logic", IsolationLevel.Logical, false));

        var probe = new FakeReadinessProbe().Ready("kronos");
        var service = Service(catalog, probe, new FakeProfileSource("kronos"), planeAvailable: true);

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.TopologyNotReady, decision.ReasonCode);
        Assert.Contains("core-x-logic", decision.Detail);
    }

    /// <summary>
    /// The other half of the same arrangement: disabling the neighbour's execution unblocks it.
    ///
    /// Worth asserting beside the refusal above, because a rule that only ever says no would
    /// look identical in every test that expects no. The organization here declares hard
    /// isolation it does not have, which is the state that blocks it in the catalogue - and a
    /// neighbour that cannot run repository controlled code is not a threat to a boundary,
    /// whatever it declares.
    /// </summary>
    [Fact]
    public async Task A_neighbour_that_was_disabled_stops_being_a_topology_problem()
    {
        await using var catalog = await TempCatalog.CreateAsync(
            ("kronos", IsolationLevel.Hard, true),
            ("core-x-logic", IsolationLevel.Logical, true));

        // The catalogue has already stopped the neighbour: it asks for a boundary it does not
        // have, which is the ADR-0017 gate and predates this phase entirely.
        Assert.False((await catalog.FindAsync("core-x-logic")).CanStartExecution);

        var probe = new FakeReadinessProbe().Ready("kronos");
        var service = Service(catalog, probe, new FakeProfileSource("kronos"), planeAvailable: true);

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    /// <summary>
    /// A neighbour is measured, not believed.
    ///
    /// It declares hard isolation, the catalogue is satisfied, and its environment was never
    /// established - so the subject's boundary is still exposed.
    /// </summary>
    [Fact]
    public async Task A_neighbour_that_only_declares_hard_isolation_is_refused()
    {
        await using var catalog = await TempCatalog.CreateAsync(
            ("kronos", IsolationLevel.Hard, true),
            ("core-x-logic", IsolationLevel.Hard, true));

        var probe = new FakeReadinessProbe()
            .Ready("kronos")
            .NotReady("core-x-logic", HardIsolationChecks.EnvironmentExists);

        var service = Service(
            catalog, probe, new FakeProfileSource("kronos", "core-x-logic"), planeAvailable: true);

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.TopologyNotReady, decision.ReasonCode);
        Assert.Contains("core-x-logic", decision.Detail);
        Assert.Contains("core-x-logic", probe.Asked);
    }

    [Fact]
    public async Task An_all_hard_ready_machine_reaches_the_execution_plane_question()
    {
        await using var catalog = await TempCatalog.CreateAsync(
            ("kronos", IsolationLevel.Hard, true),
            ("core-x-logic", IsolationLevel.Hard, true));

        var probe = new FakeReadinessProbe().Ready("kronos").Ready("core-x-logic");
        var service = Service(catalog, probe, new FakeProfileSource("kronos", "core-x-logic"));

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.ExecutionPlaneUnavailable, decision.ReasonCode);
    }

    /// <summary>
    /// What this build does with a machine that is entirely correct.
    ///
    /// It refuses, and that is the intended answer rather than a gap: Phase 10C-1 can measure a
    /// hard environment and cannot run a task inside one. The only alternative would be to run
    /// the work on the host, which is precisely what the organization asked not to happen.
    /// </summary>
    [Fact]
    public async Task A_perfect_machine_is_still_refused_while_there_is_no_execution_plane()
    {
        await using var catalog = await TempCatalog.CreateAsync(("kronos", IsolationLevel.Hard, true));

        var service = new HardIsolationAdmissionService(
            catalog.Store,
            new FakeProfileSource("kronos"),
            new FakeReadinessProbe().Ready("kronos"),
            new UnavailableHardExecutionPlane());

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(IsolationRefusal.ExecutionPlaneUnavailable, decision.ReasonCode);
        Assert.DoesNotContain("Windows", decision.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_available_execution_plane_admits_a_ready_and_well_arranged_machine()
    {
        await using var catalog = await TempCatalog.CreateAsync(("kronos", IsolationLevel.Hard, true));

        var service = Service(
            catalog, new FakeReadinessProbe().Ready("kronos"), new FakeProfileSource("kronos"),
            planeAvailable: true);

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    /// <summary>
    /// The reason an operator is given is the first thing that is actually wrong.
    ///
    /// A machine with no distribution and a logical neighbour has two problems; being told
    /// about the neighbour first would send the operator to the wrong place.
    /// </summary>
    [Fact]
    public async Task Its_own_machine_is_reported_before_the_neighbourhood()
    {
        await using var catalog = await TempCatalog.CreateAsync(
            ("kronos", IsolationLevel.Hard, true),
            ("core-x-logic", IsolationLevel.Logical, false));

        var probe = new FakeReadinessProbe().NotReady("kronos", HardIsolationChecks.EnvironmentExists);
        var service = Service(catalog, probe, new FakeProfileSource("kronos"));

        var decision = await service.EvaluateAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.Equal(IsolationRefusal.NotReady, decision.ReasonCode);
        Assert.DoesNotContain("core-x-logic", decision.Detail);
    }

    /// <summary>The diagnostic and the gate are the same computation, not two that agree today.</summary>
    [Fact]
    public async Task The_assessment_carries_what_the_decision_was_made_from()
    {
        await using var catalog = await TempCatalog.CreateAsync(("kronos", IsolationLevel.Hard, true));

        var probe = new FakeReadinessProbe().NotReady("kronos", HardIsolationChecks.SystemdAvailable);
        var service = Service(catalog, probe, new FakeProfileSource("kronos"));

        var assessment = await service.AssessAsync(await catalog.FindAsync("kronos"), CancellationToken.None);

        Assert.Equal("kronos", assessment.OrganizationSlug);
        Assert.Equal(HardIsolationChecks.Required.Count, assessment.Readiness.Checks.Count);
        Assert.Equal([HardIsolationChecks.SystemdAvailable], assessment.Readiness.UnmetCheckIds);
        Assert.False(assessment.Decision.IsAllowed);
    }
}
