using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The Hub result commit identity is a deterministic machine identity fixed by the
/// architecture (ADIM 3), not a contact address:
///
///   git -C &lt;wt&gt; -c user.name="CoreX Agent Hub" -c user.email="agent@corex.local" commit
///
/// These assertions deliberately hard code the expected values. A test that compared the
/// commit against GitEnvironment.HubCommitterEmail would pass no matter what that constant
/// became, so production and the test could drift together and a result commit could stop
/// being mechanically attributable to the Hub.
/// </summary>
public sealed class HubCommitIdentityTests
{
    [Fact]
    public void The_hub_committer_identity_is_exactly_what_the_architecture_specifies()
    {
        Assert.Equal("CoreX Agent Hub", GitEnvironment.HubCommitterName);
        Assert.Equal("agent@corex.local", GitEnvironment.HubCommitterEmail);
    }

    [Fact]
    public void The_identity_is_not_a_person_or_an_organisation_mailbox()
    {
        var email = GitEnvironment.HubCommitterEmail;

        // A machine identity, so no real mailbox and no inherited developer identity.
        Assert.DoesNotContain("info@", email, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corexlogic.com", email, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("@corex.local", email, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_managed_git_command_carries_the_identity_in_its_environment()
    {
        var environment = GitEnvironment.WithHubIdentity(
            GitEnvironment.Base(Path.Combine(Path.GetTempPath(), "corex-empty-global")));

        Assert.Equal("CoreX Agent Hub", environment["GIT_AUTHOR_NAME"]);
        Assert.Equal("agent@corex.local", environment["GIT_AUTHOR_EMAIL"]);
        Assert.Equal("CoreX Agent Hub", environment["GIT_COMMITTER_NAME"]);
        Assert.Equal("agent@corex.local", environment["GIT_COMMITTER_EMAIL"]);
    }
}
