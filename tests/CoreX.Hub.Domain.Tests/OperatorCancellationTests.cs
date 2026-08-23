using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// Abandoning a task, which is a decision about the work rather than about an attempt.
///
/// An execution ending Cancelled says one attempt was stopped, and the task normally becomes
/// available again so somebody can try once more. This says the task itself is finished with,
/// and it is terminal. Keeping the two apart is why the edge has its own name.
/// </summary>
public sealed class OperatorCancellationTests
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

    [Fact]
    public void A_ready_task_can_be_abandoned_and_the_reason_is_kept()
    {
        var task = ReadyTask();

        task.CancelByOperator("the requirement went away", DomainFixtures.Now);

        Assert.Equal(TaskState.Cancelled, task.State);
        Assert.Equal("the requirement went away", task.BlockReason);
    }

    [Fact]
    public void Work_in_flight_can_be_abandoned_and_so_can_work_waiting_for_a_person()
    {
        var running = ReadyTask();
        running.MarkRunning(DomainFixtures.Now);
        running.CancelByOperator(null, DomainFixtures.Now);

        Assert.Equal(TaskState.Cancelled, running.State);

        var awaiting = ReadyTask();
        awaiting.MarkRunning(DomainFixtures.Now);
        awaiting.MarkAwaitingAcceptance(DomainFixtures.Now);
        awaiting.CancelByOperator(null, DomainFixtures.Now);

        Assert.Equal(TaskState.Cancelled, awaiting.State);
    }

    [Fact]
    public void A_task_that_is_already_finished_with_cannot_be_abandoned_again()
    {
        var accepted = ReadyTask();
        accepted.MarkRunning(DomainFixtures.Now);
        accepted.MarkAwaitingAcceptance(DomainFixtures.Now);
        accepted.Accept(DomainFixtures.Now);

        var refused = Assert.Throws<DomainException>(() => accepted.CancelByOperator(null, DomainFixtures.Now));
        Assert.Contains("already Accepted", refused.Message, StringComparison.Ordinal);

        var cancelled = ReadyTask();
        cancelled.CancelByOperator(null, DomainFixtures.Now);

        Assert.Throws<DomainException>(() => cancelled.CancelByOperator(null, DomainFixtures.Now));
        Assert.Equal(TaskState.Cancelled, cancelled.State);
    }

    [Fact]
    public void Abandoning_a_task_is_not_a_route_into_accepted_or_rejected()
    {
        var task = ReadyTask();
        task.CancelByOperator(null, DomainFixtures.Now);

        Assert.Throws<DomainException>(() => task.Accept(DomainFixtures.Now));
        Assert.Throws<DomainException>(() => task.AcceptByReviewOverride(DomainFixtures.Now));
        Assert.Throws<DomainException>(() => task.Reject(null, DomainFixtures.Now));
        Assert.Throws<DomainException>(() => task.ReturnToReady(null, DomainFixtures.Now));

        Assert.Equal(TaskState.Cancelled, task.State);
    }
}
