using System.Text.RegularExpressions;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Source level guards for the architecture invariants that are about what the code may
/// NOT contain (architecture E.1 #1, #3, #8, #9).
///
/// Several of these guards have no subject yet because their components belong to later
/// phases. They are armed now so that the first offending line ever written fails the
/// build rather than being noticed in review. The implementation report classifies each
/// one as PASS or NOT_APPLICABLE_UNTIL_PHASE_X.
/// </summary>
public sealed class ForbiddenConstructTests
{
    [Fact]
    public void Only_the_process_runner_may_start_processes()
    {
        // E.1 #1. ProcessRunner arrives in Phase 2; until then the guard must find nothing.
        AssertNoMatch(
            new Regex(@"\bProcess\s*\.\s*Start\b|\bnew\s+ProcessStartInfo\b", RegexOptions.CultureInvariant),
            allowedFileName: "ProcessRunner.cs",
            because: "E.1 #1: process creation belongs to ProcessRunner only.");
    }

    [Fact]
    public void No_shell_is_ever_used()
    {
        // E.1 #3: every execution is an executable plus an argument vector.
        AssertNoMatch(
            new Regex(@"UseShellExecute\s*=\s*true|""cmd\.exe""|""/bin/sh""|""powershell""|""pwsh""",
                RegexOptions.CultureInvariant),
            allowedFileName: null,
            because: "E.1 #3: no shell, ever.");
    }

    [Fact]
    public void The_hub_never_bypasses_its_own_git_protection()
    {
        // E.1 #8 (ADR-0005). Git code arrives in Phase 3.
        AssertNoMatch(
            new Regex(@"--no-verify|core\.hooksPath\s*=|--force\b|--force-with-lease",
                RegexOptions.CultureInvariant),
            allowedFileName: null,
            because: "E.1 #8: the Hub must never bypass its own protection mechanism.");
    }

    [Fact]
    public void The_hub_never_rewrites_agent_created_git_history()
    {
        // E.1 #9 (errata E1). Evidence first; the worktree is preserved; cleanup is human.
        AssertNoMatch(
            new Regex(@"""reset""|""rebase""|""update-ref""|""--amend""|branch\s+-f\b",
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

    private static void AssertNoMatch(Regex pattern, string? allowedFileName, string because)
    {
        var offenders = new List<string>();

        foreach (var file in RepoLayout.ProductionSourceFiles)
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
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
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

        Assert.True(offenders.Count == 0, $"{because}{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }
}
