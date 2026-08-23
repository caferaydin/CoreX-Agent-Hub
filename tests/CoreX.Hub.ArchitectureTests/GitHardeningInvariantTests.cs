namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Structural guards for the git secondary-execution hardening.
///
/// These complement, and never replace, the behavioural marker tests in
/// CoreX.Hub.Workspace.Tests: those prove a repository controlled helper does not run,
/// while these make it hard to silently drop a flag later.
/// </summary>
public sealed class GitHardeningInvariantTests
{
    private const string WorkspaceManagerFileName = "WorkspaceManager.cs";
    private const string GitEnvironmentFileName = "GitEnvironment.cs";

    [Fact]
    public void Every_managed_diff_disables_external_diff_and_textconv()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        var diffLines = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("[\"diff\"", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(diffLines);

        foreach (var line in diffLines)
        {
            Assert.Contains("NoExternalDiff", line, StringComparison.Ordinal);
            Assert.Contains("NoTextConv", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_managed_git_runtime_neutralises_fsmonitor_and_signing()
    {
        var source = ReadProductionFile(GitEnvironmentFileName);

        Assert.Contains("(GitConfigKeys.FsMonitor, \"false\")", source, StringComparison.Ordinal);
        Assert.Contains("(GitConfigKeys.CommitGpgSign, \"false\")", source, StringComparison.Ordinal);
        Assert.Contains("core.fsmonitor", source, StringComparison.Ordinal);
        Assert.Contains("commit.gpgSign", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_safety_preflight_is_reachable_from_every_required_boundary()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        // Workspace creation goes through the full preflight.
        AssertMethodContains(source, "PrepareAsync", "await PreflightAsync(");

        // Evidence collection and staging revalidate, because config can change in between.
        AssertMethodContains(source, "CaptureChangesAsync", "RevalidateSafetyAsync(");
        AssertMethodContains(source, "CreateResultCommitAsync", "RevalidateSafetyAsync(");
    }

    [Fact]
    public void The_overload_that_creates_the_worktree_resolves_nothing_of_its_own()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        // Two overloads on purpose: one resolves and materialises in a breath, the other
        // materialises a commit the caller already resolved and acted on. The second must
        // not preflight again, because a second resolution is a second answer - and by then
        // the branch may have moved, which is exactly the drift being ruled out.
        var materialise = ExtractOverload(source, "PrepareAsync", "[\"worktree\", \"add\"");

        Assert.DoesNotContain("await PreflightAsync(", materialise, StringComparison.Ordinal);

        // The one form that turns a branch name into a commit belongs to the preflight and
        // nowhere else. Here only the pinned commit is verified, as itself.
        Assert.DoesNotContain("DefaultBranch + \"^{commit}\"", materialise, StringComparison.Ordinal);
        Assert.Contains("preflight.BaseCommitSha + \"^{commit}\"", materialise, StringComparison.Ordinal);
    }

    [Fact]
    public void Safety_is_revalidated_under_the_lock_and_before_anything_is_created()
    {
        var materialise = ExtractOverload(
            ReadProductionFile(WorkspaceManagerFileName), "PrepareAsync", "[\"worktree\", \"add\"");

        var acquire = materialise.IndexOf("gate.WaitAsync(", StringComparison.Ordinal);
        var revalidate = materialise.IndexOf("RevalidateSafetyAsync(", StringComparison.Ordinal);
        var pinned = materialise.IndexOf("preflight.BaseCommitSha + \"^{commit}\"", StringComparison.Ordinal);
        var create = materialise.IndexOf("Directory.CreateDirectory(", StringComparison.Ordinal);
        var add = materialise.IndexOf("[\"worktree\", \"add\"", StringComparison.Ordinal);
        var harden = materialise.IndexOf("HardenAsync(", StringComparison.Ordinal);

        Assert.True(revalidate >= 0, "the materialising overload must revalidate safety");

        // Checking out is itself the dangerous operation - git worktree add runs hooks and
        // filters - so noticing unsafe configuration afterwards and deleting the tree would
        // mean the repository's program had already run. The order is the protection:
        // lock, revalidate, verify the pin, then create.
        Assert.InRange(revalidate, acquire, pinned);
        Assert.InRange(pinned, revalidate, create);
        Assert.InRange(create, pinned, add);
        Assert.True(add < harden, "hardening follows the worktree it hardens");
    }

    [Fact]
    public void Staging_never_happens_before_the_safety_revalidation()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);
        var method = ExtractMethod(source, "CreateResultCommitAsync");

        var revalidate = method.IndexOf("RevalidateSafetyAsync(", StringComparison.Ordinal);
        var stage = method.IndexOf("[\"add\", \"-A\"]", StringComparison.Ordinal);

        Assert.True(revalidate >= 0, "CreateResultCommitAsync must revalidate");
        Assert.True(stage > 0, "CreateResultCommitAsync must still stage");
        Assert.True(revalidate < stage, "the safety revalidation must precede git add -A");
    }

    [Fact]
    public void The_unsupported_configuration_families_are_explicit_and_small()
    {
        // Deliberately a short hard-coded list, not a policy engine.
        Assert.Equal(
            ["filter.", "include.", "includeif."],
            Workspace.WorkspaceManager.UnsupportedConfigurationFamilies.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Worktree_creation_and_hardening_share_one_repository_critical_section()
    {
        var method = ExtractOverload(
            ReadProductionFile(WorkspaceManagerFileName), "PrepareAsync", "[\"worktree\", \"add\"");

        var acquire = method.IndexOf("gate.WaitAsync(", StringComparison.Ordinal);
        var add = method.IndexOf("[\"worktree\", \"add\"", StringComparison.Ordinal);
        var harden = method.IndexOf("HardenAsync(", StringComparison.Ordinal);
        var release = method.IndexOf("gate.Release()", StringComparison.Ordinal);

        Assert.True(acquire >= 0, "PrepareAsync must acquire the repository lock");
        Assert.True(release > acquire, "PrepareAsync must release the repository lock");

        // Both shared metadata mutations belong to the same section: the worktree add and
        // the hardening that rewrites <repo>/.git/config. Hardening drifting back outside
        // the lock is exactly the race this guards against.
        Assert.InRange(add, acquire, release);
        Assert.InRange(harden, acquire, release);
    }

    [Fact]
    public void The_repository_lock_is_keyed_by_a_normalised_repository_path()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        // Every acquisition goes through the one key function, so two spellings of one
        // repository can never take two locks.
        var acquisitions = source
            .Split('\n')
            .Where(l => l.Contains("RepositoryLocks.GetOrAdd", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(acquisitions);
        Assert.All(acquisitions, l =>
            Assert.Contains("RepositoryLockKey(", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Exact_commit_worktrees_are_created_only_by_the_workspace_module()
    {
        // Workspace owns worktree creation and removal. Application and Verification ask
        // for a snapshot; they never build the git command themselves.
        var snapshots = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), "WorkspaceManager.Snapshots.cs", StringComparison.Ordinal));

        Assert.Contains(
            Path.DirectorySeparatorChar + "CoreX.Hub.Workspace" + Path.DirectorySeparatorChar,
            snapshots,
            StringComparison.Ordinal);

        var snapshotCode = File.ReadAllText(snapshots);
        Assert.Contains("[\"worktree\", \"add\", \"--detach\"", snapshotCode, StringComparison.Ordinal);

        // Nowhere else in src builds a worktree command.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            if (file.Contains(
                    Path.DirectorySeparatorChar + "CoreX.Hub.Workspace" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var code = File.ReadAllText(file);
            var name = RepoLayout.Relative(file);

            Assert.False(code.Contains("\"worktree\"", StringComparison.Ordinal), $"{name} builds a worktree command");
            Assert.False(code.Contains("rev-parse", StringComparison.Ordinal), $"{name} builds a rev-parse command");
        }
    }

    [Fact]
    public void A_pinned_snapshot_resolves_nothing_and_reaches_no_network()
    {
        // Comments are stripped: naming a thing in order to say it does not happen is
        // documentation, not the behaviour.
        var code = string.Join(
            "\n",
            ReadProductionFile("WorkspaceManager.Snapshots.cs")
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // The caller's commit is used as given. Resolving a branch here would be the exact
        // bug the seam exists to prevent.
        foreach (var forbidden in new[] { "DefaultBranch", "origin/", "refs/heads", "fetch", "pull", "remote" })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
        }

        // It uses the shared lock and the shared hardening rather than a second copy.
        Assert.Contains("RepositoryLocks.GetOrAdd(RepositoryLockKey(", code, StringComparison.Ordinal);
        Assert.Contains("HardenAsync(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("extensions.worktreeConfig", code, StringComparison.Ordinal);

        // And it verifies what git actually reports rather than trusting the request.
        Assert.Contains("rev-parse", code, StringComparison.Ordinal);
        Assert.Contains("HeadMismatch", code, StringComparison.Ordinal);
    }

    private static string ReadProductionFile(string fileName)
    {
        var path = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));

        Assert.NotNull(path);
        return File.ReadAllText(path);
    }

    private static void AssertMethodContains(string source, string methodName, string expected)
    {
        var method = ExtractMethod(source, methodName);
        Assert.Contains(expected, method, StringComparison.Ordinal);
    }

    /// <summary>
    /// Crude but sufficient: everything between a method signature and the next method
    /// signature at the same indentation.
    /// </summary>
    private static string ExtractMethod(string source, string methodName) =>
        ExtractOverloads(source, methodName).First();

    /// <summary>
    /// The overload of <paramref name="methodName"/> whose body contains
    /// <paramref name="marker"/>.
    ///
    /// Overloads are how "resolve then materialise" stays separable from "resolve and
    /// materialise", so a guard about one of them has to be able to say which.
    /// </summary>
    private static string ExtractOverload(string source, string methodName, string marker)
    {
        var match = ExtractOverloads(source, methodName)
            .FirstOrDefault(body => body.Contains(marker, StringComparison.Ordinal));

        Assert.True(match is not null, $"no {methodName} overload contains {marker}");
        return match!;
    }

    private static IEnumerable<string> ExtractOverloads(string source, string methodName)
    {
        var found = false;
        var from = 0;

        while (true)
        {
            var start = source.IndexOf(methodName + "(", from, StringComparison.Ordinal);

            if (start < 0)
            {
                Assert.True(found, $"{methodName} was not found");
                yield break;
            }

            found = true;

            var next = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
            var privateNext = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);

            if (privateNext >= 0 && (next < 0 || privateNext < next))
            {
                next = privateNext;
            }

            yield return next < 0 ? source[start..] : source[start..next];

            if (next < 0)
            {
                yield break;
            }

            from = next;
        }
    }
}
