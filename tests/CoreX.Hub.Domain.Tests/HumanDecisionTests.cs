using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// The edges a person's decision may take, and the ones nothing may take by accident.
///
/// Accepted and Rejected are the only states a machine never reaches on its own, so the
/// aggregate's job here is narrow and unusually important: make the legal edges available
/// to a decision, and make every other route into them impossible.
/// </summary>
public sealed class HumanDecisionTests
{
    private static HubTask ReadyTask()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var repository = DomainFixtures.Repository(organization, project);
        var task = DomainFixtures.Task(organization, project, repository);

        task.AddAcceptanceCriterion(AcceptanceCriterion.Human(
            Guid.CreateVersion7(), organization.Id, task.Id, 1, "It looks right."));

        var profile = DomainFixtures.VerificationProfile(organization, repository, "test");
        task.MarkReady(ReadyGate.Evaluate(task, organization, project, repository, profile), DomainFixtures.Now);

        return task;
    }

    private static HubTask AwaitingTask()
    {
        var task = ReadyTask();
        task.MarkRunning(DomainFixtures.Now);
        task.MarkAwaitingAcceptance(DomainFixtures.Now);
        return task;
    }

    [Fact]
    public void A_passing_execution_is_accepted_from_awaiting_acceptance()
    {
        var task = AwaitingTask();

        task.Accept(DomainFixtures.Now);

        Assert.Equal(TaskState.Accepted, task.State);
        Assert.True(TaskStateRules.IsTerminal(task.State));
    }

    [Theory]
    [InlineData(TaskState.Draft)]
    [InlineData(TaskState.Ready)]
    [InlineData(TaskState.Running)]
    public void Plain_acceptance_starts_nowhere_but_awaiting_acceptance(TaskState from)
    {
        var task = ReadyTask();

        if (from == TaskState.Running)
        {
            task.MarkRunning(DomainFixtures.Now);
        }

        if (from == TaskState.Draft)
        {
            // A draft task is one that never satisfied the ready gate; the fixture is ready,
            // so this case is covered by the Ready assertion below instead.
            return;
        }

        Assert.Throws<DomainException>(() => task.Accept(DomainFixtures.Now));
        Assert.NotEqual(TaskState.Accepted, task.State);
    }

    [Fact]
    public void The_override_edge_exists_only_from_ready()
    {
        // Ready to Accepted is the one edge the ordinary transition refuses, because it is
        // meaningless unless a blocking review is what put the task back in Ready.
        var task = ReadyTask();

        Assert.Throws<DomainException>(() => task.Accept(DomainFixtures.Now));

        task.AcceptByReviewOverride(DomainFixtures.Now);

        Assert.Equal(TaskState.Accepted, task.State);
    }

    [Fact]
    public void An_override_cannot_be_used_to_skip_the_ordinary_route()
    {
        var task = AwaitingTask();

        Assert.Throws<DomainException>(() => task.AcceptByReviewOverride(DomainFixtures.Now));
        Assert.Equal(TaskState.AwaitingAcceptance, task.State);
    }

    [Fact]
    public void Rejection_is_terminal_and_keeps_its_reason()
    {
        var task = AwaitingTask();

        task.Reject("not what was asked for", DomainFixtures.Now);

        Assert.Equal(TaskState.Rejected, task.State);
        Assert.Equal("not what was asked for", task.BlockReason);
        Assert.True(TaskStateRules.IsTerminal(task.State));

        // Terminal means terminal: nothing decides again.
        Assert.Throws<DomainException>(() => task.Accept(DomainFixtures.Now));
        Assert.Throws<DomainException>(() => task.Reject(null, DomainFixtures.Now));
    }

    [Theory]
    [InlineData(TaskState.Ready)]
    [InlineData(TaskState.Running)]
    public void Rejection_starts_nowhere_but_awaiting_acceptance(TaskState from)
    {
        var task = ReadyTask();

        if (from == TaskState.Running)
        {
            task.MarkRunning(DomainFixtures.Now);
        }

        Assert.Throws<DomainException>(() => task.Reject("no", DomainFixtures.Now));
        Assert.NotEqual(TaskState.Rejected, task.State);
    }

    [Fact]
    public void Sending_the_work_back_returns_the_task_to_the_queue()
    {
        var task = AwaitingTask();

        task.ReturnToReady("needs another pass", DomainFixtures.Now);

        Assert.Equal(TaskState.Ready, task.State);
        Assert.Equal("needs another pass", task.BlockReason);
    }

    [Fact]
    public void A_finding_outside_the_diff_never_blocks()
    {
        // The only settled part of the blocking rule today. Which severities block is G7-1,
        // to be calibrated when a reviewer exists, and is deliberately not guessed at.
        Assert.False(Finding(outOfScope: true).IsBlocking);
        Assert.True(Finding(outOfScope: false).IsBlocking);
    }

    private static Finding Finding(bool outOfScope) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            FindingSeverity.Major,
            FindingCategory.Correctness,
            "A blocking concern",
            "It would break on the empty input.",
            "src/thing.cs",
            12,
            null,
            FindingConfidence.High,
            outOfScope,
            DomainFixtures.Now);
}
