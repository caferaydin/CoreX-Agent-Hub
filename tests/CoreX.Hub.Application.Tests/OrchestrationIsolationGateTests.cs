using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The gate where it actually matters: the one admission point every execution goes through.
///
/// The rest of the isolation tests are about the rule. These are about the wiring - that the
/// rule is consulted before a row exists, that a Hub which was never given an authority refuses
/// rather than proceeds, and that a logical organization is completely unaffected by any of it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationIsolationGateTests
{
    /// <summary>An authority that answers the same way every time, so the test is about the wiring.</summary>
    private sealed class FixedAdmission : IIsolationAdmission
    {
        public required IsolationAdmissionDecision Answer { get; init; }

        public List<string> Asked { get; } = [];

        public Task<IsolationAdmissionDecision> EvaluateAsync(Organization organization, CancellationToken ct)
        {
            Asked.Add(organization.Slug.Value);
            return Task.FromResult(Answer);
        }
    }

    /// <summary>
    /// The absence of an authority is not permission.
    ///
    /// A Hub built without an isolation service is not a Hub where hard organizations run
    /// freely. It is one that cannot answer the question, and an unanswerable question about a
    /// boundary is a refusal.
    /// </summary>
    [Fact]
    public async Task An_unwired_hub_refuses_a_hard_organization()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        await MakeHardAsync(hub);

        var refusal = await Assert.ThrowsAsync<ExecutionStartException>(
            () => hub.Orchestrator().RunAsync(
                hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        Assert.Contains(IsolationRefusal.NotConfigured, refusal.Message);
        Assert.Empty(await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_names_its_reason_and_writes_no_execution()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        await MakeHardAsync(hub);

        var admission = new FixedAdmission
        {
            Answer = IsolationAdmissionDecision.Refused(
                IsolationRefusal.NotReady, "the distribution does not exist."),
        };

        hub.Isolation = admission;

        var refusal = await Assert.ThrowsAsync<ExecutionStartException>(
            () => hub.Orchestrator().RunAsync(
                hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        Assert.Contains(IsolationRefusal.NotReady, refusal.Message);
        Assert.Contains("the distribution does not exist.", refusal.Message);
        Assert.Equal(["hub-dev"], admission.Asked);
        Assert.Empty(await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));
    }

    /// <summary>
    /// The gate is not a blanket refusal of hard organizations.
    ///
    /// Worth proving separately, because a gate that always says no would pass every other test
    /// here and would be indistinguishable from one that works.
    /// </summary>
    [Fact]
    public async Task An_admitted_hard_organization_runs()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        await MakeHardAsync(hub);

        hub.Isolation = new FixedAdmission { Answer = IsolationAdmissionDecision.Allowed };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
    }

    /// <summary>
    /// A logical organization never reaches the authority, wired or not.
    ///
    /// This is what keeps the whole slice additive: every organization that exists today is
    /// logical, and none of them is asked a new question.
    /// </summary>
    [Fact]
    public async Task A_logical_organization_is_never_asked()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var admission = new FixedAdmission
        {
            Answer = IsolationAdmissionDecision.Refused(IsolationRefusal.NotReady, "would refuse."),
        };

        hub.Isolation = admission;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
        Assert.Empty(admission.Asked);
    }

    /// <summary>
    /// Turns the harness's organization into one that declares a hard boundary.
    ///
    /// Nothing else about it changes, which is exactly the situation being tested: a line in a
    /// configuration file is the entire difference between an organization that runs and one
    /// that must not.
    /// </summary>
    private static async Task MakeHardAsync(OrchestrationHarness hub)
    {
        var task = await hub.TaskAsync();

        var organization = await hub.Store.FindOrganizationAsync(task.OrganizationId, CancellationToken.None)
            ?? throw new InvalidOperationException("the harness organization is missing.");

        organization.Update(
            organization.Name,
            organization.FilesystemRoot,
            IsolationLevel.Hard,
            requiresHardIsolation: true,
            organization.EnvironmentAllowlist,
            hub.Clock.GetUtcNow());

        await hub.Store.SaveChangesAsync(CancellationToken.None);

        // The catalogue is satisfied: this is an Active organization by every rule that existed
        // before this phase, which is why the refusal has to come from somewhere else.
        Assert.True(organization.CanStartExecution);
    }
}
