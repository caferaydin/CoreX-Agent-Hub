using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// What readiness says about the network, and what it refuses to say.
///
/// The whole value of these checks is that they fail closed. A network verdict is the one place
/// where a wrong Pass is not a bug that shows up later - it is an untrusted workload with a route
/// to the Hub, and nothing downstream would notice. So every case here is about the answer being
/// worse than Pass: an environment nobody asked about, a policy that is not loaded, a peer that
/// does not exist to be unreachable, a silent network that refuses everything including what it
/// is supposed to permit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkReadinessTests
{
    private static readonly string[] NetworkChecks =
    [
        HardIsolationChecks.NetworkModeSupported,
        HardIsolationChecks.NetworkPolicyEnforced,
        HardIsolationChecks.HostLoopbackDisabled,
        HardIsolationChecks.ProxyNotInherited,
        HardIsolationChecks.PeerNetworkUnreachable,
    ];

    private static HardIsolationProfile Profile()
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = "corex-fixture",
            AgentUser = "corex-agent",
            ServiceUser = "corex-hub",
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = "/srv/corex/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    /// <summary>An environment that answers the topology questions the way this machine does.</summary>
    private static ScriptedExecutionEnvironment Environment(bool withPolicy)
    {
        var script = new ScriptedExecutionEnvironment();

        script.Answer(
            "/usr/sbin/ip -o -4 addr",
            "1: lo inet 127.0.0.1/8 scope host lo\n"
            + "1: lo inet 10.255.255.254/32 scope global lo\n"
            + "2: eth0 inet 192.168.66.185/20 scope global eth0");

        script.Answer(
            "/usr/sbin/ip -4 route",
            "default via 192.168.64.1 dev eth0\n192.168.64.0/20 dev eth0 scope link src 192.168.66.185");

        script.Answer("/usr/bin/cat /etc/resolv.conf", "nameserver 10.255.255.254");

        script.Answer("/usr/bin/printenv", "PATH=/usr/bin\nHOME=/tmp");

        if (withPolicy)
        {
            script.AnswerPrefix("/usr/sbin/nft list table ", RenderedPolicy());
        }

        return script;
    }

    /// <summary>The policy this build renders for that environment, as nft would print it back.</summary>
    private static string RenderedPolicy()
    {
        var topology = HardNetworkTopology.Classify(
            ["2: eth0 inet 192.168.66.185/20 scope global eth0"],
            ["default via 192.168.64.1 dev eth0"],
            ["nameserver 10.255.255.254"]);

        Assert.True(NftablesRuleset.TryRender(HardNetworkPolicy.Phase10, topology, out var ruleset, out _));

        return string.Join(
            '\n',
            ruleset!.Text
                .Split('\n')
                .Where(l => !l.StartsWith("delete table", StringComparison.Ordinal))
                .Where(l => l.Trim() != "table inet corex_hard {}"));
    }

    private static async Task<IReadOnlyList<ReadinessCheck>> ProbeAsync(ScriptedExecutionEnvironment script) =>
        await WslHardNetworkProbe.Scripted(script).ProbeAsync(Profile(), CancellationToken.None);

    private static ReadinessCheck Check(IReadOnlyList<ReadinessCheck> checks, string id) =>
        checks.Single(c => c.Id == id);

    // -- topology ------------------------------------------------------------------------------

    /// <summary>The supported arrangement is recognised from the machine, not from a config key.</summary>
    [Fact]
    public void A_private_subnet_behind_a_gateway_is_the_supported_arrangement()
    {
        var topology = HardNetworkTopology.Classify(
            ["2: eth0 inet 192.168.66.185/20 scope global eth0"],
            ["default via 192.168.64.1 dev eth0"],
            ["nameserver 10.255.255.254"]);

        Assert.Equal(HardNetworkMode.Nat, topology.Mode);
        Assert.Equal("192.168.64.1", topology.DefaultGateway);
        Assert.Equal("10.255.255.254", topology.DnsEndpoint);
    }

    /// <summary>
    /// An environment with no gateway of its own is the shared-interface arrangement.
    ///
    /// Unsupported rather than unusual: with the host's own interfaces the control plane sits at
    /// an address no destination rule denies, so the policy would be absent rather than weaker.
    /// </summary>
    [Fact]
    public void An_environment_with_no_gateway_is_unsupported()
    {
        var topology = HardNetworkTopology.Classify(
            ["2: eth0 inet 192.168.0.101/24 scope global eth0"],
            ["192.168.0.0/24 dev eth0 scope link"],
            ["nameserver 127.0.0.53"]);

        Assert.Equal(HardNetworkMode.Mirrored, topology.Mode);
    }

    /// <summary>A gateway that is one of the environment's own addresses is the same shape.</summary>
    [Fact]
    public void An_environment_that_is_its_own_gateway_is_unsupported()
    {
        var topology = HardNetworkTopology.Classify(
            ["2: eth0 inet 192.168.0.101/24 scope global eth0"],
            ["default via 192.168.0.101 dev eth0"],
            []);

        Assert.Equal(HardNetworkMode.Mirrored, topology.Mode);
    }

    /// <summary>Nothing measurable is Unknown, and Unknown is never ready.</summary>
    [Fact]
    public void An_environment_that_reports_nothing_is_unknown()
    {
        var topology = HardNetworkTopology.Classify([], [], []);

        Assert.Equal(HardNetworkMode.Unknown, topology.Mode);
    }

    // -- the probe -----------------------------------------------------------------------------

    /// <summary>Every network check is answered, so none of them can be quietly missing.</summary>
    [Fact]
    public async Task Every_network_check_is_answered()
    {
        var checks = await ProbeAsync(Environment(withPolicy: false));

        Assert.Equal(
            NetworkChecks.Order(StringComparer.Ordinal),
            checks.Select(c => c.Id).Where(NetworkChecks.Contains).Order(StringComparer.Ordinal));

        // Including the control plane, which the network has its own answer for.
        Assert.Contains(checks, c => c.Id == HardIsolationChecks.ControlPlaneUnreachable);
    }

    /// <summary>
    /// A policy that is not loaded is a failure, not an absence.
    ///
    /// The environment in this case has no policy engine and no table. Reporting that as unknown
    /// would be defensible and wrong: nothing is filtering, and that is a fact, not a gap.
    /// </summary>
    [Fact]
    public async Task An_environment_with_no_loaded_policy_fails()
    {
        var checks = await ProbeAsync(Environment(withPolicy: false));

        Assert.Equal(
            ReadinessStatus.Fail,
            Check(checks, HardIsolationChecks.NetworkPolicyEnforced).Status);
    }

    /// <summary>
    /// A loaded policy that is the expected one passes, which is what makes the failure above mean
    /// something.
    ///
    /// Without this the check would be satisfied by an implementation that failed unconditionally.
    /// </summary>
    [Fact]
    public async Task An_environment_with_the_expected_policy_passes_that_check()
    {
        var checks = await ProbeAsync(Environment(withPolicy: true));

        var policy = Check(checks, HardIsolationChecks.NetworkPolicyEnforced);

        Assert.Equal(ReadinessStatus.Pass, policy.Status);
    }

    /// <summary>
    /// A table that is loaded but is not the Hub's policy fails.
    ///
    /// The worst of the three states. Nothing short of comparing the contents would notice: the
    /// table exists, it is named correctly, and every lighter check would report enforcement.
    /// </summary>
    [Fact]
    public async Task A_table_that_is_not_the_expected_policy_fails()
    {
        var script = Environment(withPolicy: false);

        script.AnswerPrefix(
            "/usr/sbin/nft list table ",
            RenderedPolicy().Replace("ip daddr 10.0.0.0/8 drop", "ip daddr 10.0.0.0/8 accept", StringComparison.Ordinal));

        var checks = await ProbeAsync(script);

        Assert.Equal(
            ReadinessStatus.Fail,
            Check(checks, HardIsolationChecks.NetworkPolicyEnforced).Status);
    }

    /// <summary>
    /// A shared-interface environment fails the mode check, and the probe is what says so.
    ///
    /// The classification is tested on its own elsewhere; this is the wiring. A probe that
    /// classified correctly and then reported the answer as acceptable would leave the
    /// classification doing nothing at all.
    /// </summary>
    [Fact]
    public async Task A_shared_interface_environment_fails_the_mode_check()
    {
        var script = Environment(withPolicy: false);

        // The host's own address, and no gateway of its own.
        script.Answer("/usr/sbin/ip -o -4 addr", "2: eth0 inet 192.168.0.101/24 scope global eth0");
        script.Answer("/usr/sbin/ip -4 route", "192.168.0.0/24 dev eth0 scope link");

        var checks = await ProbeAsync(script);

        var mode = Check(checks, HardIsolationChecks.NetworkModeSupported);

        Assert.Equal(ReadinessStatus.Fail, mode.Status);

        // And nothing is rendered for it, so the policy check cannot pass either.
        Assert.NotEqual(
            ReadinessStatus.Pass,
            Check(checks, HardIsolationChecks.NetworkPolicyEnforced).Status);
    }

    /// <summary>
    /// An environment that cannot be classified fails closed too.
    ///
    /// Unknown rather than Fail, because nothing was established - and unknown is not ready, which
    /// is the only property that matters here.
    /// </summary>
    [Fact]
    public async Task An_environment_that_reports_nothing_leaves_the_mode_unknown()
    {
        var script = Environment(withPolicy: false);

        script.Answer("/usr/sbin/ip -o -4 addr", string.Empty);
        script.Answer("/usr/sbin/ip -4 route", string.Empty);

        Assert.Equal(
            ReadinessStatus.Unknown,
            Check(await ProbeAsync(script), HardIsolationChecks.NetworkModeSupported).Status);
    }

    /// <summary>
    /// An agent that reaches the Hub fails the control plane check, and fails it loudly.
    ///
    /// The one case the whole probe exists for, and the one no real machine here can produce: this
    /// environment is scripted to hand back the Hub's own marker, which is what an agent would see
    /// if the host were reachable. Without this test the verdict could be inverted and every other
    /// assertion in the file would still hold.
    /// </summary>
    [Fact]
    public async Task An_agent_that_reaches_the_hub_fails_the_control_plane_check()
    {
        var script = Environment(withPolicy: true);

        // Whatever the agent is asked to reach, it gets the Hub's marker back.
        script.AnswerPrefix("/usr/bin/nc ", "corex-hard-network-probe-marker");

        var checks = await ProbeAsync(script);

        var control = Check(checks, HardIsolationChecks.ControlPlaneUnreachable);
        var loopback = Check(checks, HardIsolationChecks.HostLoopbackDisabled);

        Assert.Equal(ReadinessStatus.Fail, control.Status);
        Assert.Equal(ReadinessStatus.Fail, loopback.Status);

        // And the diagnostic says which way in was open, because "somewhere" is not actionable.
        Assert.Contains("loopback", control.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An agent that reaches only what the policy permits passes, which is the control.
    ///
    /// Without it the failure above would also be produced by an implementation that reported Fail
    /// whenever anything answered at all.
    /// </summary>
    [Fact]
    public async Task An_agent_that_reaches_only_the_resolver_passes()
    {
        var script = Environment(withPolicy: true);

        // The permitted control answers; nothing the Hub is listening on does.
        script.Answer("/usr/bin/nc -w 3 10.255.255.254 53", string.Empty);

        var checks = await ProbeAsync(script);

        Assert.Equal(
            ReadinessStatus.Pass,
            Check(checks, HardIsolationChecks.ControlPlaneUnreachable).Status);

        Assert.Equal(
            ReadinessStatus.Pass,
            Check(checks, HardIsolationChecks.HostLoopbackDisabled).Status);
    }

    /// <summary>
    /// A peer that does not exist cannot be unreachable.
    ///
    /// This build knows one environment. Reporting peer separation from that would be asserting a
    /// negative about something that was never there to refuse.
    /// </summary>
    [Fact]
    public async Task Peer_network_separation_is_unknown_in_this_build()
    {
        foreach (var withPolicy in new[] { false, true })
        {
            var checks = await ProbeAsync(Environment(withPolicy));

            Assert.Equal(
                ReadinessStatus.Unknown,
                Check(checks, HardIsolationChecks.PeerNetworkUnreachable).Status);
        }
    }

    /// <summary>
    /// A silent network is unknown, not isolated.
    ///
    /// In this environment nothing answers a reachability attempt - including the resolver the
    /// policy permits. An implementation that counted the refusals and stopped there would hand
    /// out its strongest verdict for its weakest reason.
    /// </summary>
    [Fact]
    public async Task An_environment_that_reaches_nothing_at_all_is_unknown_rather_than_isolated()
    {
        var checks = await ProbeAsync(Environment(withPolicy: true));

        Assert.Equal(
            ReadinessStatus.Unknown,
            Check(checks, HardIsolationChecks.ControlPlaneUnreachable).Status);

        Assert.Equal(
            ReadinessStatus.Unknown,
            Check(checks, HardIsolationChecks.HostLoopbackDisabled).Status);
    }

    /// <summary>An environment that inherited no proxy says so; the value is never read.</summary>
    [Fact]
    public async Task An_environment_with_no_inherited_proxy_passes()
    {
        var checks = await ProbeAsync(Environment(withPolicy: true));

        Assert.Equal(ReadinessStatus.Pass, Check(checks, HardIsolationChecks.ProxyNotInherited).Status);
    }

    /// <summary>
    /// An inherited proxy fails, and the diagnostic names the variable and not its value.
    ///
    /// A proxy setting can carry a credential. A diagnostic that printed one would put it in every
    /// log that ever captured this output.
    /// </summary>
    [Fact]
    public async Task An_inherited_proxy_fails_without_printing_its_value()
    {
        var script = Environment(withPolicy: true);

        script.Answer("/usr/bin/printenv", "PATH=/usr/bin\nHTTPS_PROXY=http://user:secret@host:3128");

        var checks = await ProbeAsync(script);
        var proxy = Check(checks, HardIsolationChecks.ProxyNotInherited);

        Assert.Equal(ReadinessStatus.Fail, proxy.Status);
        Assert.Contains("HTTPS_PROXY", proxy.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", proxy.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("3128", proxy.Detail, StringComparison.Ordinal);
    }

    // -- readiness as a whole -------------------------------------------------------------------

    /// <summary>
    /// The network checks are required, so an unanswered one keeps the environment out.
    ///
    /// Adding a check that nothing requires would be adding a diagnostic, not a boundary.
    /// </summary>
    [Fact]
    public void Every_network_check_is_required()
    {
        foreach (var id in NetworkChecks)
        {
            Assert.Contains(id, HardIsolationChecks.Required);
        }
    }

    /// <summary>
    /// A build where every other check passes is still not ready while a network answer is unknown.
    ///
    /// The point of the whole slice in one assertion: the network is not an optional dimension of
    /// hard isolation, and an environment cannot be admitted with it unestablished.
    /// </summary>
    [Fact]
    public void An_environment_is_not_ready_while_a_network_answer_is_unknown()
    {
        foreach (var unanswered in NetworkChecks)
        {
            var checks = HardIsolationChecks.Required
                .Select(id => id == unanswered
                    ? ReadinessCheck.Unknown(id, "not established")
                    : ReadinessCheck.Passed(id, "fixture"))
                .ToList();

            Assert.False(HardIsolationReadiness.From("fixture", checks).IsReady, unanswered);
        }
    }
}
