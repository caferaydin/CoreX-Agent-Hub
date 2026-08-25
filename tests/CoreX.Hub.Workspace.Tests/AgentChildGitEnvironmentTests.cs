using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Composition.Agents;
using CoreX.Hub.Execution.Processes;
using CoreX.Hub.Workspace.Abstractions;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The agent child's git configuration baseline.
///
/// Until this existed, the Hub's own git commands ignored the developer's system and
/// global configuration while a git the agent ran itself read both. The same worktree
/// therefore answered two different questions about credential helpers, line endings and
/// filter drivers, and only one of those answers had been inspected.
///
/// No real developer configuration is touched anywhere here: the "developer" file is a
/// fixture file, supplied to the child the way a leaking parent environment would supply
/// it, and the assertions are on what the child actually observed.
/// </summary>
[Collection(GitHelperCollection.Name)]
public sealed class AgentChildGitEnvironmentTests
{
    private const string GlobalConfigVariable = "GIT_CONFIG_GLOBAL";
    private const string NoSystemVariable = "GIT_CONFIG_NOSYSTEM";
    private const string ProbeDriver = "corexprobe";
    private const string FakeIdentity = "not-the-hub@example.invalid";

    private static readonly IReadOnlyList<string> FixtureAllowlist =
        ["SystemRoot", "windir", "TEMP", "TMP", "PATH", "PATHEXT"];

    /// <summary>
    /// Control. A parent supplied global configuration file really is effective for a git
    /// child that does not carry the baseline, so the protected test below is asserting
    /// something that would otherwise happen.
    /// </summary>
    [Fact]
    public async Task Control_a_parent_supplied_global_config_is_effective_without_the_baseline()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var developerConfig = WriteDeveloperConfig(fixture);

        var listed = await fixture.Executor.ExecuteAsync(
            new GitCommand
            {
                WorkingDirectory = prepared.Workspace!.WorkspacePath,
                Arguments = ["config", "--list", "--show-origin"],
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [NoSystemVariable] = "1",
                    [GlobalConfigVariable] = developerConfig,
                },
            },
            CancellationToken.None);

        Assert.True(listed.Succeeded, listed.Describe());
        Assert.Contains(ProbeDriver, listed.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FakeIdentity, listed.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autocrlf", listed.Stdout, StringComparison.OrdinalIgnoreCase);

        EvidenceLog.Write(
            "ControlAgentEnvironment: a parent supplied global config IS effective without the agent baseline");
    }

    [Fact]
    public async Task An_agent_child_cannot_be_given_the_developers_git_configuration()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        var developerConfig = WriteDeveloperConfig(fixture);

        // A runtime tries to hand the child the developer's configuration. The adapter
        // applies the baseline afterwards, so the attempt cannot take effect.
        var listed = await RunAsAgentChildAsync(
            fixture,
            workspace.WorkspacePath,
            GitFixture.ResolvedGitPath,
            ["config", "--list", "--show-origin"],
            GitEnvironment.InheritedAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalConfigVariable] = developerConfig,
                [NoSystemVariable] = "0",
            });

        Assert.Equal(0, listed.ExitCode);

        // The developer's file is not effective.
        Assert.DoesNotContain(ProbeDriver, listed.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakeIdentity, listed.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gpgsign", listed.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("autocrlf", listed.Stdout, StringComparison.OrdinalIgnoreCase);

        // Nor is anything else from outside the managed roots: whatever the machine happens
        // to carry in its system or global files, no origin may live outside the fixture.
        foreach (var origin in Origins(listed.Stdout))
        {
            Assert.StartsWith(
                Normalise(fixture.Root),
                Normalise(origin),
                StringComparison.OrdinalIgnoreCase);
        }

        EvidenceLog.Write(
            "Hardened(agent environment): a runtime supplied global config was overridden; "
            + "every effective config origin is inside the managed roots");
    }

    [Fact]
    public async Task An_agent_child_still_sees_the_managed_worktree_configuration()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        var hooks = await RunAsAgentChildAsync(
            fixture,
            workspace.WorkspacePath,
            GitFixture.ResolvedGitPath,
            ["config", "--get", GitConfigKeys.HooksPath],
            GitEnvironment.InheritedAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(0, hooks.ExitCode);
        Assert.Equal(
            Normalise(fixture.Manager.Hooks.HooksDirectory),
            Normalise(hooks.Stdout.Trim()));

        var pushUrl = await RunAsAgentChildAsync(
            fixture,
            workspace.WorkspacePath,
            GitFixture.ResolvedGitPath,
            ["config", "--get", GitConfigKeys.PushUrl],
            GitEnvironment.InheritedAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(0, pushUrl.ExitCode);
        Assert.Equal(GitConfigKeys.DisabledPushUrl, pushUrl.Stdout.Trim());

        EvidenceLog.Write(
            "Hardened(agent environment): worktree scoped hardening remains effective for the agent's own git");
    }

    [Fact]
    public async Task An_agent_child_observes_the_baseline_and_the_forced_hub_values()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var printed = await RunAsAgentChildAsync(
            fixture,
            prepared.Workspace!.WorkspacePath,
            GitFixture.MarkerExecutablePath,
            ["print-env"],
            FixtureAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [NoSystemVariable] = "0",
                ["GIT_TERMINAL_PROMPT"] = "1",
            });

        Assert.Equal(0, printed.ExitCode);
        Assert.Contains("ENV-END", printed.Stdout, StringComparison.Ordinal);

        var observed = ReadEnvironment(printed.Stdout);

        // The git baseline: the developer's system and global files are unreachable.
        Assert.Equal("1", observed[NoSystemVariable]);
        Assert.Equal(
            Normalise(fixture.Manager.Hooks.EmptyGlobalConfigPath),
            Normalise(observed[GlobalConfigVariable]));

        // And the Phase 2 forced values are still forced, for the child and everything it
        // starts: no interactive credential prompt and no askpass helper.
        Assert.Equal("0", observed["GIT_TERMINAL_PROMPT"]);
        Assert.EndsWith(
            ProcessRunner.DisabledAskPassFileName,
            observed["GIT_ASKPASS"],
            StringComparison.Ordinal);
        Assert.False(File.Exists(observed["GIT_ASKPASS"]));

        EvidenceLog.Write(
            "Hardened(agent environment): child observed GIT_CONFIG_NOSYSTEM=1, the Hub empty global config, "
            + "GIT_TERMINAL_PROMPT=0 and a disabled GIT_ASKPASS");
    }

    /// <summary>
    /// A file that stands in for the developer's own ~/.gitconfig. It is written inside the
    /// fixture and nothing outside it is read or modified.
    /// </summary>
    private static string WriteDeveloperConfig(GitFixture fixture)
    {
        var path = Path.Combine(fixture.Root, "developer.gitconfig");

        File.WriteAllText(
            path,
            string.Join(
                '\n',
                [
                    "[user]",
                    "\tname = Not The Hub",
                    "\temail = " + FakeIdentity,
                    "[commit]",
                    "\tgpgSign = true",
                    "[core]",
                    "\tautocrlf = true",
                    "[filter \"" + ProbeDriver + "\"]",
                    "\tclean = " + GitFixture.ToGitPath(GitFixture.MarkerExecutablePath),
                    string.Empty,
                ]));

        return path;
    }

    private static async Task<AgentProcessResult> RunAsAgentChildAsync(
        GitFixture fixture,
        string workingDirectory,
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> allowlist,
        IReadOnlyDictionary<string, string> environment)
    {
        // The production adapter, wired the way production wires it.
        var runner = new ProcessAgentRunner(fixture.Root, fixture.Manager.Hooks.EmptyGlobalConfigPath);

        var result = await runner.RunAsync(
            new AgentProcessRequest
            {
                Executable = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                EnvironmentAllowlist = allowlist,
                Environment = environment,
                HardTimeout = TimeSpan.FromSeconds(60),
            },
            CancellationToken.None);

        Assert.Equal(AgentProcessTermination.Exited, result.Termination);
        return result;
    }

    /// <summary>The file each effective configuration entry came from.</summary>
    private static IEnumerable<string> Origins(string showOrigin)
    {
        foreach (var raw in showOrigin.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (!line.StartsWith("file:", StringComparison.Ordinal))
            {
                continue;
            }

            var tab = line.IndexOf('\t', StringComparison.Ordinal);

            if (tab > "file:".Length)
            {
                yield return line["file:".Length..tab];
            }
        }
    }

    private static Dictionary<string, string> ReadEnvironment(string printed)
    {
        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in printed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                observed[line[..separator]] = line[(separator + 1)..];
            }
        }

        return observed;
    }

    private static string Normalise(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
}
