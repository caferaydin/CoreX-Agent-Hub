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

    /// <summary>
    /// Modules on the managed execution filesystem trust boundary. Later execution
    /// facing projects are added here as their phases land.
    /// </summary>
    private static readonly string[] ExecutionFacingModules =
        ["CoreX.Hub.Execution", "CoreX.Hub.Workspace", "CoreX.Hub.Verification"];

    private static readonly Regex ProcessLaunchPattern =
        new(@"\bProcess\s*\.\s*Start\b|\bnew\s+ProcessStartInfo\b", RegexOptions.CultureInvariant);

    [Fact]
    public void Only_the_process_runner_may_start_processes()
    {
        // E.1 #1: process creation belongs to ProcessRunner and nowhere else.
        AssertNoMatch(
            ProcessLaunchPattern,
            allowedFileName: ProcessRunnerFileName,
            because: "E.1 #1: process creation belongs to ProcessRunner only.");
    }

    [Fact]
    public void The_process_runner_exemption_is_not_vacuous()
    {
        // A guard whose single exempt file contains nothing would pass forever without
        // enforcing anything. Phase 2 makes the exemption real, so assert it is used.
        var runner = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), ProcessRunnerFileName, StringComparison.Ordinal));

        Assert.NotNull(runner);
        Assert.Matches(ProcessLaunchPattern, File.ReadAllText(runner));
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
        var launchIndex = text.IndexOf("process.Start()", StringComparison.Ordinal);

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
    public void Only_the_workspace_manager_may_create_a_commit()
    {
        // E.1 #7-A: git commit is centralised. Whether a commit is permitted at all - the
        // K0..K3 gates - is Phase 5 and is deliberately not implemented yet.
        AssertNoMatch(
            new Regex(@"""commit""", RegexOptions.CultureInvariant),
            allowedFileName: WorkspaceManagerFileName,
            because: "E.1 #7-A: git commit belongs to WorkspaceManager.CreateResultCommit only.");
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
        // Push, merge, pull, fetch, publish and deploy have no production code path at all.
        // Push appears only in test code, where it proves the blocking protections work.
        AssertNoMatch(
            new Regex(
                @"""push""|""merge""|""pull""|""fetch""|""clone""",
                RegexOptions.CultureInvariant),
            allowedFileName: null,
            because: "Phase 3 brief section 23: there is no production git publishing path.");
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
            allowedFileName: null,
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
        IReadOnlyList<string>? scopeDirectories = null)
    {
        var offenders = new List<string>();

        var files = scopeDirectories is null
            ? RepoLayout.ProductionSourceFiles
            : [.. RepoLayout.ProductionSourceFiles.Where(f => scopeDirectories.Any(d =>
                f.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.Ordinal)))];

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            if (allowedFileName is not null
                && string.Equals(Path.GetFileName(file), allowedFileName, StringComparison.Ordinal))
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
