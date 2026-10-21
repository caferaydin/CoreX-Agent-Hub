using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Composition.Agents;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Which filesystem a scripted runtime's fixture path is a path on.
///
/// Found by the first real hard acceptance attempt, before an execution existed. A hard
/// organization's agent fixture lives inside its own environment, and the registry resolved every
/// declared path against this machine: <c>Path.GetFullPath("/opt/corex/agent")</c> became
/// <c>D:\opt\corex\agent</c>, <c>File.Exists</c> said no, and a correctly configured hard
/// organization could not declare a runtime at all.
///
/// The refusal was the harmless half. Had a file happened to sit at that Windows path, the value
/// handed to <see cref="CoreX.Hub.Agents.Abstractions.HardAgentCommand.Executable"/> would have been
/// a Windows path for a Linux workload to execute - the same shape as the verification allowlist
/// defect Checkpoint A closed, in the separate authority that owns agent runtimes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScriptedFixtureLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "corex-fixloc-" + Guid.CreateVersion7().ToString("N")[..12]);

    public ScriptedFixtureLocationTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(HostFixture, "a file that exists", new UTF8Encoding(false));
    }

    private string HostFixture => Path.Combine(_root, "fixture.exe");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// An isolated fixture is accepted without the host being asked about it, and reaches the
    /// runtime unchanged.
    ///
    /// Unchanged is the assertion that matters. A path normalised by this machine is a path this
    /// machine invented, and the environment that has to execute it would receive something no
    /// operator wrote.
    /// </summary>
    [Fact]
    public void An_isolated_fixture_path_is_not_resolved_against_this_machine()
    {
        const string guest = "/opt/corex-fixture/corex-agent-fixture";

        // The premise, stated rather than assumed: this is exactly what host resolution would do to
        // it, and neither half of that answer is about the machine that will run the fixture.
        Assert.Equal(
            Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory())!, "opt", "corex-fixture", "corex-agent-fixture"),
            Path.GetFullPath(guest));

        Assert.False(File.Exists(Path.GetFullPath(guest)));

        var runtimes = AgentRuntimeRegistry.Build(Config(guest, "Isolated"), _root, HostFixture);

        Assert.True(runtimes.ContainsKey("scripted-isolated"));
        Assert.Contains("scripted-isolated", AgentRuntimeRegistry.IsolatedRuntimeKeys(Config(guest, "Isolated")));
    }

    /// <summary>A host fixture keeps the check it has always had.</summary>
    [Fact]
    public void A_host_fixture_is_still_confirmed_to_exist()
    {
        var present = AgentRuntimeRegistry.Build(Config(HostFixture, "Host"), _root, HostFixture);
        Assert.True(present.ContainsKey("scripted-isolated"));

        var missing = Path.Combine(_root, "not-there.exe");

        var refused = Assert.Throws<HubConfigurationException>(
            () => AgentRuntimeRegistry.Build(Config(missing, "Host"), _root, HostFixture));

        Assert.Contains("does not exist", refused.Message, StringComparison.Ordinal);
        Assert.Empty(AgentRuntimeRegistry.IsolatedRuntimeKeys(Config(HostFixture, "Host")));
    }

    /// <summary>
    /// An isolated declaration that is not one path inside an environment is refused where it is
    /// written.
    ///
    /// The value becomes argv[0] of a workload in another operating system, so a Windows path, a
    /// relative name or a smuggled second argument are all configuration mistakes rather than things
    /// to discover at exec time.
    /// </summary>
    [Theory]
    [InlineData(@"C:\fixtures\agent.exe")]
    [InlineData(@"\\wsl$\distro\opt\agent")]
    [InlineData("corex-agent-fixture")]
    [InlineData("/opt/corex/agent --mode apply-patch")]
    public void An_isolated_fixture_must_be_one_absolute_guest_path(string declared)
    {
        var refused = Assert.Throws<HubConfigurationException>(
            () => AgentRuntimeRegistry.Build(Config(declared, "Isolated"), _root, HostFixture));

        Assert.Contains("Isolated", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A location nobody defined is refused rather than treated as one of the two.</summary>
    [Fact]
    public void An_unknown_fixture_location_is_refused()
    {
        var refused = Assert.Throws<HubConfigurationException>(
            () => AgentRuntimeRegistry.Build(Config(HostFixture, "Guest"), _root, HostFixture));

        Assert.Contains("Host, Isolated", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Declarations written before this field existed keep meaning what they meant.</summary>
    [Fact]
    public void An_unstated_location_means_host()
    {
        var config = Config(HostFixture, "Host");
        config.Agents.ScriptedRuntimes[0].FixtureLocation = string.Empty;

        Assert.True(AgentRuntimeRegistry.Build(config, _root, HostFixture).ContainsKey("scripted-isolated"));
        Assert.Empty(AgentRuntimeRegistry.IsolatedRuntimeKeys(config));
    }

    private static HubConfig Config(string fixturePath, string location) => new()
    {
        Workspace = new HubWorkspaceConfig { Root = Path.GetTempPath() },
        Agents = new HubAgentsConfig
        {
            ScriptedRuntimes =
            [
                new ScriptedRuntimeConfig
                {
                    Key = "scripted-isolated",
                    FixturePath = fixturePath,
                    FixtureLocation = location,
                    Mode = "apply-patch",
                },
            ],
        },
    };
}
