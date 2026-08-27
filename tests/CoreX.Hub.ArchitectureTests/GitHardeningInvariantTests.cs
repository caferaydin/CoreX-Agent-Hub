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
    /// <summary>The hard isolated workspace: a second implementation of the same rules.</summary>
    private const string HardWorkspaceFileName = "HardWorkspaceGateway.cs";

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

            // The hard isolated workspace is the second implementation of the same rule, and it
            // is named rather than implied. Its own exemption test below proves it creates a
            // worktree at a pinned commit, exactly as this one does.
            if (string.Equals(Path.GetFileName(file), HardWorkspaceFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var code = File.ReadAllText(file);
            var name = RepoLayout.Relative(file);

            Assert.False(code.Contains("\"worktree\"", StringComparison.Ordinal), $"{name} builds a worktree command");
            Assert.False(code.Contains("rev-parse", StringComparison.Ordinal), $"{name} builds a rev-parse command");
        }
    }

    /// <summary>
    /// The hard workspace's exemption is real: it creates a worktree at a pinned commit.
    ///
    /// A vacuous exemption would be worse than none - it would look like the rule still had one
    /// implementation while quietly permitting a second file to build any worktree command it
    /// liked.
    /// </summary>
    [Fact]
    public void The_hard_worktree_exemption_is_not_vacuous()
    {
        var gateway = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(
                Path.GetFileName(f), HardWorkspaceFileName, StringComparison.Ordinal));

        Assert.NotNull(gateway);

        var code = File.ReadAllText(gateway!);

        Assert.Contains("\"worktree\", \"add\"", code, StringComparison.Ordinal);
        Assert.Contains("baseCommit", code, StringComparison.Ordinal);
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

    // ------------------------------------------- WORKTREE SCOPED HARDENING (G6-0) ------

    [Fact]
    public void No_working_tree_git_command_runs_before_the_safety_revalidation()
    {
        var method = ExtractMethod(ReadProductionFile(WorkspaceManagerFileName), "CaptureChangesAsync");

        var revalidate = method.IndexOf("RevalidateSafetyAsync(", StringComparison.Ordinal);
        Assert.True(revalidate >= 0, "CaptureChangesAsync must revalidate");

        // Every command below reads or refreshes the working tree, which is what gives a
        // filter driver or an attribute the chance to run a program. Reading HEAD before
        // them is deliberate and safe: refs are not the working tree.
        foreach (var dangerous in new[] { "[\"status\"", "[\"add\", \"-N\"", "[\"diff\"" })
        {
            var at = method.IndexOf(dangerous, StringComparison.Ordinal);

            Assert.True(at > 0, $"CaptureChangesAsync should still issue {dangerous}");
            Assert.True(revalidate < at, $"the safety revalidation must precede {dangerous}");
        }
    }

    [Fact]
    public void The_workspace_revalidation_asks_all_four_questions()
    {
        var revalidation = WorkspaceRevalidation();

        // Repository scope answers for what every worktree inherits; worktree scope for
        // what this one worktree could change; hardening integrity for whether the durable
        // protections are still the ones the Hub wrote; the working tree scan for the
        // attribute files no commit contains. None of the four can answer for another.
        Assert.Contains("InspectWorkingTreeAttributes(workspace.WorkspacePath)", revalidation, StringComparison.Ordinal);
        Assert.Contains("InspectRepositorySafetyAsync(", revalidation, StringComparison.Ordinal);
        Assert.Contains(
            "InspectWorktreeConfigurationAsync(workspace.WorkspacePath", revalidation, StringComparison.Ordinal);
        Assert.Contains(
            "InspectWorkspaceHardeningAsync(workspace.WorkspacePath", revalidation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_worktree_scoped_checks_are_addressed_by_the_workspace_not_the_repository()
    {
        var revalidation = WorkspaceRevalidation();

        // The bug this replaces was exactly this: a workspace revalidation that forwarded
        // to the repository path, so the mutable scope was never the one inspected.
        foreach (var mustNot in new[]
                 {
                     "InspectWorktreeConfigurationAsync(workspace.RepositoryPath",
                     "InspectWorkspaceHardeningAsync(workspace.RepositoryPath",
                     "InspectWorkingTreeAttributes(workspace.RepositoryPath",
                 })
        {
            Assert.DoesNotContain(mustNot, revalidation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_working_tree_attribute_scan_precedes_every_git_read_in_the_revalidation()
    {
        var revalidation = WorkspaceRevalidation();

        var scan = revalidation.IndexOf("InspectWorkingTreeAttributes(", StringComparison.Ordinal);
        var firstGit = revalidation.IndexOf("await Inspect", StringComparison.Ordinal);

        Assert.True(scan >= 0 && firstGit > 0);
        Assert.True(scan < firstGit, "the filesystem owned scan needs no git and must run first");
    }

    [Fact]
    public void The_working_tree_attribute_scan_never_asks_git_to_find_the_files()
    {
        var scan = ExtractOverload(
            ReadProductionFile(WorkspaceManagerFileName), "InspectWorkingTreeAttributes", "EnumerationOptions");

        // Asking git to enumerate the working tree is itself one of the operations whose
        // safety this scan exists to establish.
        Assert.DoesNotContain("GitAsync(", scan, StringComparison.Ordinal);
        Assert.Contains("Directory", scan, StringComparison.Ordinal);

        // Reparse trust and containment stay with the managed path boundary.
        Assert.Contains("_paths.Validate(", scan, StringComparison.Ordinal);
    }

    [Fact]
    public void The_safety_inspection_only_ever_reads_configuration()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        foreach (var (methodName, marker) in new[]
                 {
                     ("InspectConfigurationScopeAsync", "scopeLabel"),
                     ("ReadWorktreeSettingAsync", "--get-all"),
                 })
        {
            var commands = ConfigCommandLines(ExtractOverload(source, methodName, marker));

            // Non vacuous: the method really does issue configuration commands.
            Assert.NotEmpty(commands);

            foreach (var line in commands)
            {
                var reads = line.Contains("--list", StringComparison.Ordinal)
                            || line.Contains("--get-all", StringComparison.Ordinal);

                Assert.True(reads, $"{methodName} must only read configuration, not write it: {line}");
            }
        }

        // The hardening assertion delegates its reads and issues no command of its own, so
        // there is nowhere in it for a write to hide.
        var assertion = ExtractOverload(source, "AssertHardeningAsync", "ReadWorktreeSettingAsync(");
        Assert.Empty(ConfigCommandLines(assertion));
    }

    [Fact]
    public void Configuration_is_written_in_exactly_one_place()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        // Every configuration command in the module is either a read or one of the two
        // writes that establish the hardening in the first place. There is no third kind,
        // which is what makes "drift is refused, never repaired" a property of the code
        // rather than a promise in a comment.
        var writes = ConfigCommandLines(source)
            .Where(l => !l.Contains("--list", StringComparison.Ordinal)
                        && !l.Contains("--get", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                "[\"config\", \"--local\", GitConfigKeys.WorktreeConfigExtension, \"true\"],",
                "[\"config\", \"--worktree\", setting.Key, setting.Value],",
            ],
            writes.Order(StringComparer.Ordinal));

        var harden = ExtractOverload(source, "HardenAsync", "WorktreeConfigExtension");
        Assert.Equal(2, ConfigCommandLines(harden).Count);

        Assert.DoesNotContain("--unset", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--replace-all", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_configuration_scopes_share_one_unsupported_family_authority()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        // Two scopes, one list. A second list would be a second thing to remember.
        Assert.Single(
            source.Split('\n'),
            l => l.Contains("foreach (var family in UnsupportedConfigurationFamilies)", StringComparison.Ordinal));

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            Assert.DoesNotContain(
                "WorktreeUnsupportedConfigurationFamilies",
                File.ReadAllText(file),
                StringComparison.Ordinal);
        }

        // And both scopes really do reach that one list.
        Assert.Contains("SharedConfigurationScope, \"repository-local\"", source, StringComparison.Ordinal);
        Assert.Contains("WorktreeConfigurationScope, \"worktree-scoped\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_child_launch_adapter_applies_the_git_baseline_last()
    {
        var adapter = ReadProductionFile("ProcessAgentRunner.cs");

        Assert.Contains("AdditionalEnvironment = ComposeEnvironment(request)", adapter, StringComparison.Ordinal);

        var compose = ExtractOverload(adapter, "ComposeEnvironment", "ForAgentChild(");

        var runtimeValues = compose.IndexOf("request.Environment", StringComparison.Ordinal);
        var baseline = compose.IndexOf("GitEnvironment.ForAgentChild(", StringComparison.Ordinal);

        Assert.True(runtimeValues >= 0 && baseline > 0);
        Assert.True(baseline > runtimeValues, "the baseline must be applied after the runtime's own variables");

        // The baseline is the Hub git baseline, not a second copy of it.
        var environment = ReadProductionFile(GitEnvironmentFileName);
        Assert.Contains("ForAgentChild(string emptyGlobalConfigPath) =>", environment, StringComparison.Ordinal);
        Assert.Contains("Base(emptyGlobalConfigPath);", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void The_native_windows_phase_six_shell_boundary_is_recorded()
    {
        var adr = Directory
            .EnumerateFiles(Path.Combine(RepoLayout.Root, "docs", "adr"), "ADR-0020-*.md")
            .SingleOrDefault();

        Assert.True(adr is not null, "the native Windows agent shell boundary must be recorded as an ADR");

        var text = File.ReadAllText(adr!);

        // The decision, the reason it is not a policy claim, and the condition for revisiting.
        Assert.Contains("Bash", text, StringComparison.Ordinal);
        Assert.Contains("--disallowedTools", text, StringComparison.Ordinal);
        Assert.Contains("--no-verify", text, StringComparison.Ordinal);
        Assert.Contains("Accepted", text, StringComparison.Ordinal);

        var index = File.ReadAllText(Path.Combine(RepoLayout.Root, "docs", "adr", "README.md"));
        Assert.Contains("ADR-0020", index, StringComparison.Ordinal);
    }

    private static string WorkspaceRevalidation() =>
        ExtractOverload(
            ReadProductionFile(WorkspaceManagerFileName),
            "RevalidateSafetyAsync",
            "InspectWorkspaceHardeningAsync(");

    /// <summary>Every git configuration command the source issues, one per line.</summary>
    private static List<string> ConfigCommandLines(string source) =>
        [.. source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("[\"config\"", StringComparison.Ordinal))];

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
