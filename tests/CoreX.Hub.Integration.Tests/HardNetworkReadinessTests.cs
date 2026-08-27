using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// What readiness says about the trusted plane and about one organization's place on it.
///
/// Two truths that a single check would have collapsed, which on this platform is the mistake that
/// matters: every environment shares one network namespace and one ruleset, so "the policy is
/// loaded" is answered identically for an organization with an attachment and for one with none.
/// Everything below is about the answer being worse than Pass - a plane whose interfaces the
/// platform destroyed while its table survived, an organization with no namespace, a workload that
/// starts somewhere other than where it was assigned, a peer that does not exist to be unreachable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkReadinessTests
{
    private const string OrgA = "acme";
    private const string OrgB = "globex";

    private static readonly string[] FabricChecks =
    [
        HardIsolationChecks.NetworkFabricReady,
        HardIsolationChecks.OrgNetworkNamespaceReady,
        HardIsolationChecks.OrgNetworkAttachmentReady,
    ];

    private static readonly string[] ProbeChecks =
    [
        HardIsolationChecks.NetworkModeSupported,
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

    private static HardNetworkAttachment Attachment(string organization) =>
        HardNetworkIdentity.Describe(organization);

    /// <summary>
    /// A machine whose plane is exactly right for the organizations named.
    ///
    /// Everything a real one would report: the topology, the interfaces the plane holds, the loaded
    /// table, a running holder and a launch that lands where it should.
    /// </summary>
    private static ScriptedExecutionEnvironment Machine(
        IReadOnlyList<string> present,
        IReadOnlyList<string> classified,
        string? holderFor = null,
        string? launchedInto = null)
    {
        var script = new ScriptedExecutionEnvironment();

        script.Answer(
            "/usr/sbin/ip -o -4 addr",
            "1: lo inet 127.0.0.1/8 scope host lo\n2: eth0 inet 192.168.66.185/20 scope global eth0");

        script.Answer(
            "/usr/sbin/ip -4 route",
            "default via 192.168.64.1 dev eth0\n192.168.64.0/20 dev eth0 scope link src 192.168.66.185");

        script.Answer("/usr/bin/cat /etc/resolv.conf", "nameserver 10.255.255.254");
        script.Answer("/usr/bin/printenv", "PATH=/usr/bin");

        script.Answer(
            "/usr/sbin/ip -o link",
            string.Join(
                '\n',
                new[] { "1: lo: <LOOPBACK>", "2: eth0: <BROADCAST>" }
                    .Concat(present.Select((i, n) => $"{n + 3}: {i}@if{n}: <BROADCAST>"))));

        script.AnswerPrefix(
            "/usr/sbin/nft list table ",
            classified.Count == 0
                ? string.Empty
                : Loaded(classified),
            exitCode: classified.Count == 0 ? 1 : 0);

        if (holderFor is not null)
        {
            var unit = HardNetworkIdentity.HolderUnit(Attachment(holderFor).AttachmentId);

            script.Answer($"/usr/bin/systemctl show -p SubState --value {unit}", "running");
            script.Answer($"/usr/bin/systemctl show -p MainPID --value {unit}", "4242");
            script.Answer("/usr/bin/readlink /proc/4242/ns/net", "net:[4026532222]");
        }

        // What a workload actually gets when it is launched into that namespace.
        script.AnswerPrefix(
            "/usr/bin/systemd-run --quiet --pipe --wait --collect --uid ",
            launchedInto ?? string.Empty,
            exitCode: launchedInto is null ? 225 : 0);

        return script;
    }

    /// <summary>The table those organizations would have, as nft would print it back.</summary>
    private static string Loaded(IReadOnlyList<string> organizations)
    {
        var topology = HardNetworkTopology.Classify(
            ["2: eth0 inet 192.168.66.185/20 scope global eth0"],
            ["default via 192.168.64.1 dev eth0"],
            ["nameserver 10.255.255.254"]);

        Assert.True(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10,
                topology,
                organizations.Select(Attachment).ToList(),
                out var ruleset,
                out _));

        return string.Join(
            '\n',
            ruleset!.Text
                .Split('\n')
                .Where(l => !l.StartsWith("delete table", StringComparison.Ordinal))
                .Where(l => l.Trim() != $"table inet {NftablesRuleset.TableName} {{}}"));
    }

    private static async Task<IReadOnlyList<ReadinessCheck>> VerifyAsync(
        ScriptedExecutionEnvironment script, string organization) =>
        await WslHardNetworkFabric.Scripted(script).VerifyAsync(Profile(), organization, CancellationToken.None);

    private static ReadinessCheck Check(IReadOnlyList<ReadinessCheck> checks, string id) =>
        checks.Single(c => c.Id == id);

    // -- the whole plane -------------------------------------------------------------------------

    /// <summary>A plane that matches its policy, with a namespace and a correct launch, passes.</summary>
    [Fact]
    public async Task A_correct_plane_and_attachment_passes()
    {
        var a = Attachment(OrgA);

        var checks = await VerifyAsync(
            Machine([a.TrustedInterface], [OrgA], holderFor: OrgA, launchedInto: "net:[4026532222]"),
            OrgA);

        Assert.All(checks, c => Assert.Equal(ReadinessStatus.Pass, c.Status));
    }

    /// <summary>Every fabric answer is given, so none can be quietly missing.</summary>
    [Fact]
    public async Task Every_fabric_check_is_answered()
    {
        var checks = await VerifyAsync(Machine([], []), OrgA);

        Assert.Equal(
            FabricChecks.Order(StringComparer.Ordinal),
            checks.Select(c => c.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A table that survived a restart its interfaces did not is a failure, not a pass.
    ///
    /// The most dangerous shape measured on this platform: the policy is machine state that outlives
    /// the namespaces and links it names, so a table can list exactly the expected rules while
    /// classifying nothing at all. A check that compared only the text would report enforcement.
    /// </summary>
    [Fact]
    public async Task A_stale_table_whose_interfaces_are_gone_fails()
    {
        var checks = await VerifyAsync(Machine([], [OrgA], holderFor: OrgA), OrgA);

        var fabric = Check(checks, HardIsolationChecks.NetworkFabricReady);

        Assert.Equal(ReadinessStatus.Fail, fabric.Status);
        Assert.Contains("no longer has", fabric.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An attachment the policy does not name is a failure, because nothing would filter it.
    ///
    /// The cost of not being able to default to drop. On a shared plane the chain policy has to be
    /// permissive, so an interface nobody named is an interface with no boundary - and it would
    /// look exactly like a working one from inside.
    /// </summary>
    [Fact]
    public async Task An_attachment_no_rule_names_fails()
    {
        var a = Attachment(OrgA);
        var b = Attachment(OrgB);

        var checks = await VerifyAsync(
            Machine([a.TrustedInterface, b.TrustedInterface], [OrgA], holderFor: OrgA), OrgA);

        var fabric = Check(checks, HardIsolationChecks.NetworkFabricReady);

        Assert.Equal(ReadinessStatus.Fail, fabric.Status);
        Assert.Contains(b.TrustedInterface, fabric.Detail, StringComparison.Ordinal);
        Assert.Contains("unfiltered", fabric.Detail, StringComparison.Ordinal);
    }

    /// <summary>A plane with no policy at all fails rather than reporting nothing to check.</summary>
    [Fact]
    public async Task A_plane_with_no_policy_fails()
    {
        var a = Attachment(OrgA);

        Assert.Equal(
            ReadinessStatus.Fail,
            Check(
                await VerifyAsync(Machine([a.TrustedInterface], [], holderFor: OrgA), OrgA),
                HardIsolationChecks.NetworkFabricReady).Status);
    }

    /// <summary>A plane with a policy and no attachments at all is not a boundary either.</summary>
    [Fact]
    public async Task A_plane_with_no_attachments_fails() =>
        Assert.Equal(
            ReadinessStatus.Fail,
            Check(await VerifyAsync(Machine([], []), OrgA), HardIsolationChecks.NetworkFabricReady).Status);

    // -- one organization cannot answer for another ----------------------------------------------

    /// <summary>
    /// Another organization's plane does not make this organization ready.
    ///
    /// The single most important assertion in the file. Every environment sees the same table, so a
    /// check that stopped at the policy would report the same answer for an organization that has
    /// an attachment and one that has none at all.
    /// </summary>
    [Fact]
    public async Task One_organizations_attachment_does_not_satisfy_another()
    {
        var b = Attachment(OrgB);

        // The plane is perfectly correct - for B.
        var machine = Machine([b.TrustedInterface], [OrgB], holderFor: OrgB, launchedInto: "net:[4026532222]");

        var forB = await VerifyAsync(machine, OrgB);

        Assert.All(forB, c => Assert.Equal(ReadinessStatus.Pass, c.Status));

        var forA = await VerifyAsync(machine, OrgA);

        Assert.Equal(
            ReadinessStatus.Fail, Check(forA, HardIsolationChecks.OrgNetworkNamespaceReady).Status);
        Assert.Equal(
            ReadinessStatus.Fail, Check(forA, HardIsolationChecks.OrgNetworkAttachmentReady).Status);
    }

    // -- the organization's own namespace --------------------------------------------------------

    /// <summary>An organization with no holder has no namespace, and says so.</summary>
    [Fact]
    public async Task An_organization_with_no_holder_fails()
    {
        var a = Attachment(OrgA);

        var namespaceCheck = Check(
            await VerifyAsync(Machine([a.TrustedInterface], [OrgA]), OrgA),
            HardIsolationChecks.OrgNetworkNamespaceReady);

        Assert.Equal(ReadinessStatus.Fail, namespaceCheck.Status);
        Assert.Contains("no network boundary of its own", namespaceCheck.Detail, StringComparison.Ordinal);
    }

    /// <summary>A namespace with no link into it is not an attachment.</summary>
    [Fact]
    public async Task A_namespace_with_no_link_is_not_attached()
    {
        var attachment = Check(
            await VerifyAsync(Machine([], [], holderFor: OrgA), OrgA),
            HardIsolationChecks.OrgNetworkAttachmentReady);

        Assert.Equal(ReadinessStatus.Fail, attachment.Status);
        Assert.Contains("no link into it", attachment.Detail, StringComparison.Ordinal);
    }

    // -- where a workload really lands -----------------------------------------------------------

    /// <summary>
    /// A launch that lands in the wrong namespace fails, however healthy everything else looks.
    ///
    /// Measured on this platform: a launch mechanism was observed silently substituting a fresh
    /// empty namespace when the intended one was missing. "The unit started" is not evidence of
    /// starting in the right place, so the kernel identity is compared instead.
    /// </summary>
    [Fact]
    public async Task A_workload_that_lands_in_the_wrong_namespace_fails()
    {
        var a = Attachment(OrgA);

        var attachment = Check(
            await VerifyAsync(
                Machine([a.TrustedInterface], [OrgA], holderFor: OrgA, launchedInto: "net:[4026539999]"),
                OrgA),
            HardIsolationChecks.OrgNetworkAttachmentReady);

        Assert.Equal(ReadinessStatus.Fail, attachment.Status);
        Assert.Contains("starting is not evidence", attachment.Detail, StringComparison.Ordinal);
    }

    /// <summary>A launch that cannot happen at all fails rather than being read as absence.</summary>
    [Fact]
    public async Task A_launch_that_fails_closed_is_not_an_attachment()
    {
        var a = Attachment(OrgA);

        Assert.Equal(
            ReadinessStatus.Fail,
            Check(
                await VerifyAsync(Machine([a.TrustedInterface], [OrgA], holderFor: OrgA), OrgA),
                HardIsolationChecks.OrgNetworkAttachmentReady).Status);
    }

    // -- refusal at the edges ---------------------------------------------------------------------

    /// <summary>An organization whose name derives nothing usable is unknown, never ready.</summary>
    [Fact]
    public async Task An_unnameable_organization_is_unestablished()
    {
        var checks = await WslHardNetworkFabric
            .Scripted(Machine([], []))
            .VerifyAsync(Profile(), "   x", CancellationToken.None);

        Assert.All(checks, c => Assert.NotEqual(ReadinessStatus.Pass, c.Status));
    }

    // -- what release does to the machine ----------------------------------------------------------

    /// <summary>
    /// Releasing the last attachment does not reset a global value the Hub never set.
    ///
    /// Forwarding belongs to the machine. Turning it on when it was off is a change this build made
    /// and should undo; finding it already on is somebody else's decision, and resetting it on the
    /// way out would be this build reconfiguring an operator's machine because it happened to be
    /// finished with it.
    /// </summary>
    [Fact]
    public async Task Release_leaves_a_global_value_the_hub_did_not_set()
    {
        var script = Machine([], []);

        await WslHardNetworkFabric.Scripted(script).ReleaseAsync(Profile(), OrgA, CancellationToken.None);

        Assert.DoesNotContain(
            script.Calls,
            c => c.Count > 1 && c[0].EndsWith("sysctl", StringComparison.Ordinal));
    }

    /// <summary>
    /// Releasing one of two attachments leaves the other one classified.
    ///
    /// The plane is shared, so giving up an attachment means re-rendering the policy for the ones
    /// that remain - not deleting it. A release that tore the table down would take the surviving
    /// organization's boundary with it.
    /// </summary>
    [Fact]
    public async Task Releasing_one_attachment_does_not_tear_down_the_plane()
    {
        var b = Attachment(OrgB);

        var script = Machine([b.TrustedInterface], [OrgA, OrgB], holderFor: OrgA);

        await WslHardNetworkFabric.Scripted(script).ReleaseAsync(Profile(), OrgA, CancellationToken.None);

        // The Hub's tables are not deleted while somebody is still attached.
        Assert.DoesNotContain(
            script.Calls,
            c => c.Count > 2 && c[1] == "delete" && c[2] == "table");
    }

    // -- readiness as a whole ----------------------------------------------------------------------

    /// <summary>Every network truth is required, so an unanswered one keeps the environment out.</summary>
    [Fact]
    public void Every_network_check_is_required()
    {
        foreach (var id in FabricChecks.Concat(ProbeChecks))
        {
            Assert.Contains(id, HardIsolationChecks.Required);
        }
    }

    /// <summary>
    /// The retired check is gone rather than left behind as a summary nobody reads.
    ///
    /// It meant "this distribution has its own root policy", which is false on a platform where
    /// every distribution shares one. A check that cannot be true should not be answerable.
    /// </summary>
    [Fact]
    public void The_invalid_policy_check_no_longer_exists() =>
        Assert.DoesNotContain("network.policy.enforced", HardIsolationChecks.Required);

    /// <summary>An environment is not ready while any network answer is unknown.</summary>
    [Fact]
    public void An_environment_is_not_ready_while_a_network_answer_is_unknown()
    {
        foreach (var unanswered in FabricChecks.Concat(ProbeChecks))
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
