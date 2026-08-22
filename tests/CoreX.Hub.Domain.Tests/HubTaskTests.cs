using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

public sealed class HubTaskTests
{
    [Fact]
    public void New_task_starts_in_draft_with_version_one()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var repository = DomainFixtures.Repository(organization, project);

        var task = DomainFixtures.Task(organization, project, repository);

        Assert.Equal(TaskState.Draft, task.State);
        Assert.Equal(1, task.Version);
        Assert.Equal("HUB-001", task.TaskKey.Value);
        Assert.Empty(task.AcceptanceCriteria);
        Assert.Null(task.BlockReason);
    }

    [Fact]
    public void Task_key_prefix_must_match_the_project()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization, prefix: "HUB");
        var repository = DomainFixtures.Repository(organization, project);

        var error = Assert.Throws<DomainException>(
            () => DomainFixtures.Task(organization, project, repository, key: "GATE-001"));

        Assert.Contains("prefix", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_must_belong_to_the_project()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var otherProject = DomainFixtures.Project(organization, slug: "other-project", prefix: "OTH");
        var foreignRepository = DomainFixtures.Repository(organization, otherProject, slug: "other-repo");

        Assert.Throws<DomainException>(
            () => DomainFixtures.Task(organization, project, foreignRepository));
    }

    [Fact]
    public void Machine_criterion_keeps_its_verification_reference()
    {
        var task = BuildTask();
        var criterion = DomainFixtures.MachineCriterion(task, 1, "test");

        Assert.Equal(VerificationKind.Machine, criterion.Kind);
        Assert.Equal("test", criterion.VerificationRef);
        Assert.Null(criterion.HumanCheckHint);
    }

    [Fact]
    public void Machine_criterion_may_be_created_without_a_reference_and_is_caught_by_the_gate()
    {
        // The type allows it so that a draft can be incomplete; ADR-0015 puts the
        // rejection in the ready gate, not in the constructor.
        var task = BuildTask();
        var criterion = DomainFixtures.MachineCriterion(task, 1, verificationRef: null);

        Assert.Equal(VerificationKind.Machine, criterion.Kind);
        Assert.Null(criterion.VerificationRef);
    }

    [Fact]
    public void Human_criterion_needs_no_verification_reference()
    {
        var task = BuildTask();
        var criterion = DomainFixtures.HumanCriterion(task, 1);

        Assert.Equal(VerificationKind.Human, criterion.Kind);
        Assert.Null(criterion.VerificationRef);
        Assert.Equal("look at it", criterion.HumanCheckHint);
    }

    [Fact]
    public void Undeclared_criterion_has_no_kind()
    {
        var task = BuildTask();
        var criterion = DomainFixtures.UndeclaredCriterion(task, 1);

        Assert.Null(criterion.Kind);
    }

    [Fact]
    public void Criterion_order_must_be_unique()
    {
        var task = BuildTask();
        task.AddAcceptanceCriterion(DomainFixtures.HumanCriterion(task, 1));

        Assert.Throws<DomainException>(
            () => task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(task, 1, "test")));
    }

    [Fact]
    public void MarkReady_refuses_an_unsatisfied_gate()
    {
        var task = BuildTask();
        var unsatisfied = new ReadyGateResult(
            [new ReadyGateViolation(ReadyGateViolationCode.NoAcceptanceCriteria, "none")],
            []);

        Assert.Throws<DomainException>(() => task.MarkReady(unsatisfied, DomainFixtures.Now));
        Assert.Equal(TaskState.Draft, task.State);
    }

    [Fact]
    public void MarkReady_moves_draft_to_ready_and_bumps_the_concurrency_version()
    {
        var task = BuildTask();
        var before = task.Version;

        task.MarkReady(ReadyGateResult.Satisfied, DomainFixtures.Now.AddMinutes(5));

        Assert.Equal(TaskState.Ready, task.State);
        Assert.Equal(before + 1, task.Version);
        Assert.Equal(DomainFixtures.Now.AddMinutes(5), task.UpdatedAtUtc);
    }

    private static HubTask BuildTask()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var repository = DomainFixtures.Repository(organization, project);
        return DomainFixtures.Task(organization, project, repository);
    }
}
