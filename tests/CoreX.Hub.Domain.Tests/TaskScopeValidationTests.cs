using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// A malformed scope declaration must not survive long enough for a gate to discover,
/// after an agent has already run, that the task's own policy was unreadable.
/// </summary>
public sealed class TaskScopeValidationTests
{
    private static HubTask Task(IEnumerable<string> allowedScope, IEnumerable<string>? forbiddenScope = null)
    {
        var organization = DomainFixtures.Organization();
        var project = DomainFixtures.Project(organization);
        var repository = DomainFixtures.Repository(organization, project);

        return DomainFixtures.Task(
            organization, project, repository, allowedScope: allowedScope, forbiddenScope: forbiddenScope);
    }

    [Theory]
    [InlineData("../outside/**")]
    [InlineData("/etc/**")]
    [InlineData(@"C:\work\**")]
    [InlineData(@"\server\share\**")]
    public void A_malformed_allowed_scope_cannot_create_a_task(string glob)
    {
        var error = Assert.Throws<DomainException>(() => Task([glob]));

        Assert.Contains("Scope glob", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside/**")]
    [InlineData("/etc/**")]
    public void A_malformed_forbidden_scope_cannot_create_a_task(string glob)
    {
        Assert.Throws<DomainException>(() =>
            Task(["src/**"], [glob]));
    }

    [Fact]
    public void A_malformed_scope_therefore_cannot_reach_ready()
    {
        // Stronger than a ready gate rule: such a task cannot be constructed at all, so
        // there is no state in which one exists and is waiting to be gated.
        Assert.Throws<DomainException>(() => Task(["../escape/**"]));
    }

    [Fact]
    public void A_valid_scope_is_stored_in_its_canonical_form()
    {
        var task = Task([@"src\orders\**", "./docs/"]);

        Assert.Equal(["src/orders/**", "docs/"], task.AllowedScope);
    }

    [Fact]
    public void Two_spellings_of_one_scope_collapse_to_a_single_entry()
    {
        var task = Task(["src/**", @"src\**", "./src/**"]);

        Assert.Equal(["src/**"], task.AllowedScope);
    }

    [Fact]
    public void The_stored_representation_is_still_plain_text()
    {
        // The aggregate keeps strings, so nothing about persistence changed; ScopeGlob
        // owns what those strings mean.
        var property = typeof(HubTask).GetProperty(nameof(HubTask.AllowedScope))!;

        Assert.Equal(typeof(IReadOnlyList<string>), property.PropertyType);
    }
}
