using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// Fixture builders. No real company data: everything here is the approved Loop Zero
/// fixture identity (HUB-001) or an explicitly synthetic gate fixture.
/// </summary>
internal static class DomainFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    public static Organization Organization(
        string slug = "hub-dev",
        IsolationLevel isolationLevel = IsolationLevel.Logical,
        bool requiresHardIsolation = false) =>
        new(
            Guid.CreateVersion7(),
            Slug.Parse(slug),
            $"Fixture {slug}",
            $@"D:\cx\_fixtures\{slug}",
            isolationLevel,
            requiresHardIsolation,
            ["PATH"],
            Now);

    public static Project Project(Organization organization, string slug = "hub-sample", string prefix = "HUB") =>
        new(Guid.CreateVersion7(), organization.Id, Slug.Parse(slug), $"Fixture {slug}", prefix, Now);

    public static Catalog.Repository Repository(
        Organization organization,
        Project project,
        string slug = "hub-sample-repo") =>
        new(
            Guid.CreateVersion7(),
            organization.Id,
            project.Id,
            Slug.Parse(slug),
            slug,
            $@"D:\cx\_fixtures\{organization.Slug}\{slug}",
            "main",
            ["main"],
            Now);

    public static VerificationProfile VerificationProfile(
        Organization organization,
        Catalog.Repository repository,
        params string[] stepNames)
    {
        var names = stepNames.Length == 0 ? ["build", "test"] : stepNames;

        return new VerificationProfile(
            Guid.CreateVersion7(),
            organization.Id,
            repository.Id,
            "dotnet-default",
            isDefault: true,
            ".",
            names.Select(n => new VerificationStep(n, "dotnet", [n], 300, true, VerificationResultParser.None)),
            Now);
    }

    public static HubTask Task(
        Organization organization,
        Project project,
        Catalog.Repository repository,
        string key = "HUB-001",
        string objective = "Fixture objective",
        IEnumerable<string>? allowedScope = null,
        IEnumerable<string>? forbiddenScope = null) =>
        new(
            Guid.CreateVersion7(),
            organization,
            project,
            repository,
            TaskKey.Parse(key),
            objective,
            allowedScope ?? ["src/**"],
            forbiddenScope ?? [],
            Now);

    public static AcceptanceCriterion MachineCriterion(HubTask task, int order, string? verificationRef) =>
        AcceptanceCriterion.Machine(
            Guid.CreateVersion7(), task.OrganizationId, task.Id, order, $"machine criterion {order}", verificationRef);

    public static AcceptanceCriterion HumanCriterion(HubTask task, int order) =>
        AcceptanceCriterion.Human(
            Guid.CreateVersion7(), task.OrganizationId, task.Id, order, $"human criterion {order}", "look at it");

    public static AcceptanceCriterion UndeclaredCriterion(HubTask task, int order) =>
        AcceptanceCriterion.Undeclared(
            Guid.CreateVersion7(), task.OrganizationId, task.Id, order, $"undeclared criterion {order}");
}
