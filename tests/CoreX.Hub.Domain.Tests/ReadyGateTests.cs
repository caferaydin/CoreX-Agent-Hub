using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// The Draft -&gt; Ready admission checklist (architecture B.1, ADR-0015).
/// The rejected v1 rule "every criterion must be machine verifiable" must NOT reappear:
/// a human-only task is accepted here.
/// </summary>
public sealed class ReadyGateTests
{
    [Fact]
    public void Valid_machine_criterion_is_accepted()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "test"));

        var result = scenario.Evaluate();

        Assert.True(result.IsSatisfied);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Valid_human_criterion_is_accepted_and_needs_no_verification_command()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.HumanCriterion(scenario.Task, 1));

        var result = scenario.Evaluate();

        Assert.True(result.IsSatisfied);
        Assert.Contains(result.Warnings, w => w.Code == ReadyGateWarningCode.NoMachineCriterion);
    }

    [Fact]
    public void Mixed_machine_and_human_criteria_are_accepted()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "build"));
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 2, "test"));
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.HumanCriterion(scenario.Task, 3));

        var result = scenario.Evaluate();

        Assert.True(result.IsSatisfied);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Zero_acceptance_criteria_is_rejected()
    {
        AssertViolation(Scenario.Build().Evaluate(), ReadyGateViolationCode.NoAcceptanceCriteria);
    }

    [Fact]
    public void Criterion_without_a_verification_kind_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.UndeclaredCriterion(scenario.Task, 1));

        AssertViolation(scenario.Evaluate(), ReadyGateViolationCode.CriterionVerificationKindMissing);
    }

    [Fact]
    public void Machine_criterion_without_a_verification_reference_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, verificationRef: null));

        AssertViolation(scenario.Evaluate(), ReadyGateViolationCode.MachineCriterionMissingVerificationRef);
    }

    [Fact]
    public void Machine_criterion_referencing_an_unknown_step_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "does-not-exist"));

        var result = scenario.Evaluate();

        AssertViolation(result, ReadyGateViolationCode.MachineCriterionVerificationRefUnresolved);
        Assert.Contains(result.Violations, v => v.Message.Contains("build", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_repository_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "test"));

        var result = ReadyGate.Evaluate(
            scenario.Task, scenario.Organization, scenario.Project, repository: null, scenario.Profile);

        AssertViolation(result, ReadyGateViolationCode.RepositoryNotFound);
    }

    [Fact]
    public void Missing_project_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "test"));

        var result = ReadyGate.Evaluate(
            scenario.Task, scenario.Organization, project: null, scenario.Repository, scenario.Profile);

        AssertViolation(result, ReadyGateViolationCode.ProjectNotFound);
    }

    [Fact]
    public void Missing_organization_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "test"));

        var result = ReadyGate.Evaluate(
            scenario.Task, organization: null, scenario.Project, scenario.Repository, scenario.Profile);

        AssertViolation(result, ReadyGateViolationCode.OrganizationNotFound);
    }

    [Fact]
    public void Repository_without_a_verification_profile_is_rejected()
    {
        var scenario = Scenario.Build();
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.HumanCriterion(scenario.Task, 1));

        var result = ReadyGate.Evaluate(
            scenario.Task, scenario.Organization, scenario.Project, scenario.Repository, verificationProfile: null);

        AssertViolation(result, ReadyGateViolationCode.VerificationProfileMissing);
    }

    [Fact]
    public void Blocked_organization_is_rejected()
    {
        var scenario = Scenario.Build(requiresHardIsolation: true);
        scenario.Task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(scenario.Task, 1, "test"));

        AssertViolation(scenario.Evaluate(), ReadyGateViolationCode.OrganizationBlocked);
    }

    [Fact]
    public void Missing_allowed_scope_is_rejected()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var repository = DomainFixtures.Repository(organization, project);
        var profile = DomainFixtures.VerificationProfile(organization, repository);
        var task = DomainFixtures.Task(organization, project, repository, allowedScope: []);
        task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(task, 1, "test"));

        AssertViolation(
            ReadyGate.Evaluate(task, organization, project, repository, profile),
            ReadyGateViolationCode.AllowedScopeMissing);
    }

    [Fact]
    public void Scope_declared_as_both_allowed_and_forbidden_is_rejected()
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var repository = DomainFixtures.Repository(organization, project);
        var profile = DomainFixtures.VerificationProfile(organization, repository);
        var task = DomainFixtures.Task(
            organization, project, repository, allowedScope: ["src/**"], forbiddenScope: ["src/**"]);
        task.AddAcceptanceCriterion(DomainFixtures.MachineCriterion(task, 1, "test"));

        AssertViolation(
            ReadyGate.Evaluate(task, organization, project, repository, profile),
            ReadyGateViolationCode.ScopeEntryConflict);
    }

    private static void AssertViolation(ReadyGateResult result, ReadyGateViolationCode expected)
    {
        Assert.False(result.IsSatisfied);
        Assert.Contains(result.Violations, v => v.Code == expected);
    }

    private sealed record Scenario(
        Organization Organization,
        Project Project,
        Domain.Catalog.Repository Repository,
        VerificationProfile Profile,
        HubTask Task)
    {
        public static Scenario Build(bool requiresHardIsolation = false)
        {
            var organization = DomainFixtures.Organization(
                requiresHardIsolation ? "isolation-gate-fixture" : "hub-dev",
                IsolationLevel.Logical,
                requiresHardIsolation);

            var project = DomainFixtures.Project(organization);
            var repository = DomainFixtures.Repository(organization, project);
            var profile = DomainFixtures.VerificationProfile(organization, repository, "build", "test");
            var task = DomainFixtures.Task(organization, project, repository);

            return new Scenario(organization, project, repository, profile, task);
        }

        public ReadyGateResult Evaluate() =>
            ReadyGate.Evaluate(Task, Organization, Project, Repository, Profile);
    }
}
