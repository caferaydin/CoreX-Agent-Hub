using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Composition.HardWorkspace;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The hard workspace against a real Windows repository and a real WSL environment.
///
/// Two claims are being tested and they are separable. The first is about what crosses the
/// boundary: a pinned commit and nothing else - not the developer's uncommitted edits, not their
/// untracked files, not their ignored ones, not their git configuration. The second is about who
/// owns what afterwards: the agent may edit source files and may not become the administrator of
/// the repository those files came from.
///
/// What this does not claim is that a Hub task is isolated. A workspace is one of the things a
/// task needs on the right side of the boundary; the context, the evidence, the provider
/// authentication and the network are still on the host, and the last tests here re-establish
/// that admission still refuses.
///
/// On a machine with no WSL or no git the fixture reports unavailable and each test says so
/// rather than failing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardWorkspaceTests : IAsyncLifetime
{
    /// <summary>The execution these workspace operations belong to.</summary>
    private static readonly Guid ExecutionIdentity = Guid.Parse("0195b1c0-0000-7000-8000-00000000w5pc".Replace('w', 'a').Replace('p', 'b'));

    private readonly HardWorkspaceFixture _fixture = new();

    private HardWorkspaceGateway _gateway = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        if (_fixture.Available)
        {
            _gateway = _fixture.Gateway();
        }
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<PreparedExecutionWorkspace> PrepareAsync(string taskKey = "HARD-1", int attempt = 1)
    {
        var preflight = await _gateway.PreflightAsync(_fixture.RepositoryId, CancellationToken.None);

        Assert.Equal(_fixture.BaseCommitSha, preflight.BaseCommitSha);

        var prepared = await _gateway.PrepareAsync(
            ExecutionIdentity,
            _fixture.RepositoryId, preflight, taskKey, attempt, CancellationToken.None);

        Assert.NotNull(prepared);
        return prepared!;
    }

    private Task<(int? ExitCode, string Stdout, string Stderr)> AgentAsync(params string[] arguments) =>
        _fixture.ControlAsync(arguments, HardWorkspaceFixture.AgentUser);

    // -- what crosses -----------------------------------------------------------------------

    /// <summary>
    /// The worktree is at the pinned commit, and the file holds the committed content.
    ///
    /// The host's copy of that same file says something different at this moment, which is what
    /// makes the assertion about the transfer rather than about the file.
    /// </summary>
    [Fact]
    public async Task The_worktree_holds_the_pinned_commit_and_not_the_host_working_tree()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        Assert.Equal(_fixture.BaseCommitSha, prepared.BaseCommitSha);

        var head = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "rev-parse", "HEAD"]);

        Assert.Equal(_fixture.BaseCommitSha, head.Stdout.Trim());

        var tracked = await _fixture.ControlAsync(["/usr/bin/cat", prepared.WorkspacePath + "/tracked.txt"]);

        Assert.Contains(HardWorkspaceFixture.CommittedContent, tracked.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(HardWorkspaceFixture.DirtyMarker, tracked.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three things that live only in the developer's working tree stay there.
    ///
    /// This is the central negative of the whole slice. An ignored file is exactly where a
    /// developer keeps a credential, and a transfer that copied a directory rather than a commit
    /// would carry it without anybody noticing.
    /// </summary>
    [Fact]
    public async Task Nothing_that_was_not_committed_reaches_the_environment()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        var listed = await _fixture.ControlAsync(["/usr/bin/ls", "-a", prepared.WorkspacePath]);

        Assert.DoesNotContain("secret.txt", listed.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("untracked.txt", listed.Stdout, StringComparison.Ordinal);

        // And not hiding in the object database either.
        var everything = await _fixture.ControlAsync(
            ["/usr/bin/grep", "-r", "-l", HardWorkspaceFixture.SecretMarker, _fixture.ExecutionRoot]);

        Assert.NotEqual(0, everything.ExitCode);
    }

    /// <summary>
    /// The target repository's configuration is the Hub's, not the source repository's.
    ///
    /// Committed content is untrusted and a developer machine's git configuration is not content
    /// the Hub has any reason to adopt - a credential helper, an alias, a filter driver would all
    /// arrive with it.
    /// </summary>
    [Fact]
    public async Task The_target_repository_config_is_hub_created()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await PrepareAsync();

        var config = await _fixture.ControlAsync(
            ["/usr/bin/git", "--git-dir=" + _fixture.Layout.BareRepository(_fixture.RepositoryId),
             "config", "--list", "--local"]);

        Assert.Contains("core.bare=true", config.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", config.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user.email", config.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filter", config.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// There is no remote, so there is nothing to fetch from and nowhere to push to.
    ///
    /// The source repository's origin is not copied. A hard workspace that inherited it would be
    /// one accidental publish away from writing to the real repository with the developer's
    /// credentials.
    /// </summary>
    [Fact]
    public async Task The_target_repository_has_no_remote()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await PrepareAsync();

        var remotes = await _fixture.ControlAsync(
            ["/usr/bin/git", "--git-dir=" + _fixture.Layout.BareRepository(_fixture.RepositoryId), "remote"]);

        Assert.Equal(string.Empty, remotes.Stdout.Trim());
    }

    /// <summary>The workspace is on the environment's own filesystem, not a mounted host drive.</summary>
    [Fact]
    public async Task The_workspace_is_linux_native()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        Assert.StartsWith("/", prepared.WorkspacePath, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/", prepared.WorkspacePath, StringComparison.Ordinal);
        Assert.DoesNotContain(":", prepared.WorkspacePath, StringComparison.Ordinal);

        var filesystem = await _fixture.ControlAsync(
            ["/usr/bin/stat", "-f", "-c", "%T", prepared.WorkspacePath]);

        Assert.Contains(
            filesystem.Stdout.Trim(),
            new[] { "ext2/ext3", "ext4", "xfs", "btrfs", "overlayfs" });
    }

    /// <summary>
    /// A branch that moved after the preflight does not quietly become the new base.
    ///
    /// The pinned commit is what was written down as this execution's provenance, and everything
    /// downstream measures against it. So the artefact is asked what it actually carries, and a
    /// branch that has moved on produces a refusal rather than an execution based on a commit
    /// nobody recorded.
    /// </summary>
    [Fact]
    public async Task A_source_branch_that_moved_after_the_preflight_is_refused()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var preflight = await _gateway.PreflightAsync(_fixture.RepositoryId, CancellationToken.None);

        Assert.Equal(_fixture.BaseCommitSha, preflight.BaseCommitSha);

        // The developer commits again while the Hub is between deciding and materialising.
        await File.WriteAllTextAsync(
            Path.Combine(_fixture.SourceRepository, "moved.txt"), "after the preflight\n");

        await _fixture.SourceGitAsync("add", "moved.txt");
        await _fixture.SourceGitAsync("commit", "-m", "moved");

        Assert.NotEqual(_fixture.BaseCommitSha, await _fixture.SourceGitAsync("rev-parse", "main"));

        var prepared = await _gateway.PrepareAsync(
            ExecutionIdentity,
            _fixture.RepositoryId, preflight, "HARD-MOVED", 1, CancellationToken.None);

        Assert.Null(prepared);
    }

    // -- ownership --------------------------------------------------------------------------

    [Fact]
    public async Task The_agent_can_edit_and_create_source_files()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        var edited = await AgentAsync("/usr/bin/tee", prepared.WorkspacePath + "/tracked.txt");
        var created = await AgentAsync("/usr/bin/touch", prepared.WorkspacePath + "/added.txt");

        Assert.Equal(0, edited.ExitCode);
        Assert.Equal(0, created.ExitCode);
    }

    /// <summary>
    /// The agent may not write to the controlled repository, or even look inside it.
    ///
    /// The object database, the refs and the configuration are what a repository *is*; an agent
    /// that could write them could write history the Hub never authorised and then present it as
    /// its own result.
    /// </summary>
    [Fact]
    public async Task The_agent_cannot_reach_the_controlled_repository()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await PrepareAsync();

        var bare = _fixture.Layout.BareRepository(_fixture.RepositoryId);

        var listed = await AgentAsync("/usr/bin/ls", bare);
        var written = await AgentAsync("/usr/bin/tee", bare + "/config");
        var refs = await AgentAsync("/usr/bin/tee", bare + "/refs/heads/agent-owned");

        Assert.NotEqual(0, listed.ExitCode);
        Assert.NotEqual(0, written.ExitCode);
        Assert.NotEqual(0, refs.ExitCode);
    }

    /// <summary>
    /// The agent cannot replace or remove the pointer that says which repository this is.
    ///
    /// The subtle half is the removal. A directory the agent can create files in normally lets it
    /// delete anything inside, including a file it cannot write - so the worktree carries the
    /// sticky bit, and an entry may only be removed by whoever owns it.
    /// </summary>
    [Fact]
    public async Task The_agent_cannot_rewrite_the_worktree_control_metadata()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();
        var pointer = prepared.WorkspacePath + "/.git";

        var overwritten = await _fixture.ControlAsync(
            ["/usr/bin/tee", pointer], HardWorkspaceFixture.AgentUser, "gitdir: /tmp/elsewhere\n");

        var removed = await AgentAsync("/usr/bin/rm", "-f", pointer);
        var moved = await AgentAsync("/usr/bin/mv", pointer, prepared.WorkspacePath + "/.git.bak");

        Assert.NotEqual(0, overwritten.ExitCode);
        Assert.NotEqual(0, removed.ExitCode);
        Assert.NotEqual(0, moved.ExitCode);

        var still = await _fixture.ControlAsync(["/usr/bin/cat", pointer]);
        Assert.Contains(_fixture.Layout.BareRepository(_fixture.RepositoryId), still.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent cannot use git against the worktree at all, which is stronger than not being
    /// allowed to commit.
    ///
    /// It follows from the ownership rather than from a tool restriction: git needs the gitdir,
    /// and the gitdir is not the agent's to read. A runtime allowlist could be worked around; a
    /// permission cannot be.
    /// </summary>
    [Fact]
    public async Task The_agent_cannot_create_a_commit()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        await AgentAsync("/usr/bin/tee", prepared.WorkspacePath + "/tracked.txt");

        var status = await AgentAsync("/usr/bin/git", "-C", prepared.WorkspacePath, "status", "--porcelain");
        var commit = await AgentAsync(
            "/usr/bin/git", "-C", prepared.WorkspacePath, "commit", "-am", "agent authored");

        Assert.NotEqual(0, status.ExitCode);
        Assert.NotEqual(0, commit.ExitCode);

        var head = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "rev-parse", "HEAD"]);

        Assert.Equal(_fixture.BaseCommitSha, head.Stdout.Trim());
    }

    // -- capture ----------------------------------------------------------------------------

    [Fact]
    public async Task The_service_identity_can_see_what_the_agent_changed()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        await _fixture.ControlAsync(
            ["/usr/bin/tee", prepared.WorkspacePath + "/tracked.txt"],
            HardWorkspaceFixture.AgentUser,
            "agent edited this\n");

        await _fixture.ControlAsync(
            ["/usr/bin/tee", prepared.WorkspacePath + "/added.txt"],
            HardWorkspaceFixture.AgentUser,
            "agent added this\n");

        var capture = await _gateway.CaptureAsync(prepared, CancellationToken.None);

        Assert.Equal(_fixture.BaseCommitSha, capture.BaseCommitSha);
        Assert.Equal(_fixture.BaseCommitSha, capture.ObservedHeadSha);
        Assert.Empty(capture.UnexpectedCommitShas);
        Assert.Contains("tracked.txt", capture.ChangedPaths);
        Assert.Contains("added.txt", capture.ChangedPaths);
        Assert.Contains("agent edited this", capture.GitWorktreeDiff, StringComparison.Ordinal);
    }

    /// <summary>
    /// A commit that appeared without the Hub making it is visible as one.
    ///
    /// The ordinary agent cannot produce this, which is the stronger guarantee - so the test
    /// injects one with the trusted identity instead. The point is that capture would notice: an
    /// invariant that only holds because nobody can currently break it is worth checking against
    /// somebody who can.
    /// </summary>
    [Fact]
    public async Task An_unexpected_commit_is_detected_rather_than_repaired()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        await _fixture.ControlAsync(
            ["/usr/bin/tee", prepared.WorkspacePath + "/tracked.txt"],
            HardWorkspaceFixture.AgentUser,
            "content behind an injected commit\n");

        await _fixture.ControlAsync(["/usr/bin/git", "-C", prepared.WorkspacePath, "add", "-A"]);
        await _fixture.ControlAsync(
            ["/usr/bin/env", "GIT_AUTHOR_NAME=Injected", "GIT_AUTHOR_EMAIL=injected@corex.local",
             "GIT_COMMITTER_NAME=Injected", "GIT_COMMITTER_EMAIL=injected@corex.local", "HOME=/root",
             "/usr/bin/git", "-C", prepared.WorkspacePath, "commit", "-m", "injected"]);

        var capture = await _gateway.CaptureAsync(prepared, CancellationToken.None);

        Assert.NotEqual(capture.BaseCommitSha, capture.ObservedHeadSha);
        Assert.Single(capture.UnexpectedCommitShas);

        // Detected, and left exactly where it was. Repairing it would destroy the evidence that
        // it happened.
        var head = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "rev-parse", "HEAD"]);

        Assert.Equal(capture.ObservedHeadSha, head.Stdout.Trim());
    }

    // -- result commit ----------------------------------------------------------------------

    /// <summary>
    /// The Hub's own commit: one parent, the pinned commit, and the Hub's identity on it.
    ///
    /// The agent could not have made this and did not: it has no access to the object database.
    /// What the assertion establishes is that when the Hub makes one, it is exactly the commit
    /// the provenance says it is.
    /// </summary>
    [Fact]
    public async Task The_hub_creates_the_result_commit_with_its_own_identity()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync("HARD-2", 3);

        await _fixture.ControlAsync(
            ["/usr/bin/tee", prepared.WorkspacePath + "/tracked.txt"],
            HardWorkspaceFixture.AgentUser,
            "the agent's work\n");

        var outcome = await _gateway.CreateResultCommitAsync(
            prepared, "HARD-2", 3, CancellationToken.None);

        Assert.True(outcome.Created, outcome.Detail);
        Assert.NotNull(outcome.CommitSha);

        var parent = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "rev-parse", outcome.CommitSha + "^"]);

        Assert.Equal(_fixture.BaseCommitSha, parent.Stdout.Trim());

        var identity = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "log", "-1", "--format=%an|%ae|%cn|%ce"]);

        Assert.Equal(
            "CoreX Agent Hub|agent@corex.local|CoreX Agent Hub|agent@corex.local",
            identity.Stdout.Trim());

        var changed = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "show", "--name-only", "--format=", outcome.CommitSha!]);

        Assert.Equal("tracked.txt", changed.Stdout.Trim());

        var branch = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "rev-parse", "--abbrev-ref", "HEAD"]);

        Assert.Equal("agent/HARD-2/3", branch.Stdout.Trim());

        var count = await _fixture.ControlAsync(
            ["/usr/bin/git", "-C", prepared.WorkspacePath, "rev-list", "--count",
             _fixture.BaseCommitSha + "..HEAD"]);

        Assert.Equal("1", count.Stdout.Trim());
    }

    // -- path safety ------------------------------------------------------------------------

    /// <summary>
    /// A symlink out of the worktree is not followed because its path looks contained.
    ///
    /// The agent may create symlinks - source trees contain them - and every character of the
    /// link's own path stays under the root. The Hub resolves first and decides on the target.
    /// </summary>
    [Fact]
    public async Task A_symlink_out_of_the_worktree_is_refused()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        var linked = await AgentAsync("/usr/bin/ln", "-s", "/etc", prepared.WorkspacePath + "/escape");

        Assert.Equal(0, linked.ExitCode);

        Assert.False(
            await _gateway.IsInsideWorktreeAsync(
                prepared, prepared.WorkspacePath + "/escape/passwd", CancellationToken.None));

        Assert.True(
            await _gateway.IsInsideWorktreeAsync(
                prepared, prepared.WorkspacePath + "/tracked.txt", CancellationToken.None));
    }

    // -- source immutability ----------------------------------------------------------------

    /// <summary>
    /// The Windows repository is exactly as it was: same HEAD, same refs, same working tree.
    ///
    /// The transfer reads; it does not write. A temporary ref, a temporary branch or a stash
    /// would each have been an easy way to build the artefact and each would have mutated
    /// somebody's repository to do it.
    /// </summary>
    [Fact]
    public async Task The_source_repository_is_left_exactly_as_it_was()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var headBefore = await _fixture.SourceGitAsync("rev-parse", "HEAD");
        var refsBefore = await _fixture.SourceGitAsync("show-ref");
        var statusBefore = await _fixture.SourceGitAsync("status", "--porcelain");
        var configBefore = await _fixture.SourceGitAsync("config", "--list", "--local");

        await PrepareAsync();

        Assert.Equal(headBefore, await _fixture.SourceGitAsync("rev-parse", "HEAD"));
        Assert.Equal(refsBefore, await _fixture.SourceGitAsync("show-ref"));
        Assert.Equal(statusBefore, await _fixture.SourceGitAsync("status", "--porcelain"));
        Assert.Equal(configBefore, await _fixture.SourceGitAsync("config", "--list", "--local"));

        // The dirty edit and the untracked file are still the developer's, untouched.
        Assert.Contains(
            HardWorkspaceFixture.DirtyMarker,
            await File.ReadAllTextAsync(Path.Combine(_fixture.SourceRepository, "tracked.txt")),
            StringComparison.Ordinal);
    }

    // -- release ----------------------------------------------------------------------------

    [Fact]
    public async Task Releasing_removes_the_worktree_and_keeps_the_repository()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        var released = await _gateway.ReleaseAsync(prepared, CancellationToken.None);

        Assert.True(released.Released, released.Detail);

        var worktree = await _fixture.ControlAsync(["/usr/bin/stat", "-c", "%n", prepared.WorkspacePath]);
        Assert.NotEqual(0, worktree.ExitCode);

        // The branch the result commit would be on outlives the worktree; it is the provenance an
        // acceptance decision is about.
        var branch = await _fixture.ControlAsync(
            ["/usr/bin/git", "--git-dir=" + _fixture.Layout.BareRepository(_fixture.RepositoryId),
             "rev-parse", "--verify", prepared.BranchName]);

        Assert.Equal(0, branch.ExitCode);
    }

    /// <summary>
    /// A worktree that cannot be released is reported, never forced.
    ///
    /// Removing it harder is how evidence disappears, and a worktree that will not come away
    /// cleanly is precisely the case an operator should be told about.
    /// </summary>
    [Fact]
    public async Task A_worktree_that_will_not_release_is_reported_as_an_orphan()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var prepared = await PrepareAsync();

        // Git refuses to remove a worktree whose administrative data has gone.
        await _fixture.ControlAsync(
            ["/usr/bin/rm", "-rf",
             _fixture.Layout.BareRepository(_fixture.RepositoryId) + "/worktrees"]);

        var released = await _gateway.ReleaseAsync(prepared, CancellationToken.None);

        Assert.False(released.Released);
        Assert.Equal(prepared.WorkspacePath, released.WorktreePath);
        Assert.NotEmpty(released.Detail);

        // Reported, and still there. Nothing was destroyed to make the report tidy.
        var still = await _fixture.ControlAsync(["/usr/bin/stat", "-c", "%n", prepared.WorkspacePath]);
        Assert.Equal(0, still.ExitCode);
    }

    // -- what this does not change ----------------------------------------------------------

    /// <summary>The environment these tests ran in is still not hard-ready.</summary>
    [Fact]
    public async Task Running_here_does_not_make_this_environment_hard_ready()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await PrepareAsync();

        var readiness = await WslReadinessProbe.Create(Path.GetTempPath()).ProbeAsync(
            "fixture", _fixture.Profile(), CancellationToken.None);

        Assert.False(readiness.IsReady);
        Assert.NotEmpty(readiness.UnmetCheckIds);
    }

    /// <summary>
    /// A working hard workspace does not make a hard task available.
    ///
    /// The temptation this guards against is specific: source now lives inside the boundary, so
    /// it would be easy to conclude the work does. The context, the evidence, the provider
    /// authentication and the network are still on the host.
    /// </summary>
    [Fact]
    public void A_working_hard_workspace_does_not_make_hard_execution_available()
    {
        var plane = new UnavailableHardExecutionPlane();

        Assert.False(plane.IsAvailable);
        Assert.Contains("on the host", plane.UnavailableReason, StringComparison.Ordinal);
    }
}
