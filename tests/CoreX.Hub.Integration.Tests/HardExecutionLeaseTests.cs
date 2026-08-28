using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A machine that answers, so the lease's own ordering can be tested as ordering.
///
/// Opening a channel and placing an executor are the only parts of acquiring a lease that touch an
/// operating system. Everything else - what is claimed, in what order, and what is unwound when a
/// stage refuses - is policy, and policy is the part worth proving. So the machine is scripted and
/// the lease is real; nothing here starts a distribution or touches a developer's WSL.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ScriptedEnvironmentChannel : IHardEnvironmentChannel
{
    private readonly IWslExecutionGateway _gateway;

    public ScriptedEnvironmentChannel(IWslExecutionGateway gateway) => _gateway = gateway;

    /// <summary>Set when the environment cannot be reached at all.</summary>
    public string? Unreachable { get; set; }

    public List<HardWorkloadPlacement> Placements { get; } = [];

    public bool TryOpen(out IWslExecutionGateway? gateway, out string? problem)
    {
        problem = Unreachable;
        gateway = Unreachable is null ? _gateway : null;

        return Unreachable is null;
    }

    public IHardProcessExecutor PlacedExecutor(HardWorkloadPlacement placement)
    {
        Placements.Add(placement);

        return new UnusedExecutor();
    }

    /// <summary>Nothing in these tests runs a workload; one that tried would say so loudly.</summary>
    private sealed class UnusedExecutor : IHardProcessExecutor
    {
        public Task<HardProcessResult> RunAsync(
            HardIsolationProfile profile, HardProcessRequest request, CancellationToken ct) =>
            throw new InvalidOperationException(
                "these tests acquire and release boundaries; they do not run workloads.");
    }
}

/// <summary>
/// The production lease path, proved not to bypass durable ownership.
///
/// The ownership layer was built, frozen and then reached by nothing: the lease still called the
/// physical fabric directly, so two executions of one organization would have had the first to
/// finish tear the namespace out from under the second. The layer's own tests could not catch that,
/// because they exercised the layer rather than the caller - which is exactly why these drive the
/// real <see cref="HardExecutionLease"/> instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardExecutionLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "corex-lease-" + Guid.CreateVersion7().ToString("N"));

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
    /// Two executions of one organization, through the real lease.
    ///
    /// The regression, written where the defect was. Releasing the first must leave the second's
    /// boundary standing; only the last one out takes the attachment with it.
    /// </summary>
    [Fact]
    public async Task Two_executions_of_one_organization_share_one_attachment()
    {
        var fabric = new ScriptedFabric();
        var consumers = new HardNetworkConsumers(fabric, _root);
        var channel = new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment());

        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var (a, aRefusal) = await HardExecutionLease.AcquireAsync(
            consumers, channel, Profile(), "org-x", first, CancellationToken.None);

        var (b, bRefusal) = await HardExecutionLease.AcquireAsync(
            consumers, channel, Profile(), "org-x", second, CancellationToken.None);

        Assert.Null(aRefusal);
        Assert.Null(bRefusal);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // One organization, one attachment: both executions are placed in the same namespace, which
        // is the whole reason the first must not release it.
        Assert.Equal(a!.Placement, b!.Placement);

        await a.DisposeAsync();

        Assert.Equal(0, fabric.Released);
        Assert.Equal([second], consumers.Consumers("org-x"));

        await b.DisposeAsync();

        Assert.Equal(1, fabric.Released);
        Assert.Empty(consumers.Consumers("org-x"));
    }

    /// <summary>
    /// Disposal is idempotent, and a second one does not release somebody else's claim.
    ///
    /// The shape that produced a real defect once already: a caller holding no claim reaching
    /// physical teardown.
    /// </summary>
    [Fact]
    public async Task Releasing_twice_releases_once()
    {
        var fabric = new ScriptedFabric();
        var consumers = new HardNetworkConsumers(fabric, _root);
        var channel = new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment());

        var peer = Guid.CreateVersion7();

        var (lease, _) = await HardExecutionLease.AcquireAsync(
            consumers, channel, Profile(), "org-x", Guid.CreateVersion7(), CancellationToken.None);

        await lease!.DisposeAsync();
        Assert.Equal(1, fabric.Released);

        // A peer arrives and takes the attachment again.
        Assert.NotNull(await consumers.AcquireAsync(Profile(), "org-x", peer, CancellationToken.None));

        await lease.DisposeAsync();

        // The second disposal did not reach the plane, and did not evict the peer.
        Assert.Equal(1, fabric.Released);
        Assert.Equal([peer], consumers.Consumers("org-x"));
    }

    /// <summary>
    /// An attachment the plane will not prepare is a refusal, and leaves no claim behind.
    /// </summary>
    [Fact]
    public async Task A_refused_attachment_leaves_no_claim()
    {
        var consumers = new HardNetworkConsumers(new ScriptedFabric { Refuse = true }, _root);
        var channel = new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment());

        var (lease, refusal) = await HardExecutionLease.AcquireAsync(
            consumers, channel, Profile(), "org-x", Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(lease);
        Assert.Equal("network", refusal!.Stage);
        Assert.Empty(consumers.Consumers("org-x"));
        Assert.Empty(consumers.ClaimedAttachments());
    }

    /// <summary>An environment that cannot be reached refuses before anything is claimed.</summary>
    [Fact]
    public async Task An_unreachable_environment_claims_nothing()
    {
        var fabric = new ScriptedFabric();
        var consumers = new HardNetworkConsumers(fabric, _root);

        var channel = new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment())
        {
            Unreachable = "no distribution",
        };

        var (lease, refusal) = await HardExecutionLease.AcquireAsync(
            consumers, channel, Profile(), "org-x", Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(lease);
        Assert.Equal("environment", refusal!.Stage);
        Assert.Equal(0, fabric.Prepared);
        Assert.Empty(consumers.ClaimedAttachments());
    }

    /// <summary>
    /// A lease cannot be taken for an execution that has no identity.
    ///
    /// Empty is not a default. The claim written on disk is named by this value, and one nobody can
    /// name is a claim reconciliation cannot classify after a crash.
    /// </summary>
    [Fact]
    public async Task An_empty_execution_identity_is_refused()
    {
        var fabric = new ScriptedFabric();
        var consumers = new HardNetworkConsumers(fabric, _root);
        var channel = new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => HardExecutionLease.AcquireAsync(
                consumers, channel, Profile(), "org-x", Guid.Empty, CancellationToken.None));

        Assert.Equal(0, fabric.Prepared);
        Assert.Empty(consumers.ClaimedAttachments());
    }

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
