using CoreX.Hub.Composition.HardWorkspace;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The layout and the command surface, proven without a machine.
///
/// Both are places where a mistake is invisible at runtime. A path derived from a display name
/// works perfectly until the day the name contains a slash; a git command missing its hardened
/// configuration runs a repository's own hooks and reports success. So the derivation is
/// arithmetic on strings and the commands are built by a pure function, and both are checked
/// here against literals.
/// </summary>
public sealed class HardWorkspaceCommandTests
{
    private static readonly Guid Repository = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static HardWorkspaceLayout Layout() => HardWorkspaceLayout.For("/srv/corex/exec");

    // -- layout -----------------------------------------------------------------------------

    /// <summary>
    /// Every path is under the execution root, and every one is derived from a trusted id.
    ///
    /// Nothing here can carry an organization name, a repository display name or a task
    /// objective, because none of those is an input.
    /// </summary>
    [Fact]
    public void The_layout_is_derived_from_the_execution_root_and_trusted_ids()
    {
        var layout = Layout();

        Assert.Equal("/srv/corex/exec", layout.ExecutionRoot);
        Assert.Equal("/srv/corex/exec/control", layout.ControlRoot);
        Assert.Equal("/srv/corex/exec/control/hooks", layout.HooksDirectory);
        Assert.Equal("/srv/corex/exec/control/empty.gitconfig", layout.EmptyGlobalConfigPath);
        Assert.Equal("/srv/corex/exec/stage", layout.StageRoot);

        Assert.Equal(
            "/srv/corex/exec/repositories/11111111222233334444555555555555/git",
            layout.BareRepository(Repository));

        Assert.Equal(
            "/srv/corex/exec/worktrees/11111111222233334444555555555555",
            layout.Worktree(Repository));
    }

    [Fact]
    public void Every_derived_path_stays_under_the_execution_root()
    {
        var layout = Layout();

        foreach (var path in new[]
                 {
                     layout.ControlRoot, layout.HooksDirectory, layout.EmptyGlobalConfigPath,
                     layout.StageRoot, layout.RepositoriesRoot, layout.WorktreesRoot,
                     layout.BareRepository(Repository), layout.Worktree(Repository),
                     layout.TransferArtefact(Repository),
                 })
        {
            Assert.StartsWith(layout.ExecutionRoot + "/", path, StringComparison.Ordinal);
        }
    }

    /// <summary>Two attempts never derive the same directory, because the identity differs.</summary>
    [Fact]
    public void Two_workspaces_never_share_a_directory()
    {
        var layout = Layout();

        Assert.NotEqual(layout.Worktree(Guid.CreateVersion7()), layout.Worktree(Guid.CreateVersion7()));
    }

    [Fact]
    public void A_workspace_without_an_identity_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Layout().Worktree(Guid.Empty));
    }

    /// <summary>
    /// A root that is not Linux-native storage is refused before anything is built on it.
    ///
    /// A workspace under a mounted host volume would be a Windows directory wearing a Linux name:
    /// the agent would be editing the host's filesystem, and none of the ownership model below
    /// would mean anything.
    /// </summary>
    [Theory]
    [InlineData("/mnt/c/corex")]
    [InlineData("/mnt/d/corex")]
    [InlineData(@"C:\corex")]
    [InlineData("srv/corex")]
    [InlineData("/srv/corex/../../etc")]
    [InlineData("/")]
    public void An_execution_root_that_is_not_linux_native_storage_is_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => HardWorkspaceLayout.For(root));
    }

    // -- hardened configuration -------------------------------------------------------------

    /// <summary>
    /// The exact invocation, compared against a literal.
    ///
    /// Written out in full because the failure worth catching is an option that quietly stops
    /// being emitted, and a test that only checks the options it remembers would not notice.
    /// </summary>
    [Fact]
    public void A_managed_git_command_carries_the_whole_hub_configuration()
    {
        var layout = Layout();

        Assert.Equal(
            [
                "/usr/bin/env",
                "-i",
                "PATH=/usr/bin:/bin",
                "HOME=/srv/corex/exec/control",
                "GIT_CONFIG_NOSYSTEM=1",
                "GIT_CONFIG_GLOBAL=/srv/corex/exec/control/empty.gitconfig",
                "GIT_TERMINAL_PROMPT=0",
                "GIT_OPTIONAL_LOCKS=0",
                "GIT_CONFIG_COUNT=6",
                "GIT_CONFIG_KEY_0=core.hooksPath",
                "GIT_CONFIG_VALUE_0=/srv/corex/exec/control/hooks",
                "GIT_CONFIG_KEY_1=gc.auto",
                "GIT_CONFIG_VALUE_1=0",
                "GIT_CONFIG_KEY_2=core.quotePath",
                "GIT_CONFIG_VALUE_2=false",
                "GIT_CONFIG_KEY_3=core.fsmonitor",
                "GIT_CONFIG_VALUE_3=false",
                "GIT_CONFIG_KEY_4=commit.gpgSign",
                "GIT_CONFIG_VALUE_4=false",
                "GIT_CONFIG_KEY_5=tag.gpgSign",
                "GIT_CONFIG_VALUE_5=false",
                "/usr/bin/git",
                "status",
            ],
            HardGitCommands.Hardened(layout, "status"));
    }

    /// <summary>
    /// The environment starts from nothing, so the guest's own login environment cannot speak.
    ///
    /// Without <c>-i</c> a credential helper, an alias or a proxy configured for the service
    /// account would apply to every managed command, and the Hub would have no idea.
    /// </summary>
    [Fact]
    public void The_managed_environment_starts_empty()
    {
        Assert.Equal("-i", HardGitCommands.Hardened(Layout(), "status")[1]);
    }

    /// <summary>
    /// The hooks path is the Hub's empty directory, and it is set before the command runs.
    ///
    /// This is the first-checkout guarantee. A repository that configures its own hooks path gets
    /// to execute code during the very first checkout-capable command, so the configuration has
    /// to be in force when that command runs rather than applied afterwards - which is why it
    /// travels as environment rather than as a later config write.
    /// </summary>
    [Fact]
    public void The_hooks_path_is_hub_owned_and_effective_before_the_command()
    {
        var command = HardGitCommands.Hardened(Layout(), "worktree", "add", "/srv/corex/exec/worktrees/x", "sha");

        var hooksIndex = command.ToList().IndexOf("GIT_CONFIG_VALUE_0=/srv/corex/exec/control/hooks");
        var gitIndex = command.ToList().IndexOf("/usr/bin/git");

        Assert.True(hooksIndex > 0);
        Assert.True(hooksIndex < gitIndex);
    }

    /// <summary>
    /// The configuration is the same policy the Windows workspace applies, not a second one.
    ///
    /// Restated as an assertion rather than as a comment, because two literal copies of a policy
    /// stay equal only until somebody edits one of them.
    /// </summary>
    [Theory]
    [InlineData(GitConfigKeys.HooksPath)]
    [InlineData(GitConfigKeys.GcAuto)]
    [InlineData(GitConfigKeys.FsMonitor)]
    [InlineData(GitConfigKeys.CommitGpgSign)]
    [InlineData(GitConfigKeys.TagGpgSign)]
    public void The_configuration_keys_are_the_ones_the_hub_already_owned(string key)
    {
        Assert.Contains(
            HardGitCommands.Hardened(Layout(), "status"),
            a => a.EndsWith("=" + key, StringComparison.Ordinal));
    }

    /// <summary>The Hub's commit identity is the one the Windows side already uses.</summary>
    [Fact]
    public void The_result_commit_identity_is_the_hubs_and_is_never_a_command_line_argument()
    {
        var command = HardGitCommands.HardenedWithHubIdentity(Layout(), "commit", "-m", "x");
        var gitIndex = command.ToList().IndexOf("/usr/bin/git");

        Assert.Contains("GIT_AUTHOR_NAME=" + GitEnvironment.HubCommitterName, command);
        Assert.Contains("GIT_COMMITTER_EMAIL=" + GitEnvironment.HubCommitterEmail, command);

        // Environment assignments, before the executable - never -c on the command line.
        Assert.All(
            command.Where(a => a.StartsWith("GIT_AUTHOR", StringComparison.Ordinal)),
            a => Assert.True(command.ToList().IndexOf(a) < gitIndex));

        Assert.DoesNotContain("-c", command);
    }

    // -- no shell, no network ---------------------------------------------------------------

    /// <summary>
    /// Nothing in the command surface is a shell or an interpreter.
    ///
    /// <c>env</c> is not one: it sets variables and executes, and every argument stays its own
    /// vector entry. There is no command line to quote and therefore no quoting rule to get
    /// wrong.
    /// </summary>
    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("/bin/bash")]
    [InlineData("-c")]
    [InlineData("eval")]
    public void The_command_surface_contains_no_shell(string construct)
    {
        var commands = new[]
        {
            HardGitCommands.Hardened(Layout(), "status"),
            HardGitCommands.HardenedWithHubIdentity(Layout(), "commit", "-m", "x"),
            HardGitCommands.DecodeTransfer("/srv/corex/exec/stage/x.bundle"),
            HardGitCommands.ListTransferHeads("/srv/corex/exec/stage/x.bundle"),
            HardGitCommands.Resolve("/srv/corex/exec/worktrees/x"),
        };

        Assert.All(commands, command => Assert.DoesNotContain(construct, command));
    }

    /// <summary>
    /// Evidence commands never invoke a program the repository chose.
    ///
    /// A repository can configure an external diff or a textconv driver, and collecting evidence
    /// is exactly when it would run.
    /// </summary>
    [Fact]
    public void Evidence_commands_refuse_repository_supplied_diff_programs()
    {
        Assert.Equal("--no-ext-diff", HardGitCommands.NoExternalDiff);
        Assert.Equal("--no-textconv", HardGitCommands.NoTextConv);
    }

    /// <summary>
    /// The transfer is a local artefact, and there is no network verb anywhere near it.
    ///
    /// The source is trusted control-plane data that the Hub already had; a hard workspace has
    /// no reason to reach a remote and no credentials with which to.
    /// </summary>
    [Theory]
    [InlineData("clone")]
    [InlineData("pull")]
    [InlineData("push")]
    [InlineData("remote")]
    [InlineData("submodule")]
    public void The_transfer_uses_no_network_verb(string verb)
    {
        Assert.DoesNotContain(verb, HardGitCommands.DecodeTransfer("/srv/corex/exec/stage/x.bundle"));
        Assert.DoesNotContain(verb, HardGitCommands.ListTransferHeads("/srv/corex/exec/stage/x.bundle"));
    }

    /// <summary>The transfer is read back rather than trusted, on whichever side is asking.</summary>
    [Fact]
    public void The_transfer_artefact_can_be_asked_what_it_contains()
    {
        Assert.Equal(
            ["/usr/bin/git", "bundle", "list-heads", "/srv/corex/exec/stage/x.bundle"],
            HardGitCommands.ListTransferHeads("/srv/corex/exec/stage/x.bundle"));
    }
}
