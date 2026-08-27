using System.Text.RegularExpressions;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Source level guards for the architecture invariants that are about what the code may
/// NOT contain (architecture E.1 #1, #2, #3, #8, #9).
///
/// Guards whose subject belongs to a later phase are armed now so the first offending
/// line ever written fails the build instead of being noticed in review.
/// </summary>
public sealed class ForbiddenConstructTests
{
    private const string ProcessRunnerFileName = "ProcessRunner.cs";
    private const string FileSystemGuardFileName = "FileSystemGuard.cs";
    private const string ShellPolicyFileName = "ShellExecutablePolicy.cs";
    private const string WorkspaceManagerFileName = "WorkspaceManager.cs";

    private const string SnapshotFileName = "WorkspaceManager.Snapshots.cs";

    /// <summary>The hard isolated workspace: a second implementation of the same rules.</summary>
    private const string HardWorkspaceFileName = "HardWorkspaceGateway.cs";

    /// <summary>
    /// Modules on the managed execution filesystem trust boundary. Later execution
    /// facing projects are added here as their phases land.
    /// </summary>
    private static readonly string[] ExecutionFacingModules =
        [
            "CoreX.Hub.Execution", "CoreX.Hub.Workspace", "CoreX.Hub.Verification",
            "CoreX.Hub.Context", "CoreX.Hub.Agents",
        ];

    private static readonly Regex ProcessLaunchPattern =
        new(@"\bProcess\s*\.\s*Start\b|\bnew\s+ProcessStartInfo\b", RegexOptions.CultureInvariant);

    [Fact]
    public void Nothing_starts_a_process_through_the_managed_api()
    {
        // E.1 #1 with no exemption left to grant. ProcessRunner used to be the one file
        // allowed to call Process.Start, because it was the single launch boundary; since
        // Phase 10B-1 it creates the child suspended through CreateProcessW instead, so the
        // managed launch has no legitimate caller anywhere and the ban is total.
        //
        // That matters beyond tidiness: Process.Start returns a child that is already
        // running, which is precisely the ordering that made a descendant able to escape the
        // Job Object. A reintroduction here would reintroduce the race.
        AssertNoMatch(
            ProcessLaunchPattern,
            allowedFileName: null,
            because: "Process.Start hands back an already running child; the Hub creates one suspended.");
    }

    [Fact]
    public void The_single_launch_boundary_is_still_the_process_runner()
    {
        // The complement of the ban: exactly one file drives the native launch, so "no
        // Process.Start anywhere" cannot be satisfied by having no launcher at all.
        var runner = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), ProcessRunnerFileName, StringComparison.Ordinal));

        Assert.NotNull(runner);
        Assert.Contains("NativeProcessLauncher.Launch", File.ReadAllText(runner!), StringComparison.Ordinal);

        var callers = RepoLayout.ProductionSourceFiles
            .Where(f => File.ReadAllText(f).Contains("NativeProcessLauncher.Launch", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([ProcessRunnerFileName], callers);
    }

    [Fact]
    public void No_shell_is_ever_used()
    {
        // E.1 #3, source half: every execution is an executable plus an argument vector.
        // The single exempt file is the policy that DEFINES the forbidden interpreters, so
        // naming a shell is allowed exactly where a shell is forbidden. The runtime half
        // lives in ProcessRunner and is asserted separately below.
        AssertNoMatch(
            new Regex(
                @"UseShellExecute\s*=\s*true|""cmd.exe""|""/bin/sh""|""bash""|""powershell""|""pwsh""",
                RegexOptions.CultureInvariant),
            allowedFileName: ShellPolicyFileName,
            because: "E.1 #3: no shell, ever.");
    }

    [Fact]
    public void The_shell_policy_exemption_is_not_vacuous()
    {
        var policy = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), ShellPolicyFileName, StringComparison.Ordinal));

        Assert.NotNull(policy);

        var text = File.ReadAllText(policy);
        Assert.Contains("ForbiddenExecutableBaseNames", text, StringComparison.Ordinal);
        Assert.Contains("ForbiddenExtensions", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_no_shell_guard_is_wired_into_the_process_runner()
    {
        // E.1 #3, runtime half. A clean source tree is not enough: the caller supplies the
        // executable, so the request boundary must classify and refuse a shell before any
        // process is created. This asserts the single launch point actually consults the
        // policy rather than merely being free of shell literals itself.
        var runner = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), ProcessRunnerFileName, StringComparison.Ordinal));

        Assert.NotNull(runner);

        var text = File.ReadAllText(runner);
        Assert.Contains("ShellExecutablePolicy.IsShellOrScript", text, StringComparison.Ordinal);

        var guardIndex = text.IndexOf("ShellExecutablePolicy.IsShellOrScript", StringComparison.Ordinal);

        // Phase 10B-1 replaced Process.Start with a suspended native creation, so the launch
        // point is now the call into the native launcher. The property is unchanged: a shell
        // is classified and refused before anything can be created.
        var launchIndex = text.IndexOf("NativeProcessLauncher.Launch", StringComparison.Ordinal);

        Assert.True(launchIndex > 0, "the launch point should still be in ProcessRunner");
        Assert.True(guardIndex < launchIndex, "the shell classification must precede process creation");
    }

    [Fact]
    public void Only_the_filesystem_guard_makes_managed_path_trust_decisions()
    {
        // E.1 #2, scoped as the Phase 2 brief directs: the invariant applies to the
        // managed execution filesystem trust boundary and every later execution facing
        // module, NOT to the accepted Phase 1 bootstrap (configuration discovery and the
        // SQLite data directory). Those are application startup paths, and routing them
        // through Execution would invert the accepted dependency direction.
        AssertNoMatch(
            new Regex(
                @"Path\s*\.\s*GetFullPath|Path\s*\.\s*GetRelativePath|ResolveLinkTarget|FileAttributes\s*\.\s*ReparsePoint",
                RegexOptions.CultureInvariant),
            allowedFileName: FileSystemGuardFileName,
            because: "E.1 #2: canonicalisation, containment and reparse trust belong to FileSystemGuard only.",
            scopeDirectories: ExecutionFacingModules);
    }

    [Fact]
    public void The_filesystem_guard_exemption_is_not_vacuous()
    {
        var guard = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), FileSystemGuardFileName, StringComparison.Ordinal));

        Assert.NotNull(guard);
        Assert.Contains("ResolveLinkTarget", File.ReadAllText(guard), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_trusted_workspace_authority_may_create_a_commit()
    {
        // E.1 #7-A: git commit is centralised. There are now two workspaces - the Windows one
        // and the hard isolated one - and the rule is unchanged: each has exactly one place that
        // commits and nothing else in production may. Both exemptions are proved non-vacuous.
        AssertNoMatch(
            new Regex(@"""commit""", RegexOptions.CultureInvariant),
            allowedFileName: WorkspaceManagerFileName,
            because: "E.1 #7-A: git commit belongs to a trusted workspace authority only.",
            alsoAllowedFileNames: [HardWorkspaceFileName]);
    }

    /// <summary>
    /// The hard workspace's exemption is real, and it commits as the Hub.
    ///
    /// The same shape as the Windows exemption beside it: the file really does create a result
    /// commit, and it does so with the Hub's identity rather than whoever happens to be running.
    /// </summary>
    [Fact]
    public void The_hard_commit_exemption_is_not_vacuous()
    {
        var gateway = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(
                Path.GetFileName(f), HardWorkspaceFileName, StringComparison.Ordinal));

        Assert.NotNull(gateway);

        var code = File.ReadAllText(gateway!);

        Assert.Contains("CreateResultCommitAsync", code, StringComparison.Ordinal);
        Assert.Contains("GitAsHubAsync", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_commit_exemption_is_not_vacuous()
    {
        var manager = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), WorkspaceManagerFileName, StringComparison.Ordinal));

        Assert.NotNull(manager);
        Assert.Contains("CreateResultCommitAsync", File.ReadAllText(manager), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_code_contains_no_publishing_path()
    {
        // Push, merge, pull, publish and deploy have no production code path at all. Push appears
        // only in test code, where it proves the blocking protections work.
        //
        // The hard workspace is the single exemption, and for one verb: it fetches the pinned
        // objects out of a local transfer artefact into the repository it has just created. That
        // artefact is a file on the environment's own disk, the repository has no remote, and the
        // test below keeps it that way.
        AssertNoMatch(
            new Regex(
                @"""push""|""merge""|""pull""|""fetch""|""clone""",
                RegexOptions.CultureInvariant),
            allowedFileName: null,
            because: "Phase 3 brief section 23: there is no production git publishing path.",
            alsoAllowedFileNames: [HardWorkspaceFileName]);
    }

    /// <summary>
    /// The one fetch in production reads a local file and can reach nothing.
    ///
    /// Worth pinning precisely, because fetch is the verb that would let a hard workspace pull
    /// from somewhere the Hub never handed it. Its source is the transfer artefact, and there is
    /// no remote to fall back on.
    /// </summary>
    [Fact]
    public void The_only_production_fetch_reads_a_local_transfer_artefact()
    {
        var gateway = File.ReadAllText(
            RepoLayout.ProductionSourceFiles.Single(
                f => string.Equals(Path.GetFileName(f), HardWorkspaceFileName, StringComparison.Ordinal)));

        var fetches = Regex.Matches(gateway, @"""fetch"",\s*(?<source>[A-Za-z]+)");

        Assert.Single(fetches);
        Assert.Equal("artefact", fetches[0].Groups["source"].Value);

        foreach (var forbidden in new[] { "http://", "https://", "git@", "ssh://", "\"origin\"" })
        {
            Assert.DoesNotContain(forbidden, gateway, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_only_git_protection_exemption_is_snapshot_disposal()
    {
        // A non vacuous exemption: the exempt file really does contain exactly one forcing
        // flag, it really is a worktree removal, and it is nothing else.
        var snapshots = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), SnapshotFileName, StringComparison.Ordinal));

        var text = File.ReadAllText(snapshots);

        var forcing = Assert.Single(
            text.Split('\n'),
            l => l.Contains("--force", StringComparison.Ordinal));

        Assert.Contains("\"worktree\", \"remove\", \"--force\"", forcing, StringComparison.Ordinal);

        Assert.DoesNotContain("--no-verify", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--force-with-lease", text, StringComparison.Ordinal);

        // Execution worktree release is deliberately not exempt and still never forces.
        var manager = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), WorkspaceManagerFileName, StringComparison.Ordinal));

        Assert.DoesNotContain("--force", File.ReadAllText(manager), StringComparison.Ordinal);
    }

    [Fact]
    public void The_publishing_guard_scans_production_only()
    {
        // The guard would be meaningless if the tests that prove push is blocked were
        // themselves scanned, so confirm the scanner only ever reads src/**.
        Assert.All(
            RepoLayout.ProductionSourceFiles,
            f => Assert.StartsWith(RepoLayout.SourceRoot, f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_hub_never_bypasses_its_own_git_protection()
    {
        // E.1 #8 (ADR-0005). Git code arrives in Phase 3.
        AssertNoMatch(
            new Regex(
                @"--no-verify|core\.hooksPath\s*=|--force\b|--force-with-lease",
                RegexOptions.CultureInvariant),
            allowedFileName: SnapshotFileName,
            because: "E.1 #8: the Hub must never bypass its own protection mechanism.");
    }

    [Fact]
    public void The_hub_never_rewrites_agent_created_git_history()
    {
        // E.1 #9 (errata E1). Evidence first; the worktree is preserved; cleanup is human.
        AssertNoMatch(
            new Regex(
                @"""reset""|""rebase""|""update-ref""|""--amend""|branch\s+-f\b",
                RegexOptions.CultureInvariant),
            allowedFileName: null,
            because: "E.1 #9: agent created commits are never repaired or deleted by the Hub.");
    }

    [Fact]
    public void Guards_actually_scan_something()
    {
        // Protects against a silently empty scan making every guard vacuously pass.
        Assert.NotEmpty(RepoLayout.ProductionSourceFiles);
        Assert.True(RepoLayout.ProductionSourceFiles.Count > 30);
    }

    private static void AssertNoMatch(
        Regex pattern,
        string? allowedFileName,
        string because,
        IReadOnlyList<string>? scopeDirectories = null,
        IReadOnlyList<string>? alsoAllowedFileNames = null)
    {
        var offenders = new List<string>();

        var files = scopeDirectories is null
            ? RepoLayout.ProductionSourceFiles
            : [.. RepoLayout.ProductionSourceFiles.Where(f => scopeDirectories.Any(d =>
                f.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.Ordinal)))];

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            if (allowedFileName is not null
                && string.Equals(fileName, allowedFileName, StringComparison.Ordinal))
            {
                continue;
            }

            // A second implementation of the same rule is named, never implied. Every entry here
            // has its own test proving the exemption is not vacuous.
            if (alsoAllowedFileNames is not null && alsoAllowedFileNames.Contains(fileName, StringComparer.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Comments describe the rule; only real code may violate it.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (pattern.IsMatch(line))
                {
                    offenders.Add($"{RepoLayout.Relative(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{because}{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }
}
