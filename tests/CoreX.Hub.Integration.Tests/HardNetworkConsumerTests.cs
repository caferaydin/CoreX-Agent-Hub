using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Who owns an organization's network attachment, and when it is safe to take it away.
///
/// The defect these were written for: a physical attachment belongs to an organization and is
/// shared by everything that organization runs, while a lease belongs to one execution. The first
/// execution to finish was calling an unconditional teardown, so a second execution of the same
/// organization lost its namespace holder in the middle of its own run.
///
/// The fabric is scripted here so the ownership decisions can be tested as decisions. Whether a
/// veth is really deleted is the fabric's business and is proven against a real machine elsewhere;
/// what matters here is whether the teardown is called at all, and that is a question with an
/// answer that must not depend on which machine the suite runs on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkConsumerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "corex-consumers-" + Guid.CreateVersion7().ToString("N"));

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

    private HardNetworkConsumers Consumers(ScriptedFabric fabric) => new(fabric, _root);

    // -- the case the layer exists for ---------------------------------------------------------

    /// <summary>
    /// Two executions of one organization share the attachment, and the first to leave does not
    /// take it with them.
    ///
    /// This is the defect, written as a test. Before the ownership layer, releasing A deleted the
    /// veth and stopped the holder while B was still inside the namespace - so B's verification and
    /// review could no longer launch, failing closed but failing.
    /// </summary>
    [Fact]
    public async Task The_first_execution_to_finish_does_not_take_the_attachment_with_it()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        Assert.NotNull(await consumers.AcquireAsync(Profile(), "org-x", a, CancellationToken.None));
        Assert.NotNull(await consumers.AcquireAsync(Profile(), "org-x", b, CancellationToken.None));

        // One organization, one physical attachment. The second acquisition reuses it rather than
        // building a second one.
        Assert.Equal(2, fabric.Prepared);
        Assert.Equal(["org-x", "org-x"], fabric.PreparedFor);

        await consumers.ReleaseAsync(Profile(), "org-x", a, CancellationToken.None);

        // B is still working. The holder and the veth are still B's.
        Assert.Equal(0, fabric.Released);
        Assert.Equal([b], consumers.Consumers("org-x"));

        await consumers.ReleaseAsync(Profile(), "org-x", b, CancellationToken.None);

        // Last one out.
        Assert.Equal(1, fabric.Released);
        Assert.Empty(consumers.Consumers("org-x"));
    }

    /// <summary>One execution asking repeatedly is one consumer, not several.</summary>
    [Fact]
    public async Task One_execution_asking_twice_is_one_consumer()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var execution = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", execution, CancellationToken.None);
        await consumers.AcquireAsync(Profile(), "org-x", execution, CancellationToken.None);

        Assert.Equal([execution], consumers.Consumers("org-x"));

        await consumers.ReleaseAsync(Profile(), "org-x", execution, CancellationToken.None);

        Assert.Equal(1, fabric.Released);
    }

    /// <summary>Releasing twice releases once. A second call has nothing left to give up.</summary>
    [Fact]
    public async Task Releasing_the_same_execution_twice_tears_down_once()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var execution = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", execution, CancellationToken.None);
        await consumers.ReleaseAsync(Profile(), "org-x", execution, CancellationToken.None);
        await consumers.ReleaseAsync(Profile(), "org-x", execution, CancellationToken.None);

        // The second release held nothing, so it gave nothing up. Reaching the physical teardown
        // again would be a caller with no claim tearing down an attachment - which with no
        // consumers left is indistinguishable from a legitimate last-consumer release.
        Assert.Equal(1, fabric.Released);
        Assert.Empty(consumers.Consumers("org-x"));
    }

    /// <summary>Organizations are counted apart; one finishing says nothing about another.</summary>
    [Fact]
    public async Task Organizations_are_counted_separately()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", a, CancellationToken.None);
        await consumers.AcquireAsync(Profile(), "org-y", b, CancellationToken.None);

        await consumers.ReleaseAsync(Profile(), "org-x", a, CancellationToken.None);

        Assert.Equal(["org-x"], fabric.ReleasedFor);
        Assert.Equal([b], consumers.Consumers("org-y"));
    }

    // -- failure during acquisition -------------------------------------------------------------

    /// <summary>
    /// An attachment that could not be prepared leaves no claim behind.
    ///
    /// A claim with nothing under it would keep a later release from tearing down an attachment
    /// that some other execution really did establish.
    /// </summary>
    [Fact]
    public async Task A_failed_preparation_leaves_no_claim()
    {
        var fabric = new ScriptedFabric { Refuse = true };
        var consumers = Consumers(fabric);
        var execution = Guid.CreateVersion7();

        Assert.Null(await consumers.AcquireAsync(Profile(), "org-x", execution, CancellationToken.None));
        Assert.Empty(consumers.Consumers("org-x"));
    }

    /// <summary>
    /// A failed preparation does not remove a claim that was already there.
    ///
    /// Two executions, and the second one's preparation fails. The first one is still working and
    /// its claim is not collateral damage.
    /// </summary>
    [Fact]
    public async Task A_failed_preparation_does_not_remove_someone_elses_claim()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", a, CancellationToken.None);

        fabric.Refuse = true;

        Assert.Null(await consumers.AcquireAsync(Profile(), "org-x", b, CancellationToken.None));
        Assert.Equal([a], consumers.Consumers("org-x"));
    }

    /// <summary>
    /// A teardown that fails does not resurrect the claim to hide it.
    ///
    /// The attachment is Hub owned and has no consumers, which is exactly the state reconciliation
    /// can find and release later. Putting the claim back would make it invisible to that.
    /// </summary>
    [Fact]
    public async Task A_failed_teardown_is_reported_and_leaves_no_claim()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var execution = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", execution, CancellationToken.None);

        fabric.ThrowOnRelease = true;

        var residual = await consumers.ReleaseAsync(Profile(), "org-x", execution, CancellationToken.None);

        Assert.NotNull(residual);
        Assert.Contains("could not be released", residual!, StringComparison.Ordinal);
        Assert.Empty(consumers.Consumers("org-x"));
    }

    // -- reconciliation --------------------------------------------------------------------------

    /// <summary>
    /// Claims belonging to executions that are no longer entitled to one are dropped.
    ///
    /// The verdict comes from the caller, because deciding whether an execution is still live is
    /// the lifecycle's job and an ownership registry that decided it would be a second opinion.
    /// </summary>
    [Fact]
    public async Task Claims_from_executions_that_are_finished_are_forgotten()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var live = Guid.CreateVersion7();
        var finished = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", live, CancellationToken.None);
        await consumers.AcquireAsync(Profile(), "org-x", finished, CancellationToken.None);

        var dropped = consumers.Forget("org-x", id => id == live);

        Assert.Equal([finished], dropped);
        Assert.Equal([live], consumers.Consumers("org-x"));
    }

    /// <summary>
    /// An id nothing can classify keeps its claim.
    ///
    /// Preserving an attachment nobody is using is visible and releasable later. Tearing down a
    /// namespace somebody is working in is not recoverable, so ambiguity resolves the safe way.
    /// </summary>
    [Fact]
    public async Task An_unclassifiable_claim_is_preserved()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);
        var unknown = Guid.CreateVersion7();

        await consumers.AcquireAsync(Profile(), "org-x", unknown, CancellationToken.None);

        // The caller cannot say, so it says yes: entitled until proven otherwise.
        Assert.Empty(consumers.Forget("org-x", _ => true));
        Assert.Equal([unknown], consumers.Consumers("org-x"));
    }

    /// <summary>Every organization holding claims is discoverable, so reconciliation can walk them.</summary>
    [Fact]
    public async Task Claimed_organizations_are_discoverable()
    {
        var fabric = new ScriptedFabric();
        var consumers = Consumers(fabric);

        await consumers.AcquireAsync(Profile(), "org-x", Guid.CreateVersion7(), CancellationToken.None);
        await consumers.AcquireAsync(Profile(), "org-y", Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(2, consumers.ClaimedAttachments().Count);
    }

    /// <summary>An execution with no durable identity is not a consumer of anything.</summary>
    [Fact]
    public async Task An_execution_with_no_identity_cannot_claim()
    {
        var fabric = new ScriptedFabric();

        Assert.Null(await Consumers(fabric).AcquireAsync(
            Profile(), "org-x", Guid.Empty, CancellationToken.None));

        Assert.Equal(0, fabric.Prepared);
    }

    /// <summary>A fabric that records what it was asked to do, and can be told to refuse.</summary>
}
