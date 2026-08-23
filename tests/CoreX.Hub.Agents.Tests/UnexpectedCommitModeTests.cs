using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Cli.Workspace;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The condition K1 will later have to detect: an agent that committed even though the
/// Hub never asked it to.
///
/// The commit is made by the fixture harness, exactly as an external coding agent would
/// make it. Production must never gain git policy or repair code to arrange this
/// (E.1 #5, E.1 #9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnexpectedCommitModeTests
{
    [Fact]
    public async Task The_unexpected_commit_condition_is_produced_by_the_fixture()
    {
        Assert.True(GitToolchain.TryResolve(out var git, out var diagnostic), diagnostic);

        using var fixture = ScriptedFixture.Create();
        InitialiseRepository(git!, fixture.WorkspaceRoot);

        var head = Capture(git!, fixture.WorkspaceRoot, "rev-parse", "HEAD");

        Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", git);

        try
        {
            var result = await fixture.Runtime(ScriptedAgentMode.UnexpectedCommit).RunAsync(
                fixture.Request(environmentAllowlist: ["SystemRoot", "TEMP", "TMP", "PATH", "COREX_FIXTURE_GIT"]),
                new EventCollector(),
                CancellationToken.None);

            Assert.Equal(AgentRunOutcome.Completed, result.Outcome);

            // The agent moved HEAD on its own.
            var afterwards = Capture(git!, fixture.WorkspaceRoot, "rev-parse", "HEAD");
            Assert.NotEqual(head, afterwards);

            var subject = Capture(git!, fixture.WorkspaceRoot, "log", "-1", "--format=%s");
            Assert.Equal("agent: unexpected commit", subject);

            // The runtime reports a completed run and assigns no policy verdict: naming
            // this a RuntimePolicyViolation is the Phase 5B gate's job.
            Assert.DoesNotContain("policy", result.Diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("violation", result.Diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // And nothing repaired history: the base commit is still reachable.
            var parent = Capture(git!, fixture.WorkspaceRoot, "rev-parse", "HEAD~1");
            Assert.Equal(head, parent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", null);
        }
    }

    private static void InitialiseRepository(string git, string path)
    {
        Run(git, path, "init", "-q", "-b", "main");
        Run(git, path, "config", "user.name", "Fixture");
        Run(git, path, "config", "user.email", "fixture@corex.local");

        File.WriteAllText(Path.Combine(path, "README.md"), "base\n");

        Run(git, path, "add", "-A");
        Run(git, path, "commit", "-q", "-m", "base");
    }

    private static void Run(string git, string workingDirectory, params string[] arguments) =>
        Assert.Equal(0, Execute(git, workingDirectory, arguments).ExitCode);

    private static string Capture(string git, string workingDirectory, params string[] arguments)
    {
        var result = Execute(git, workingDirectory, arguments);
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    /// <summary>
    /// Test harness only. Production still launches every process through ProcessRunner;
    /// this arranges the repository the scenario needs.
    /// </summary>
    private static (int ExitCode, string Output) Execute(
        string git,
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));

        return (process.ExitCode, output);
    }
}
