using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The two gates, and the circular dependency that made one of them necessary.
///
/// The defect these exist for was found by the first real hard acceptance and could not have been
/// found any other way: every part worked, and the parts were ordered so that no first execution
/// could ever happen. Admission required a live network fabric; the fabric is built by an
/// execution's own lease; the lease is taken after admission. Nothing was weak - the arrangement was
/// simply impossible, and it would have stayed impossible on any machine however well provisioned.
///
/// So the ordering changed and the trust did not. Gate A asks what is true of an idle organization.
/// Gate B asks whether the boundary the lease just built holds, from the placement the workload will
/// get, before anything repository controlled runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardBoundaryGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "corex-gates-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The regression. A perfect environment with no fabric yet is admissible.
    ///
    /// Every pre-acquire check passes, every live check is unmet because nothing has been built, and
    /// admission says yes - because the only thing admission is entitled to decide is whether the
    /// Hub may try.
    /// </summary>
    [Fact]
    public void An_organization_with_no_live_fabric_is_still_admissible()
    {
        var readiness = HardIsolationReadiness.From("org-x", PreAcquireAllPass());

        Assert.True(readiness.IsAdmissionEligible);

        // And the whole picture is honestly not ready, which is what a diagnostic shows.
        Assert.False(readiness.IsReady);
        Assert.False(readiness.IsLiveBoundaryReady(peerApplicable: false));
    }

    /// <summary>
    /// The three live checks are exactly the ones an execution builds, and none of them gate
    /// admission.
    ///
    /// Stated as a set rather than checked one at a time, so a check added to the required list
    /// later has to be classified deliberately instead of silently re-creating the deadlock.
    /// </summary>
    [Fact]
    public void The_live_boundary_checks_are_never_pre_acquire()
    {
        Assert.Equal(
            [
                HardIsolationChecks.ControlPlaneUnreachable,
                HardIsolationChecks.NetworkFabricReady,
                HardIsolationChecks.HostLoopbackDisabled,
                HardIsolationChecks.OrgNetworkAttachmentReady,
                HardIsolationChecks.OrgNetworkNamespaceReady,
            ],
            HardIsolationChecks.PostAcquireRequired.Order(StringComparer.Ordinal));

        Assert.Empty(
            HardIsolationChecks.PreAcquireRequired
                .Intersect(HardIsolationChecks.PostAcquireRequired, StringComparer.Ordinal));

        Assert.Empty(
            HardIsolationChecks.PreAcquireRequired
                .Intersect(HardIsolationChecks.ConditionalOnPeer, StringComparer.Ordinal));

        // Nothing was dropped on the way: the three groups still account for every required check.
        Assert.Equal(
            HardIsolationChecks.Required.Order(StringComparer.Ordinal),
            HardIsolationChecks.PreAcquireRequired
                .Concat(HardIsolationChecks.PostAcquireRequired)
                .Concat(HardIsolationChecks.ConditionalOnPeer)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Admission builds nothing.
    ///
    /// The frozen rule, restated where the ordering changed: measuring may not provision, so a Gate A
    /// that had quietly started building a fabric to satisfy itself would be the failure the whole
    /// separation exists to avoid.
    /// </summary>
    [Fact]
    public async Task Admission_creates_no_fabric()
    {
        var fabric = new ScriptedFabric();
        var consumers = new HardNetworkConsumers(fabric, _root);

        var readiness = HardIsolationReadiness.From("org-x", PreAcquireAllPass());

        Assert.True(readiness.IsAdmissionEligible);
        Assert.Equal(0, fabric.Prepared);
        Assert.Empty(consumers.ClaimedAttachments());

        await Task.CompletedTask;
    }

    /// <summary>
    /// A sound boundary is measured once and the environment becomes usable.
    ///
    /// The measurement is not optional and not lazy: it happens between the lease being taken and the
    /// lease being handed to anything, so every hard port inherits the same invariant.
    /// </summary>
    [Fact]
    public async Task A_sound_boundary_is_measured_before_the_lease_is_usable()
    {
        var gate = new SoundBoundaryGate();
        var resolver = Resolver(gate, out var fabric);

        var refusal = await resolver.EnsureBoundaryAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(refusal);
        Assert.Equal(1, gate.Measured);
        Assert.Equal(1, fabric.Prepared);
        Assert.True(resolver.HasLiveEnvironment);
    }

    /// <summary>
    /// An unsound boundary refuses the execution and releases what was built.
    ///
    /// No workspace, no agent, no verification, no review - and nothing left running. A boundary that
    /// did not hold is not something to keep alive while an execution is turned away.
    /// </summary>
    [Fact]
    public async Task An_unsound_boundary_refuses_and_releases()
    {
        var gate = new SoundBoundaryGate { Unsound = "org-network-attachment.ready (fail)" };
        var resolver = Resolver(gate, out var fabric);

        var refusal = await resolver.EnsureBoundaryAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Contains("org-network-attachment.ready", refusal!, StringComparison.Ordinal);

        Assert.Equal(1, fabric.Prepared);
        Assert.Equal(1, fabric.Released);
        Assert.False(resolver.HasLiveEnvironment);
    }

    /// <summary>
    /// A live check nobody could answer fails closed.
    ///
    /// The case that matters most, because it is the one an optimistic implementation gets wrong:
    /// "the probe is unavailable" is not "the boundary is fine".
    /// </summary>
    [Theory]
    [InlineData(ReadinessStatus.Fail)]
    [InlineData(ReadinessStatus.Unknown)]
    public void A_live_check_that_did_not_pass_is_never_ready(ReadinessStatus status)
    {
        var checks = PreAcquireAllPass()
            .Concat(HardIsolationChecks.PostAcquireRequired.Select(id =>
                id == HardIsolationChecks.ControlPlaneUnreachable
                    ? new ReadinessCheck(id, status, "this build has no probe for it")
                    : ReadinessCheck.Passed(id, "built")))
            .ToList();

        var readiness = HardIsolationReadiness.From("org-x", checks);

        Assert.False(readiness.IsLiveBoundaryReady(peerApplicable: false));
        Assert.Contains(HardIsolationChecks.ControlPlaneUnreachable, readiness.UnmetAmong(HardIsolationChecks.PostAcquireRequired));
    }

    /// <summary>
    /// With no peer, peer separation is not applicable and does not refuse anything.
    ///
    /// The raw answer is kept exactly as the probe gave it - unknown, because nothing was measured -
    /// and is not rewritten into a pass. What changes is only whether the question applies.
    /// </summary>
    [Fact]
    public void A_single_organization_is_not_refused_for_having_no_peer()
    {
        var readiness = HardIsolationReadiness.From("org-x", LiveAllPass());

        Assert.True(readiness.IsLiveBoundaryReady(peerApplicable: false));
        Assert.False(readiness.IsLiveBoundaryReady(peerApplicable: true));

        // Not fabricated: the check still says exactly what the probe concluded.
        var peer = readiness.Checks.Single(c => c.Id == HardIsolationChecks.PeerNetworkUnreachable);

        Assert.Equal(ReadinessStatus.Unknown, peer.Status);
    }

    /// <summary>With a peer present, the same unknown answer refuses the boundary.</summary>
    [Fact]
    public void A_peer_that_exists_makes_the_check_required()
    {
        var readiness = HardIsolationReadiness.From("org-x", LiveAllPass());

        Assert.False(readiness.IsLiveBoundaryReady(peerApplicable: true));

        var proved = HardIsolationReadiness.From(
            "org-x",
            LiveAllPass()
                .Where(c => c.Id != HardIsolationChecks.PeerNetworkUnreachable)
                .Append(ReadinessCheck.Passed(HardIsolationChecks.PeerNetworkUnreachable, "no peer was reachable")));

        Assert.True(proved.IsLiveBoundaryReady(peerApplicable: true));
    }

    // -- fixtures -----------------------------------------------------------------------------

    private HardExecutionEnvironmentResolver Resolver(IHardBoundaryGate gate, out ScriptedFabric fabric)
    {
        fabric = new ScriptedFabric();

        return new HardExecutionEnvironmentResolver(
            new HardNetworkConsumers(fabric, _root),
            new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment()),
            gate,
            new TrustedGuestFileTransfer(new ScriptedExecutionEnvironment(), Profile()),
            Profile(),
            "org-x");
    }

    private static List<ReadinessCheck> PreAcquireAllPass() =>
    [
        .. HardIsolationChecks.PreAcquireRequired.Select(id => ReadinessCheck.Passed(id, "measured")),
    ];

    private static List<ReadinessCheck> LiveAllPass() =>
    [
        .. PreAcquireAllPass(),
        .. HardIsolationChecks.PostAcquireRequired.Select(id => ReadinessCheck.Passed(id, "built and measured")),
    ];

    private static HardIsolationProfile Profile()
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
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);

        return profile!;
    }
}
