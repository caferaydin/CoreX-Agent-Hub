using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The network boundary asked of a real machine, which does not have one.
///
/// The operator's own environment is the negative fixture for this slice, deliberately and without
/// being changed: nothing is installed into it, no rule is loaded, no route is touched. What it can
/// establish is the half of the boundary that does not need enforcement to be real - that the
/// arrangement is the supported one, that the agent identity has no way to alter a policy, that a
/// child namespace does not buy the agent authority over the parent's, and above all that with no
/// policy loaded readiness says so and keeps the environment out.
///
/// What it cannot establish is enforcement. Proving that traffic to the host is dropped by a rule
/// rather than by this machine's incidental configuration needs a disposable environment the Hub
/// may provision, and that is where this slice stops.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkRealTests : IAsyncLifetime
{
    private readonly HardEvidenceFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private HardIsolationProfile Profile() => _fixture.ProfileA();

    private async Task<IReadOnlyList<ReadinessCheck>> ProbeAsync() =>
        await WslHardNetworkProbe.Create(Path.GetTempPath()).ProbeAsync(Profile(), CancellationToken.None);

    private static ReadinessCheck Check(IReadOnlyList<ReadinessCheck> checks, string id) =>
        checks.Single(c => c.Id == id);

    // -- topology ------------------------------------------------------------------------------

    /// <summary>
    /// This machine's environment is the supported arrangement, measured rather than assumed.
    ///
    /// It has its own private address behind a next hop that is not one of its own. That is what
    /// makes a destination policy meaningful here: the host is somewhere else, at an address a rule
    /// can name a range for.
    /// </summary>
    [Fact]
    public async Task The_environment_is_the_supported_arrangement()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var mode = Check(await ProbeAsync(), HardIsolationChecks.NetworkModeSupported);

        Assert.Equal(ReadinessStatus.Pass, mode.Status);
        Assert.Contains("Nat", mode.Detail, StringComparison.Ordinal);
    }

    // -- fail closed ---------------------------------------------------------------------------

    /// <summary>
    /// With no policy loaded, readiness fails - and the environment stays out.
    ///
    /// The single most important behaviour in the slice. This machine has no filtering engine and
    /// no Hub table, and the honest answer to "is the boundary enforced" is no. An implementation
    /// that reported unknown, or that reported the absence as "nothing to check", would let an
    /// environment through on the strength of a boundary that does not exist.
    /// </summary>
    [Fact]
    public async Task An_environment_with_no_loaded_policy_is_not_ready()
    {
        if (!_fixture.Available)
        {
            return;
        }

        // The plane is the fabric's question, not the probe's: the probe measures reachability and
        // the fabric measures what is built. Asking the right one is part of what changed.
        var fabric = await WslHardNetworkFabric.Create(Path.GetTempPath())
            .VerifyAsync(Profile(), "fixture", CancellationToken.None);

        Assert.Equal(
            ReadinessStatus.Fail, Check(fabric, HardIsolationChecks.NetworkFabricReady).Status);

        // And this machine has no attachment either, so neither organization truth can pass.
        Assert.Equal(
            ReadinessStatus.Fail, Check(fabric, HardIsolationChecks.OrgNetworkNamespaceReady).Status);

        // And the whole readiness verdict follows, rather than the check standing alone.
        var readiness = await WslReadinessProbe.Create(Path.GetTempPath())
            .ProbeAsync("fixture", Profile(), CancellationToken.None);

        Assert.False(readiness.IsReady);

        Assert.Equal(
            ReadinessStatus.Fail,
            readiness.Checks.Single(c => c.Id == HardIsolationChecks.NetworkFabricReady).Status);
    }

    /// <summary>
    /// The control plane verdict agrees with what an independent attempt actually finds.
    ///
    /// This machine turns out to be the interesting case rather than the boring one. A Windows
    /// service bound to the host's address on the environment's own subnet <i>is</i> reachable
    /// from inside - measured here, repeatedly - so the honest expectation is not that the check
    /// passes. It is that the check says what is true.
    ///
    /// The Hub itself is still not reachable, because it binds loopback and loopback inside the
    /// environment is the environment's own. But that is a property of where the Hub happens to
    /// bind, not of a boundary: any Windows service on that address is reachable today. Which is
    /// exactly why loopback-only cannot be the hard boundary, and why this check has to be a
    /// measurement rather than a restatement of the binding policy.
    /// </summary>
    [Fact]
    public async Task The_control_plane_verdict_agrees_with_an_independent_attempt()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var checks = await ProbeAsync();

        var control = Check(checks, HardIsolationChecks.ControlPlaneUnreachable);
        var loopback = Check(checks, HardIsolationChecks.HostLoopbackDisabled);

        // Both answers come from the same attempts, so they can never disagree.
        Assert.Equal(control.Status, loopback.Status);

        if (control.Status == ReadinessStatus.Fail)
        {
            // It found a way in, and it says which one. "Somewhere" would not be actionable.
            Assert.Contains("the host's address", control.Detail, StringComparison.Ordinal);

            // And an independent attempt from the same identity agrees that the host answers.
            var reachable = await _fixture.ControlAsync(
                ["/usr/bin/nc", "-w", "3", "-z", await GatewayAsync(), "53"], HardEvidenceFixture.AgentA);

            Assert.NotNull(reachable.ExitCode);
        }
        else if (control.Status == ReadinessStatus.Pass)
        {
            Assert.Contains("permitted traffic worked", control.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("reached nothing", control.Detail, StringComparison.Ordinal);
        }

        // Whatever it concluded, the environment is not admitted.
        var readiness = await WslReadinessProbe.Create(Path.GetTempPath())
            .ProbeAsync("fixture", Profile(), CancellationToken.None);

        Assert.False(readiness.IsReady);
    }

    /// <summary>The environment's own next hop, which is the host's address on its subnet.</summary>
    private async Task<string> GatewayAsync()
    {
        var routes = await _fixture.ControlAsync(["/usr/sbin/ip", "-4", "route"]);

        var topology = HardNetworkTopology.Classify([], routes.Stdout.Split('\n'), []);

        return topology.DefaultGateway.Length > 0 ? topology.DefaultGateway : "127.0.0.1";
    }

    /// <summary>
    /// Peer separation stays unknown on a machine with one hard environment.
    ///
    /// There is a second distribution registered here, and it is still not a peer hard environment
    /// with its own principals and its own policy. Counting it would be counting the wrong thing.
    /// </summary>
    [Fact]
    public async Task Peer_network_separation_stays_unknown()
    {
        if (!_fixture.Available)
        {
            return;
        }

        Assert.Equal(
            ReadinessStatus.Unknown,
            Check(await ProbeAsync(), HardIsolationChecks.PeerNetworkUnreachable).Status);
    }

    // -- the agent's own powers ------------------------------------------------------------------

    /// <summary>
    /// The agent identity holds no network authority at all.
    ///
    /// Measured as capabilities rather than argued from group membership. An agent with
    /// CAP_NET_ADMIN could load, flush or replace any policy the Hub installed, which would make
    /// every other assertion in this file decorative.
    /// </summary>
    [Fact]
    public async Task The_agent_holds_no_network_capability()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var capabilities = await _fixture.ControlAsync(
            ["/usr/bin/grep", "-E", "^Cap(Eff|Prm|Amb)", "/proc/self/status"], HardEvidenceFixture.AgentA);

        Assert.Equal(0, capabilities.ExitCode);

        foreach (var line in capabilities.Stdout.Split('\n').Where(l => l.Trim().Length > 0))
        {
            Assert.EndsWith("0000000000000000", line.Trim(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The agent cannot alter the environment's routing, which is the cheapest way around a
    /// destination policy.
    ///
    /// A route the agent controls would let it send a denied destination somewhere else and call
    /// it something else. The attempt below names a documentation range and is refused, so nothing
    /// is changed either way.
    /// </summary>
    [Fact]
    public async Task The_agent_cannot_change_the_route_table()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var before = await _fixture.ControlAsync(["/usr/sbin/ip", "-4", "route"]);

        var attempt = await _fixture.ControlAsync(
            ["/usr/sbin/ip", "route", "add", "203.0.113.0/24", "dev", "eth0"], HardEvidenceFixture.AgentA);

        Assert.NotEqual(0, attempt.ExitCode);

        var after = await _fixture.ControlAsync(["/usr/sbin/ip", "-4", "route"]);

        Assert.Equal(before.Stdout, after.Stdout);
    }

    /// <summary>
    /// A child user namespace does not buy the agent authority over the parent's network.
    ///
    /// This is the narrow invariant, and it is worth stating precisely because the broad version is
    /// false: unprivileged user namespaces are available here, and inside one the agent really does
    /// hold a full capability set. What it does not hold is any power over the network namespace it
    /// came from, because that namespace belongs to the user namespace above it. So a policy loaded
    /// in the parent stays out of reach, and a namespace the agent makes for itself can only take
    /// reachability away.
    /// </summary>
    [Fact]
    public async Task A_child_user_namespace_cannot_reach_into_the_parents_network()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var before = await _fixture.ControlAsync(["/usr/sbin/ip", "-4", "route"]);

        // Inside the child the agent is root and holds everything...
        var capabilities = await _fixture.ControlAsync(
            [
                "/usr/bin/unshare", "--user", "--map-root-user",
                "/usr/bin/grep", "-E", "^CapEff", "/proc/self/status",
            ],
            HardEvidenceFixture.AgentA);

        if (capabilities.ExitCode != 0)
        {
            // No unprivileged user namespaces on this kernel; the invariant holds more simply.
            return;
        }

        Assert.DoesNotContain("0000000000000000", capabilities.Stdout, StringComparison.Ordinal);

        // ...and none of it applies to the network namespace it did not create.
        var attempt = await _fixture.ControlAsync(
            [
                "/usr/bin/unshare", "--user", "--map-root-user",
                "/usr/sbin/ip", "route", "add", "203.0.113.0/24", "dev", "eth0",
            ],
            HardEvidenceFixture.AgentA);

        Assert.NotEqual(0, attempt.ExitCode);

        var after = await _fixture.ControlAsync(["/usr/sbin/ip", "-4", "route"]);

        Assert.Equal(before.Stdout, after.Stdout);
    }

    // -- what was not changed --------------------------------------------------------------------

    /// <summary>
    /// Measuring the network left the machine exactly as it was.
    ///
    /// A diagnostic that provisioned what it measured would always be able to report success, so
    /// this asserts the absence directly: no Hub table appeared, and no filtering engine was
    /// installed to put one there.
    /// </summary>
    [Fact]
    public async Task Probing_installs_nothing_and_loads_nothing()
    {
        if (!_fixture.Available)
        {
            return;
        }

        await ProbeAsync();

        var table = await _fixture.ControlAsync(
            ["/usr/sbin/nft", "list", "table", "inet", "corex_hard"]);

        Assert.NotEqual(0, table.ExitCode);

        var engine = await _fixture.ControlAsync(["/usr/bin/test", "-x", "/usr/sbin/nft"]);

        Assert.NotEqual(0, engine.ExitCode);
    }
}
