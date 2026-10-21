using System.Reflection;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Integration.Tests;

/// <summary>A runtime that cannot be placed inside a boundary. Most real ones cannot.</summary>
internal sealed class HostOnlyRuntime : IAgentRuntime
{
    public AgentCapabilities Describe() => AgentCapabilities.None with
    {
        SupportsReadOnlyMode = true,
        SupportsStructuredSchema = true,
    };

    public Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("nothing in these tests runs a runtime.");

    public Task<AgentRunResult> RunAsync(
        AgentRunRequest request, IProgress<AgentEvent> sink, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "the wrapped runtime's own RunAsync is never reached on the hard path.");
}

/// <summary>A runtime that splits deciding a command from interpreting its result.</summary>
internal sealed class BoundaryCapableRuntime : IAgentRuntime, IHardCapableAgentRuntime
{
    public AgentCapabilities Describe() => AgentCapabilities.None with
    {
        SupportsReadOnlyMode = true,
        SupportsStructuredSchema = true,
    };

    public Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("nothing in these tests runs a runtime.");

    public Task<AgentRunResult> RunAsync(
        AgentRunRequest request, IProgress<AgentEvent> sink, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "the wrapped runtime's own RunAsync is never reached on the hard path.");

    public HardAgentCommand DescribeCommand(AgentRunRequest request) =>
        new() { Executable = "/opt/corex/bin/fixture" };

    public AgentRunResult Interpret(AgentRunRequest request, HardAgentProcessResult process) =>
        new() { Outcome = AgentRunOutcome.Completed, Duration = process.Duration };
}

/// <summary>
/// Which side of the boundary an execution runs on, and how many times that is decided.
///
/// The failure these exist for is not "hard did not work". It is an execution that is hard in four
/// of its five ports: an agent perfectly contained whose verification runs on Windows has moved the
/// problem to the step that executes the repository's own test suite, and every log line still says
/// the execution was isolated. So the interesting assertions here are about coherence - one
/// decision, one environment, no mixed set - rather than about any single port working.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardExecutionRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "corex-routing-" + Guid.CreateVersion7().ToString("N"));

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

    // -- the routing decision -----------------------------------------------------------------

    /// <summary>A logical organization is untouched by any of this.</summary>
    [Fact]
    public void A_logical_organization_routes_logically()
    {
        var routing = ExecutionIsolationRouting.For(
            IsolationLevel.Logical, null, planeAvailable: false, "no plane", new HostOnlyRuntime(), "host-only");

        Assert.False(routing.IsHard);
        Assert.False(routing.IsRefused);
    }

    /// <summary>
    /// A. An unavailable plane refuses a hard organization; it does not demote it.
    ///
    /// An organization declared hard is an operator saying the work must be contained. Running it
    /// uncontained because containment was unavailable answers a question nobody asked.
    /// </summary>
    [Fact]
    public void A_an_unavailable_plane_refuses_rather_than_demotes()
    {
        var routing = ExecutionIsolationRouting.For(
            IsolationLevel.Hard, Profile(), planeAvailable: false, "this machine has no environment",
            new BoundaryCapableRuntime(), "boundary-capable");

        Assert.True(routing.IsRefused);
        Assert.True(routing.IsHard);
        Assert.Contains("no environment", routing.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A hard organization with no configuration is refused before anything is built.</summary>
    [Fact]
    public void An_unconfigured_hard_organization_is_refused()
    {
        var routing = ExecutionIsolationRouting.For(
            IsolationLevel.Hard, null, planeAvailable: true, string.Empty,
            new BoundaryCapableRuntime(), "boundary-capable");

        Assert.True(routing.IsRefused);
    }

    /// <summary>
    /// E, F. An incompatible runtime is a refusal, and never a host run.
    ///
    /// Decided by a type test rather than a provider name, so nothing has to be kept current as
    /// providers change - a runtime either implements the seam or it does not.
    /// </summary>
    [Fact]
    public void EF_an_incompatible_runtime_is_refused_with_no_fallback()
    {
        var routing = ExecutionIsolationRouting.For(
            IsolationLevel.Hard, Profile(), planeAvailable: true, string.Empty,
            new HostOnlyRuntime(), "host-only");

        Assert.True(routing.IsRefused);
        Assert.Contains("does not fall back", routing.Refusal!, StringComparison.Ordinal);

        // The reviewer half of the same question, asked before a repository is told it is reviewed.
        Assert.False(HardReviewerCompatibility.CanReviewInsideBoundary(new HostOnlyRuntime()));
        Assert.True(HardReviewerCompatibility.CanReviewInsideBoundary(new BoundaryCapableRuntime()));
    }

    /// <summary>G. Everything present and compatible routes hard.</summary>
    [Fact]
    public void G_a_ready_hard_organization_routes_hard()
    {
        var routing = ExecutionIsolationRouting.For(
            IsolationLevel.Hard, Profile(), planeAvailable: true, string.Empty,
            new BoundaryCapableRuntime(), "boundary-capable");

        Assert.True(routing.IsHard);
        Assert.False(routing.IsRefused);
    }

    /// <summary>
    /// J. The plane being available does not move anybody.
    ///
    /// A machine capable of hard isolation is a machine capable of it. Every logical organization on
    /// it stays logical, which is the difference between a per-execution decision and a global
    /// switch.
    /// </summary>
    [Fact]
    public void J_an_available_plane_does_not_move_a_logical_organization()
    {
        var routing = ExecutionIsolationRouting.For(
            IsolationLevel.Logical, Profile(), planeAvailable: true, string.Empty,
            new BoundaryCapableRuntime(), "boundary-capable");

        Assert.False(routing.IsHard);
    }

    // -- one environment for the whole execution ------------------------------------------------

    /// <summary>
    /// L. Every port that binds one execution gets the identical environment.
    ///
    /// Not an equal one - the same object. Five adapters each preparing what they needed would each
    /// be isolated while the execution was not, and every test that checked one of them alone would
    /// still pass.
    /// </summary>
    [Fact]
    public void L_all_five_ports_share_one_environment()
    {
        var resolver = Resolver();
        var execution = Guid.CreateVersion7();

        var workspace = resolver.Bind(execution);
        var evidence = resolver.Bind(execution);
        var agent = resolver.Bind(execution);
        var verifier = resolver.Bind(execution);
        var reviewer = resolver.Bind(execution);

        Assert.Same(workspace, evidence);
        Assert.Same(workspace, agent);
        Assert.Same(workspace, verifier);
        Assert.Same(workspace, reviewer);
        Assert.Equal(execution, resolver.BoundExecutionId);
    }

    /// <summary>
    /// H, I, K. A second execution cannot borrow the first one's composition.
    ///
    /// K is the interesting half. Once a route is admitted it governs the whole execution, so a
    /// later port arriving with a different identity is not served under the same environment - it is
    /// refused, because the alternative is one execution silently running inside another's boundary
    /// with both looking fine.
    /// </summary>
    [Fact]
    public void HIK_a_second_execution_cannot_rebind_the_composition()
    {
        var resolver = Resolver();
        var first = Guid.CreateVersion7();

        resolver.Bind(first);

        var refused = Assert.Throws<InvalidOperationException>(() => resolver.Bind(Guid.CreateVersion7()));

        Assert.Contains("bound to execution", refused.Message, StringComparison.Ordinal);
        Assert.Equal(first, resolver.BoundExecutionId);
    }

    /// <summary>Two composers race on the first bind, and one environment comes out.</summary>
    [Fact]
    public void Concurrent_first_binds_produce_one_environment()
    {
        var resolver = Resolver();
        var execution = Guid.CreateVersion7();

        var bound = new HardExecutionEnvironment[16];

        Parallel.For(0, bound.Length, i => bound[i] = resolver.Bind(execution));

        Assert.All(bound, e => Assert.Same(bound[0], e));
    }

    /// <summary>
    /// The resolver takes nothing until an execution asks it to.
    ///
    /// A composition is built for executions that are about to be refused as well as for ones that
    /// run, so provisioning here would provision for both.
    /// </summary>
    [Fact]
    public void Creating_a_resolver_claims_nothing()
    {
        var fabric = new ScriptedFabric();
        var consumers = new HardNetworkConsumers(fabric, _root);

        var resolver = new HardExecutionEnvironmentResolver(
            consumers,
            new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment()),
            new SoundBoundaryGate(),
            new TrustedGuestFileTransfer(new ScriptedExecutionEnvironment(), Profile()),
            Profile(),
            "org-x");

        Assert.Equal(Guid.Empty, resolver.BoundExecutionId);
        Assert.False(resolver.HasLiveEnvironment);
        Assert.Equal(0, fabric.Prepared);
        Assert.Empty(consumers.ClaimedAttachments());

        // Binding is still not acquiring: the environment exists, the boundary does not.
        resolver.Bind(Guid.CreateVersion7());

        Assert.False(resolver.HasLiveEnvironment);
        Assert.Equal(0, fabric.Prepared);
        Assert.Empty(consumers.ClaimedAttachments());
    }

    /// <summary>An execution with no identity is refused at every hard entry point.</summary>
    [Fact]
    public void An_empty_execution_identity_is_refused_everywhere()
    {
        var resolver = Resolver();

        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Bind(Guid.Empty));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HardExecutionEnvironment(
                new HardNetworkConsumers(new ScriptedFabric(), _root),
                new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment()),
                new SoundBoundaryGate(),
                Profile(),
                "org-x",
                Guid.Empty));
    }

    // -- the port set -------------------------------------------------------------------------

    /// <summary>
    /// A mixed set has no constructor, and neither factory takes fewer than five.
    ///
    /// The type system carrying the rule rather than a review comment. A future change that isolates
    /// the agent and forgets the reviewer does not compile.
    /// </summary>
    [Fact]
    public void A_mixed_port_set_has_no_public_path()
    {
        var type = typeof(ExecutionPortSet);

        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var factories = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == type)
            .ToList();

        Assert.Equal(2, factories.Count);
        Assert.All(factories, m => Assert.Equal(5, m.GetParameters().Length));

        // The flag describes what the set already is; it is not an argument somebody could set
        // differently from the implementations beside it.
        Assert.DoesNotContain(
            factories.SelectMany(m => m.GetParameters()),
            p => p.ParameterType == typeof(bool));
    }

    // -- fixtures -----------------------------------------------------------------------------

    private HardExecutionEnvironmentResolver Resolver() =>
        new(
            new HardNetworkConsumers(new ScriptedFabric(), _root),
            new ScriptedEnvironmentChannel(new ScriptedExecutionEnvironment()),
            new SoundBoundaryGate(),
            new TrustedGuestFileTransfer(new ScriptedExecutionEnvironment(), Profile()),
            Profile(),
            "org-x");

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
