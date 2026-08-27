using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The production fabric, against two real disposable environments.
///
/// Every test here does nothing unless an operator has explicitly named two disposable
/// environments in the process environment, and that is the whole safety model: this class builds
/// interfaces, loads a policy and turns on machine-global forwarding, so it must never run because
/// somebody typed <c>dotnet test</c>. It does not create or destroy distributions either -
/// provisioning is the operator's step, done deliberately, and these tests only use what they were
/// pointed at.
///
/// What it proves is the part no scripted environment can: that the code which renders and verifies
/// the boundary is the code that actually denies traffic. The scripted tests establish that the
/// policy is correct; these establish that it is real.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkFabricAcceptanceTests
{
    private const string OrgA = "corex-fabric-org-a";
    private const string OrgB = "corex-fabric-org-b";

    /// <summary>The two disposable environments, or nothing, in which case every test is inert.</summary>
    private static (string A, string B)? Environments()
    {
        var a = Environment.GetEnvironmentVariable("COREX_FABRIC_A");
        var b = Environment.GetEnvironmentVariable("COREX_FABRIC_B");

        return string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) ? null : (a!, b!);
    }

    private static HardIsolationProfile Profile(string environment, string agent)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = environment,
            AgentUser = agent,
            ServiceUser = "root",
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = "/srv/corex/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    private static WslHardNetworkFabric Fabric() => WslHardNetworkFabric.Create(Path.GetTempPath());

    /// <summary>Runs one command in an environment, for arranging and for reading back.</summary>
    private static async Task<(int? ExitCode, string Stdout)> RunAsync(
        string environment, string user, params string[] arguments)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        var result = await new ProcessRunner(FileSystemGuard.Create(Path.GetTempPath())).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["-d", environment, "-u", user, "--exec", .. arguments],
                WorkingDirectory = Path.GetTempPath(),
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromMinutes(2),
                MaxCapturedCharactersPerStream = 1024 * 1024,
            },
            CancellationToken.None);

        return (result.ExitCode, result.NormalizedStdout);
    }

    /// <summary>Launches a command as the agent, inside the organization's own namespace.</summary>
    private static Task<(int? ExitCode, string Stdout)> InAttachmentAsync(
        string environment, string agent, HardNetworkAttachment attachment, params string[] command) =>
        RunAsync(
            environment,
            "root",
            [
                "/usr/bin/systemd-run", "--quiet", "--pipe", "--wait", "--collect",
                "--uid", agent,
                "--property=NetworkNamespacePath=" + attachment.NamespaceReference,
                .. command,
            ]);

    private static ReadinessCheck Check(IReadOnlyList<ReadinessCheck> checks, string id) =>
        checks.Single(c => c.Id == id);

    // -- attachment ------------------------------------------------------------------------------

    /// <summary>
    /// Two organizations prepared through production code get two distinct namespaces, and a
    /// workload launched for each lands in its own.
    ///
    /// The first half was already known from discovery; what this adds is that the code doing it is
    /// the shipped code, driven the way the Hub will drive it.
    /// </summary>
    [Fact]
    public async Task Two_prepared_organizations_get_distinct_namespaces()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var fabric = Fabric();

        var a = await fabric.PrepareAsync(Profile(environments.A, "corex-agent-a"), OrgA, CancellationToken.None);
        var b = await fabric.PrepareAsync(Profile(environments.B, "corex-agent-b"), OrgB, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);

        Assert.NotEqual(a.NamespaceIdentity, b.NamespaceIdentity);
        Assert.NotEqual(a.TrustedInterface, b.TrustedInterface);
        Assert.NotEqual(a.Subnet, b.Subnet);

        // And a workload really lands there, rather than reporting that it did.
        var landed = await InAttachmentAsync(
            environments.A, "corex-agent-a", a, "/usr/bin/readlink", "/proc/self/ns/net");

        Assert.Equal(a.NamespaceIdentity, landed.Stdout.Trim());

        var root = await RunAsync(environments.A, "root", "/usr/bin/readlink", "/proc/1/ns/net");

        Assert.NotEqual(root.Stdout.Trim(), landed.Stdout.Trim());
    }

    /// <summary>
    /// A launch against a namespace that is not there fails, and the executable never runs.
    ///
    /// The race this design turns on. The alternative mechanism was measured starting the workload
    /// in a fresh empty namespace instead of failing, so what matters is not that the wrong
    /// namespace is detected afterwards - it is that nothing untrusted starts at all.
    /// </summary>
    [Fact]
    public async Task A_launch_against_a_missing_namespace_never_starts_the_workload()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var absent = HardNetworkIdentity.Describe(OrgA, "/proc/999999/ns/net", "net:[0]");

        var attempt = await InAttachmentAsync(
            environments.A, "corex-agent-a", absent, "/usr/bin/touch", "/tmp/corex-should-not-exist");

        Assert.NotEqual(0, attempt.ExitCode);

        var evidence = await RunAsync(environments.A, "root", "/usr/bin/test", "-e", "/tmp/corex-should-not-exist");

        Assert.NotEqual(0, evidence.ExitCode);
    }

    /// <summary>
    /// Killing the holder makes every later launch fail rather than land somewhere else.
    ///
    /// The deliberate adversarial case. A mechanism that fell back to a fresh namespace would pass
    /// every other test in this file and would be a boundary that disappears exactly when the thing
    /// holding it does.
    /// </summary>
    [Fact]
    public async Task A_dead_holder_makes_every_later_launch_fail()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var profile = Profile(environments.A, "corex-agent-a");
        var fabric = Fabric();

        var attachment = await fabric.PrepareAsync(profile, OrgA, CancellationToken.None);

        Assert.NotNull(attachment);

        var before = await InAttachmentAsync(
            environments.A, "corex-agent-a", attachment, "/usr/bin/readlink", "/proc/self/ns/net");

        Assert.Equal(attachment.NamespaceIdentity, before.Stdout.Trim());

        await RunAsync(
            environments.A, "root", "/usr/bin/systemctl", "stop",
            HardNetworkIdentity.HolderUnit(attachment.AttachmentId));

        var after = await InAttachmentAsync(
            environments.A, "corex-agent-a", attachment, "/usr/bin/readlink", "/proc/self/ns/net");

        Assert.NotEqual(0, after.ExitCode);
        Assert.DoesNotContain("net:[", after.Stdout, StringComparison.Ordinal);

        // And readiness says the organization has no boundary any more.
        var checks = await fabric.VerifyAsync(profile, OrgA, CancellationToken.None);

        Assert.Equal(
            ReadinessStatus.Fail, Check(checks, HardIsolationChecks.OrgNetworkNamespaceReady).Status);
    }

    // -- what the policy denies -------------------------------------------------------------------

    /// <summary>
    /// With both organizations prepared, neither can reach the other and both can reach the public
    /// internet.
    ///
    /// The peers were reachable before any policy existed - that is what discovery established, and
    /// it is what makes this denial a measurement rather than a coincidence of routing.
    /// </summary>
    [Fact]
    public async Task Peers_are_denied_while_public_egress_still_works()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var fabric = Fabric();

        var a = await fabric.PrepareAsync(Profile(environments.A, "corex-agent-a"), OrgA, CancellationToken.None);
        var b = await fabric.PrepareAsync(Profile(environments.B, "corex-agent-b"), OrgB, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);

        // The permitted control first: a denial means nothing if nothing works.
        var egress = await InAttachmentAsync(
            environments.A, "corex-agent-a", a, "/usr/bin/nc", "-w", "5", "-z", "1.1.1.1", "443");

        Assert.Equal(0, egress.ExitCode);

        var resolver = await InAttachmentAsync(
            environments.A, "corex-agent-a", a, "/usr/bin/nc", "-w", "5", "-z", "10.255.255.254", "53");

        Assert.Equal(0, resolver.ExitCode);

        // And the peer, at the address the Hub itself assigned it.
        var peer = await InAttachmentAsync(
            environments.A, "corex-agent-a", a, "/usr/bin/nc", "-w", "4", "-z", b.NamespaceAddress, "22");

        Assert.NotEqual(0, peer.ExitCode);

        var reverse = await InAttachmentAsync(
            environments.B, "corex-agent-b", b, "/usr/bin/nc", "-w", "4", "-z", a.NamespaceAddress, "22");

        Assert.NotEqual(0, reverse.ExitCode);
    }

    /// <summary>
    /// The trusted plane's own address is denied, and so is the host behind it.
    ///
    /// Two different denials that look alike from inside: the first stops an attachment terminating
    /// at a service on the shared plane, the second stops it reaching the machine the plane runs on.
    /// </summary>
    [Fact]
    public async Task The_trusted_plane_and_the_host_are_both_denied()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var profile = Profile(environments.A, "corex-agent-a");

        var a = await Fabric().PrepareAsync(profile, OrgA, CancellationToken.None);

        Assert.NotNull(a);

        // The gateway is the trusted side of this attachment's own link.
        var plane = await InAttachmentAsync(
            environments.A, "corex-agent-a", a, "/usr/bin/nc", "-w", "4", "-z", a.GatewayAddress, "53");

        Assert.NotEqual(0, plane.ExitCode);

        var host = await RunAsync(environments.A, "root", "/usr/sbin/ip", "-4", "route");

        var gateway = HardNetworkTopology
            .Classify([], host.Stdout.Split('\n'), [])
            .DefaultGateway;

        if (gateway.Length > 0)
        {
            var toHost = await InAttachmentAsync(
                environments.A, "corex-agent-a", a, "/usr/bin/nc", "-w", "4", "-z", gateway, "53");

            Assert.NotEqual(0, toHost.ExitCode);
        }
    }

    /// <summary>
    /// An agent inside its namespace cannot change what classifies it.
    ///
    /// Identity is the interface a packet arrives on, so the interesting attempt is not reading the
    /// policy - it is changing the address, the route or the link, and taking the peer's address is
    /// the one that would matter if identity were the source address instead.
    /// </summary>
    [Fact]
    public async Task An_agent_cannot_change_what_classifies_it()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var fabric = Fabric();

        var a = await fabric.PrepareAsync(Profile(environments.A, "corex-agent-a"), OrgA, CancellationToken.None);
        var b = await fabric.PrepareAsync(Profile(environments.B, "corex-agent-b"), OrgB, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);

        foreach (var attempt in new[]
                 {
                     new[] { "/usr/sbin/ip", "addr", "add", b.NamespaceAddress + "/32", "dev", a.NamespaceInterface },
                     ["/usr/sbin/ip", "route", "add", "203.0.113.0/24", "dev", a.NamespaceInterface],
                     ["/usr/sbin/ip", "link", "set", a.NamespaceInterface, "down"],
                     ["/usr/sbin/nft", "flush", "ruleset"],
                     ["/usr/sbin/nft", "delete", "table", "inet", NftablesRuleset.TableName],
                 })
        {
            var refused = await InAttachmentAsync(environments.A, "corex-agent-a", a, attempt);

            Assert.NotEqual(0, refused.ExitCode);
        }

        // The plane is untouched, so the boundary still holds.
        var checks = await fabric.VerifyAsync(Profile(environments.A, "corex-agent-a"), OrgA, CancellationToken.None);

        Assert.Equal(ReadinessStatus.Pass, Check(checks, HardIsolationChecks.NetworkFabricReady).Status);
    }

    /// <summary>
    /// A packet carrying the peer's source address is still classified as its own attachment.
    ///
    /// Arranged from outside the namespace, because the agent cannot add the address itself - which
    /// is the point: even when the address is forged by something that can, the interface it arrives
    /// on is not.
    /// </summary>
    [Fact]
    public async Task A_forged_source_address_is_still_classified_by_interface()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var fabric = Fabric();

        var a = await fabric.PrepareAsync(Profile(environments.A, "corex-agent-a"), OrgA, CancellationToken.None);
        var b = await fabric.PrepareAsync(Profile(environments.B, "corex-agent-b"), OrgB, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);

        var holder = await RunAsync(
            environments.A, "root", "/usr/bin/systemctl", "show", "-p", "MainPID", "--value",
            HardNetworkIdentity.HolderUnit(a.AttachmentId));

        var pid = holder.Stdout.Trim();

        await RunAsync(
            environments.A, "root", "/usr/bin/nsenter", "-t", pid, "-n",
            "/usr/sbin/ip", "addr", "add", b.NamespaceAddress + "/32", "dev", a.NamespaceInterface);

        try
        {
            var forged = await RunAsync(
                environments.A, "root", "/usr/bin/nsenter", "-t", pid, "-n",
                "/usr/bin/nc", "-w", "4", "-z", "-s", b.NamespaceAddress, a.GatewayAddress, "53");

            Assert.NotEqual(0, forged.ExitCode);
        }
        finally
        {
            await RunAsync(
                environments.A, "root", "/usr/bin/nsenter", "-t", pid, "-n",
                "/usr/sbin/ip", "addr", "del", b.NamespaceAddress + "/32", "dev", a.NamespaceInterface);
        }
    }

    // -- the plane's own truth ---------------------------------------------------------------------

    /// <summary>
    /// The canonical comparison agrees with what the real policy engine prints back.
    ///
    /// The gap the previous slice left open. A verifier that had never been checked against real
    /// output would either reject a correct plane forever or, worse, have been loosened until it
    /// accepted anything.
    /// </summary>
    [Fact]
    public async Task The_canonical_comparison_matches_real_policy_output()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var profile = Profile(environments.A, "corex-agent-a");

        Assert.NotNull(await Fabric().PrepareAsync(profile, OrgA, CancellationToken.None));

        var checks = await Fabric().VerifyAsync(profile, OrgA, CancellationToken.None);

        // A pass here is only possible if the loaded table and the rendered one canonicalise the
        // same way - which is the assertion, made against output nobody wrote by hand.
        Assert.Equal(ReadinessStatus.Pass, Check(checks, HardIsolationChecks.NetworkFabricReady).Status);
    }

    /// <summary>
    /// Releasing the last attachment leaves the machine as it was found.
    ///
    /// Including the global value: it goes back only if the Hub was the one that changed it, because
    /// a value the operator set is not the Hub's to reset.
    /// </summary>
    [Fact]
    public async Task Releasing_the_last_attachment_restores_the_machine()
    {
        if (Environments() is not { } environments)
        {
            return;
        }

        var fabric = Fabric();

        var profileA = Profile(environments.A, "corex-agent-a");
        var profileB = Profile(environments.B, "corex-agent-b");

        Assert.NotNull(await fabric.PrepareAsync(profileA, OrgA, CancellationToken.None));
        Assert.NotNull(await fabric.PrepareAsync(profileB, OrgB, CancellationToken.None));

        await fabric.ReleaseAsync(profileA, OrgA, CancellationToken.None);

        // One gone, one left: the plane is still there and still correct for the survivor.
        var survivor = await fabric.VerifyAsync(profileB, OrgB, CancellationToken.None);

        Assert.Equal(ReadinessStatus.Pass, Check(survivor, HardIsolationChecks.NetworkFabricReady).Status);

        await fabric.ReleaseAsync(profileB, OrgB, CancellationToken.None);

        var table = await RunAsync(
            environments.A, "root", "/usr/sbin/nft", "list", "table", "inet", NftablesRuleset.TableName);

        Assert.NotEqual(0, table.ExitCode);

        var forwarding = await RunAsync(environments.A, "root", "/usr/bin/cat", "/proc/sys/net/ipv4/ip_forward");

        Assert.Equal("0", forwarding.Stdout.Trim());

        var links = await RunAsync(environments.A, "root", "/usr/sbin/ip", "-o", "link");

        Assert.DoesNotContain("cxr", links.Stdout, StringComparison.Ordinal);
    }
}
