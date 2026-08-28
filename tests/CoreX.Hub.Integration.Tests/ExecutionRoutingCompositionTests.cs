using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// What a real composition routes an organization to, and what it refuses to route at all.
///
/// The two halves matter equally. A logical organization must be entirely untouched by hard
/// isolation existing - the whole point of a per-execution decision is that a capable machine does
/// not become a different product for everybody on it. And a hard organization that cannot be
/// contained must come out as a refusal rather than as a quieter execution.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExecutionRoutingCompositionTests
{
    /// <summary>
    /// A logical organization gets the ports it always had.
    ///
    /// Asserted on the composed object rather than on configuration, because the failure worth
    /// catching is a composition that decided globally: hard machinery shipping and quietly
    /// capturing organizations that never asked for it.
    /// </summary>
    [Fact]
    public async Task A_logical_organization_keeps_the_host_ports()
    {
        await using var fixture = await CompositionLab.CreateAsync();

        await using var composition = fixture.Compose(IsolationLevel.Logical);

        Assert.False(composition.IsHard);

        // The host verifier, which measures in a Windows snapshot through the Windows executor.
        Assert.IsType<VerificationGateway>(composition.Verifier);
    }

    /// <summary>
    /// The hard plane existing does not move a logical organization, however ready the machine is.
    ///
    /// The same composition twice: nothing about this decision reads global state, so composing a
    /// logical organization on a machine that could run hard executions produces exactly what it
    /// produced before hard executions were possible.
    /// </summary>
    [Fact]
    public async Task A_capable_machine_does_not_switch_the_hub_to_hard()
    {
        await using var fixture = await CompositionLab.CreateAsync();

        await using var first = fixture.Compose(IsolationLevel.Logical);
        await using var second = fixture.Compose(IsolationLevel.Logical);

        Assert.False(first.IsHard);
        Assert.False(second.IsHard);
    }

    /// <summary>
    /// A hard organization whose runtime cannot be contained is refused, and never composed.
    ///
    /// Machine independent by construction: on a machine with no environment the refusal is the
    /// plane's, and on one with an environment it is the runtime's. Both are refusals, which is the
    /// property - what must never happen is a composition coming back that runs the work on the
    /// host.
    /// </summary>
    [Fact]
    public async Task A_hard_organization_that_cannot_be_contained_is_refused()
    {
        await using var fixture = await CompositionLab.CreateAsync();

        var refused = Assert.Throws<HubConfigurationException>(() => fixture.Compose(IsolationLevel.Hard));

        Assert.True(
            refused.Message.Contains("cannot run inside a hard isolated environment", StringComparison.Ordinal)
            || refused.Message.Contains("no environment a hard organization could run in", StringComparison.Ordinal)
            || refused.Message.Contains("could not be reached", StringComparison.Ordinal),
            refused.Message);

        // Whatever the reason, it is not an invitation to run it anywhere weaker.
        Assert.DoesNotContain("falling back", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A verification profile naming a tool the environment map does not is caught by configuration
    /// alone.
    ///
    /// No machine is consulted, which is the point: this is the check that can run before an
    /// execution row exists, so a typo costs a refusal rather than a workspace, an agent turn and a
    /// result commit.
    /// </summary>
    [Fact]
    public void An_unmapped_verification_tool_is_a_configuration_refusal()
    {
        var profile = HardProfile(("dotnet", "/usr/bin/dotnet"));

        Assert.Empty(HardVerificationExecutableResolver.MissingMappings(profile, ["dotnet"]));

        Assert.Equal(
            ["node", "pytest"],
            HardVerificationExecutableResolver.MissingMappings(profile, ["dotnet", "pytest", "node", "pytest"]));
    }

    /// <summary>Names are matched the way the host allowlist matches its own.</summary>
    [Fact]
    public void Tool_names_are_matched_case_insensitively()
    {
        var profile = HardProfile(("dotnet", "/usr/bin/dotnet"));

        Assert.Empty(HardVerificationExecutableResolver.MissingMappings(profile, ["DotNet"]));
    }

    private static HardIsolationProfile HardProfile(params (string Name, string Path)[] executables)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = "corex-fixture",
            AgentUser = "corex-agent",
            ServiceUser = "corex-service",
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = "/srv/corex/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
            VerificationExecutables = executables.ToDictionary(
                e => e.Name, e => e.Path, StringComparer.OrdinalIgnoreCase),
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);

        return profile!;
    }

    /// <summary>A real session and a real configuration, with nothing pointing at a live provider.</summary>
    private sealed class CompositionLab : IAsyncDisposable
    {
        private readonly TempHub _hub;
        private readonly HubSession _session;
        private readonly string _root;

        private CompositionLab(TempHub hub, HubSession session, HubConfig config, string root)
        {
            _hub = hub;
            _session = session;
            _root = root;
            Config = config;
        }

        public HubConfig Config { get; }

        public static async Task<CompositionLab> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "corex-route-" + Guid.CreateVersion7().ToString("N")[..12]);
            Directory.CreateDirectory(root);

            var tool = Path.Combine(root, "installed-tool.exe");
            File.WriteAllText(tool, "path that exists", new UTF8Encoding(false));

            // A trust root has to exist before it can be canonicalised, which is what the path guard
            // refuses to do for a directory that is not there.
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            Directory.CreateDirectory(Path.Combine(root, "org", "repo"));

            var hub = TempHub.Create();

            var session = await HubSession.OpenAsync(
                hub.DataDirectory, hub.ConfigPath, TextWriter.Null, CancellationToken.None);

            var config = new HubConfig
            {
                Workspace = new HubWorkspaceConfig { Root = Path.Combine(root, "workspace") },
                Verification = new HubVerificationConfig { Executables = { ["dotnet"] = tool } },
                Agents = new HubAgentsConfig
                {
                    ClaudeCodeRuntimes =
                    [
                        new ClaudeCodeRuntimeConfig { Key = "claude-code", ExecutablePath = tool },
                    ],
                },
                Organizations =
                [
                    new OrganizationConfig
                    {
                        Slug = "route-lab",
                        HardIsolation = new HardIsolationConfig
                        {
                            Backend = "Wsl2",
                            EnvironmentId = "corex-route-lab",
                            AgentUser = "corex-agent",
                            ServiceUser = "corex-service",
                            ExecutionRoot = "/srv/corex/exec",
                            EvidenceRoot = "/srv/corex/evidence",
                            ProcessLimit = 64,
                            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
                            VerificationExecutables = { ["dotnet"] = "/usr/bin/dotnet" },
                        },
                    },
                ],
            };

            return new CompositionLab(hub, session, config, root);
        }

        /// <summary>
        /// Composes for one isolation level.
        ///
        /// The agent profile names the vendor CLI runtime deliberately: it is exactly the runtime
        /// that cannot be split into "describe a command" and "interpret a result", because it owns
        /// its own process launch - which is what makes it the honest subject for the refusal.
        /// </summary>
        public ExecutionComposition Compose(IsolationLevel level)
        {
            var organization = new Organization(
                Guid.CreateVersion7(),
                Slug.Parse("route-lab"),
                "Route Lab",
                Path.Combine(_root, "org"),
                level,
                false,
                ["PATH", "SystemRoot"],
                DateTimeOffset.UnixEpoch);

            var repository = new Repository(
                Guid.CreateVersion7(),
                organization.Id,
                Guid.CreateVersion7(),
                Slug.Parse("repo"),
                "repo",
                Path.Combine(_root, "org", "repo"),
                "main",
                ["main"],
                DateTimeOffset.UnixEpoch);

            var agent = new AgentProfile(
                Guid.CreateVersion7(),
                organization.Id,
                "implementer",
                AgentRole.Implementer,
                "ClaudeCode",
                "claude-code",
                AgentPermissionProfile.WorkspaceWrite,
                "claude-opus-5",
                "high",
                900,
                1200,
                1800,
                DateTimeOffset.UnixEpoch);

            return ExecutionComposition.Create(
                _session, Config, organization, repository, null, agent, ReviewerSelection.None,
                TimeProvider.System);
        }

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
            _hub.Dispose();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
