using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Bringing durable network claims back into agreement with what the executions really are.
///
/// A Hub that dies mid execution leaves a claim on its organization's network attachment behind.
/// Nobody is coming back for it, and until somebody notices, the attachment it names stays alive.
/// These establish that the next process notices - and, just as importantly, that it does not
/// notice things that are not its business.
///
/// The asymmetry runs through all of them. Leaving an attachment alive that nothing is using is
/// visible, releasable, and costs a veth; tearing down one that an execution is working inside is
/// not recoverable. So every ambiguous case here resolves towards preservation, and the tests say
/// so explicitly rather than leaving it to be inferred from an absence.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkReconciliationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "corex-reconcile-" + Guid.CreateVersion7().ToString("N"));

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

    private HardNetworkConsumers Consumers(RecordingFabric fabric) => new(fabric, _root);

    private HardNetworkConsumerReconciler Reconciler(
        RecordingFabric fabric,
        HardNetworkConsumers consumers,
        IReadOnlyList<string> hardOrganizations,
        IReadOnlySet<Guid> entitled) =>
        new(
            _ => Task.FromResult(hardOrganizations),
            _ => Task.FromResult(entitled),
            consumers,
            new FixedProfiles(Profile()),
            fabric);

    private async Task<Guid> ClaimAsync(HardNetworkConsumers consumers, string org)
    {
        var execution = Guid.CreateVersion7();
        await consumers.AcquireAsync(Profile(), org, execution, CancellationToken.None);
        return execution;
    }

    // -- claims that are no longer owed anything ------------------------------------------------

    /// <summary>A claim whose execution has finished is not a live consumer. (A, B)</summary>
    [Fact]
    public async Task A_claim_from_a_finished_execution_is_dropped()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);

        await ClaimAsync(consumers, "org-x");

        // Terminal, interrupted, cancelled - all the same to this. Entitlement is what the
        // lifecycle says, and it says nothing here.
        var report = await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid>())
            .ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.ClaimsDropped);
        Assert.Empty(consumers.Consumers("org-x"));

        // Nobody left, so the attachment is the Hub's to take back. (E, I)
        Assert.Equal(1, report.AttachmentsReleased);
        Assert.Equal(["org-x"], fabric.ReleasedFor);
    }

    /// <summary>Two live executions keep their attachment. (C)</summary>
    [Fact]
    public async Task Two_live_executions_keep_their_attachment()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);

        var a = await ClaimAsync(consumers, "org-x");
        var b = await ClaimAsync(consumers, "org-x");

        var report = await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid> { a, b })
            .ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, report.ClaimsDropped);
        Assert.Equal(0, report.AttachmentsReleased);
        Assert.Empty(fabric.ReleasedFor);
        Assert.Equal(2, consumers.Consumers("org-x").Count);
    }

    /// <summary>
    /// One stale claim beside a live one drops only the stale one. (D)
    ///
    /// The case that would hurt most if it went wrong: an execution finishing is not a reason to
    /// take the network away from one that is still working.
    /// </summary>
    [Fact]
    public async Task A_stale_claim_beside_a_live_one_takes_nothing_with_it()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);

        await ClaimAsync(consumers, "org-x");
        var live = await ClaimAsync(consumers, "org-x");

        var report = await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid> { live })
            .ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.ClaimsDropped);
        Assert.Equal(0, report.AttachmentsReleased);
        Assert.Equal([live], consumers.Consumers("org-x"));
        Assert.Empty(fabric.ReleasedFor);
    }

    /// <summary>
    /// A claim proves nobody released it, never that the boundary is ready. (F)
    ///
    /// Readiness is measured, not remembered. A surviving marker says only that reconciliation had
    /// no reason to remove it; whether a holder or a veth exists is a question for the fabric.
    /// </summary>
    [Fact]
    public async Task A_surviving_claim_does_not_assert_readiness()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);
        var live = await ClaimAsync(consumers, "org-x");

        await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid> { live })
            .ReconcileAsync(CancellationToken.None);

        // Reconciliation asked nothing about physical state, and manufactured no verdict from the
        // claim it preserved.
        Assert.Empty(fabric.Verified);
        Assert.Equal([live], consumers.Consumers("org-x"));
    }

    // -- things that are not this reconciler's business -----------------------------------------

    /// <summary>
    /// A registry entry for no organization this Hub knows is preserved and counted. (G)
    ///
    /// A directory name is not authority to tear down a network attachment. It could belong to an
    /// organization that was renamed, one that another installation owns, or nothing at all.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_attachment_is_preserved()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);

        await ClaimAsync(consumers, "org-gone");

        // This Hub knows only org-x now.
        var report = await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid>())
            .ReconcileAsync(CancellationToken.None);

        Assert.Single(report.Preserved);
        Assert.Equal(0, report.ClaimsDropped);
        Assert.Empty(fabric.ReleasedFor);

        // Still there, untouched.
        Assert.Single(consumers.Consumers("org-gone"));
    }

    /// <summary>
    /// A file that is not an execution identity is left exactly where it is. (H)
    ///
    /// Something put it there, and this does not know what. Deleting unrecognised content out of a
    /// directory the Hub happens to own is the kind of tidying that removes evidence.
    /// </summary>
    [Fact]
    public async Task A_marker_that_is_not_an_execution_identity_is_left_alone()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);
        var live = await ClaimAsync(consumers, "org-x");

        var directory = Path.Combine(
            _root, "hard-network-consumers", HardNetworkIdentity.AttachmentId("org-x"));

        var strange = Path.Combine(directory, "not-an-execution.lease");
        await File.WriteAllTextAsync(strange, string.Empty);

        await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid> { live })
            .ReconcileAsync(CancellationToken.None);

        // Not parsed as an identity, not counted as a consumer, and not deleted.
        Assert.True(File.Exists(strange));
        Assert.Equal([live], consumers.Consumers("org-x"));
    }

    // -- crash shapes ----------------------------------------------------------------------------

    /// <summary>
    /// An attachment that is already gone reconciles to success. (J)
    ///
    /// The crash-after-release case: the physical teardown ran and the process died before it could
    /// finish. Releasing again is what the frozen fabric is idempotent for.
    /// </summary>
    [Fact]
    public async Task An_attachment_that_is_already_gone_is_not_an_error()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);

        await ClaimAsync(consumers, "org-x");

        var report = await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid>())
            .ReconcileAsync(CancellationToken.None);

        Assert.Empty(report.Residual);
        Assert.Equal(1, report.AttachmentsReleased);
    }

    /// <summary>
    /// A teardown that fails is reported and left for next time. (K)
    ///
    /// No retry loop, and nothing about an execution is rewritten: the claim is already gone, so
    /// the attachment is findable again on the next pass.
    /// </summary>
    [Fact]
    public async Task A_failed_teardown_is_reported_and_retried_later()
    {
        var fabric = new RecordingFabric { ThrowOnRelease = true };
        var consumers = Consumers(fabric);

        await ClaimAsync(consumers, "org-x");

        var report = await Reconciler(fabric, consumers, ["org-x"], new HashSet<Guid>())
            .ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, report.AttachmentsReleased);
        Assert.Single(report.Residual);
        Assert.Contains("could not be released", report.Residual[0], StringComparison.Ordinal);

        // The claim did not come back, so a later pass can still find and release the attachment.
        Assert.Empty(consumers.Consumers("org-x"));
    }

    // -- the fast path ---------------------------------------------------------------------------

    /// <summary>
    /// With no registry at all, nothing reaches the environment. (L)
    ///
    /// This runs whenever a session opens, which is every command an operator types. Reaching into
    /// WSL to check on organizations that have never claimed anything would make every invocation
    /// pay for a boundary it is not using.
    /// </summary>
    [Fact]
    public async Task An_absent_registry_never_reaches_the_environment()
    {
        var fabric = new RecordingFabric();

        var report = await Reconciler(fabric, Consumers(fabric), ["org-x", "org-y"], new HashSet<Guid>())
            .ReconcileAsync(CancellationToken.None);

        Assert.True(report.MadeNoChanges);
        Assert.Empty(fabric.ReleasedFor);
        Assert.Empty(fabric.Verified);
        Assert.Equal(0, fabric.Prepared);
    }

    /// <summary>
    /// A known organization with no claims of its own is not touched either. (M, N)
    ///
    /// One organization having a registry entry is not a reason to go looking at another's
    /// environment.
    /// </summary>
    [Fact]
    public async Task An_organization_with_no_claims_is_not_touched()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);

        await ClaimAsync(consumers, "org-x");

        var report = await Reconciler(fabric, consumers, ["org-x", "org-quiet"], new HashSet<Guid>())
            .ReconcileAsync(CancellationToken.None);

        // org-x is reconciled; org-quiet never had a claim, so nothing was asked about it.
        Assert.Equal(["org-x"], fabric.ReleasedFor);
        Assert.Equal(1, report.AttachmentsReleased);
    }

    // -- the facts are asked for, not remembered --------------------------------------------------

    /// <summary>
    /// Each pass reads the truth as it is now, not as it was when the reconciler was built.
    ///
    /// This matters because of what runs immediately before it. The stale execution reconciler has
    /// just marked abandoned executions Interrupted, so a reconciler holding a list captured at
    /// construction would be judging claims against the state that existed before that happened -
    /// preserving a claim for an execution the Hub has already decided is over, and leaving its
    /// organization's attachment alive with nobody coming back for it.
    ///
    /// Asserted by changing the answer between two passes rather than by reading the lambdas.
    /// </summary>
    [Fact]
    public async Task Each_pass_reads_the_current_truth()
    {
        var fabric = new RecordingFabric();
        var consumers = Consumers(fabric);
        var execution = await ClaimAsync(consumers, "org-x");

        // What the store would say, which the test changes between passes.
        var entitled = new HashSet<Guid> { execution };

        var reconciler = new HardNetworkConsumerReconciler(
            _ => Task.FromResult<IReadOnlyList<string>>(["org-x"]),
            _ => Task.FromResult<IReadOnlySet<Guid>>(entitled),
            consumers,
            new FixedProfiles(Profile()),
            fabric);

        var first = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, first.ClaimsDropped);
        Assert.Equal([execution], consumers.Consumers("org-x"));

        // The execution ends - which is exactly what the stale execution reconciler does to an
        // abandoned one just before this runs.
        entitled.Clear();

        var second = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, second.ClaimsDropped);
        Assert.Empty(consumers.Consumers("org-x"));
        Assert.Equal(["org-x"], fabric.ReleasedFor);
    }

    /// <summary>A fabric that records what it was asked to do.</summary>
    private sealed class RecordingFabric : IHardNetworkFabric
    {
        public int Prepared { get; private set; }

        public List<string> ReleasedFor { get; } = [];

        public List<string> Verified { get; } = [];

        public bool ThrowOnRelease { get; set; }

        public Task<HardNetworkAttachment?> PrepareAsync(
            HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
        {
            Prepared++;

            return Task.FromResult<HardNetworkAttachment?>(
                HardNetworkIdentity.Describe(organizationSlug, "/proc/1234/ns/net", "net:[4026532999]"));
        }

        public Task<IReadOnlyList<ReadinessCheck>> VerifyAsync(
            HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
        {
            Verified.Add(organizationSlug);
            return Task.FromResult<IReadOnlyList<ReadinessCheck>>([]);
        }

        public Task ReleaseAsync(
            HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
        {
            if (ThrowOnRelease)
            {
                throw new InvalidOperationException("the veth would not go away");
            }

            ReleasedFor.Add(organizationSlug);
            return Task.CompletedTask;
        }
    }

    /// <summary>One profile for every organization, which is all these decisions need.</summary>
    private sealed class FixedProfiles : IHardIsolationProfileSource
    {
        private readonly HardIsolationProfile _profile;

        public FixedProfiles(HardIsolationProfile profile) => _profile = profile;

        public bool TryGetProfile(string organizationSlug, out HardIsolationProfile? profile, out string? problem)
        {
            profile = _profile;
            problem = null;
            return true;
        }
    }
}
