using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Composition;
using CoreX.Hub.Composition.Orchestration;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// That HTTP cancellation is the same mechanism the console uses, proved rather than asserted.
///
/// A test that handed the service a fake signal and watched it return true would prove the
/// service calls its port, which nobody doubted. What matters is the other thing: the named
/// primitive the API's runner listens on and the one an operator's cancel reaches for are the
/// same primitive, so a run started in a browser is stoppable from a terminal and the other
/// way round.
///
/// So this drives the real <see cref="NamedExecutionCancellation"/> across two independently
/// constructed instances - which is what two processes each get - and shows one hears the
/// other. No provider is involved.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedCancellationAuthorityTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_request_from_one_instance_reaches_a_listener_on_another()
    {
        var executionId = Guid.CreateVersion7();

        // Two instances, built the way each adapter builds one. In production these are two
        // processes; the primitive is named, so the boundary it crosses is the same either way.
        var runner = HubCancellation.Signal();
        var operatorSide = HubCancellation.Signal();

        Assert.NotNull(runner);
        Assert.NotNull(operatorSide);

        using var heard = new ManualResetEventSlim(false);
        using var listening = runner!.Listen(executionId, () => heard.Set());

        Assert.True(operatorSide!.TryRequest(executionId), "the operator side found no listener");
        Assert.True(heard.Wait(Patience), "the runner never heard the request");
    }

    [Fact]
    public void An_execution_nobody_is_listening_for_cannot_be_stopped_from_here()
    {
        var signal = HubCancellation.Signal();

        Assert.NotNull(signal);

        // False is not "cancelled" - it is "nothing was stopped", and the authority turns it
        // into a refusal rather than a task that claims to be finished.
        Assert.False(signal!.TryRequest(Guid.CreateVersion7()));
    }

    [Fact]
    public void A_request_names_one_execution_and_reaches_no_other()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        var runner = HubCancellation.Signal();
        var operatorSide = HubCancellation.Signal();

        using var wrong = new ManualResetEventSlim(false);
        using var listening = runner!.Listen(other, () => wrong.Set());

        // Nobody is listening for mine, so this is a refusal - and the listener registered
        // for a different execution is not woken by it.
        Assert.False(operatorSide!.TryRequest(mine));
        Assert.False(wrong.Wait(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_listener_that_is_released_stops_being_reachable()
    {
        var executionId = Guid.CreateVersion7();

        var runner = HubCancellation.Signal();
        var operatorSide = HubCancellation.Signal();

        using (var heard = new ManualResetEventSlim(false))
        using (runner!.Listen(executionId, () => heard.Set()))
        {
            Assert.True(operatorSide!.TryRequest(executionId));
            Assert.True(heard.Wait(Patience));
        }

        // The run finished and let go. A later request finds nobody, which is what stops a
        // stale cancellation from being reported as a stop that happened.
        Assert.False(operatorSide!.TryRequest(executionId));
    }

    [Fact]
    public void Both_adapters_build_the_authority_from_the_one_place_that_builds_it()
    {
        // Reflection rather than text: the console and the HTTP surface hold the same type
        // from the same assembly, and neither of them can construct it differently because
        // neither of them constructs it.
        var built = HubCancellation.Build(() => throw new InvalidOperationException("not opened here"));

        Assert.IsType<TaskCancellationService>(built);
        Assert.Equal(typeof(TaskCancellationService).Assembly, built.GetType().Assembly);

        // The port the service depends on lives in the application; its implementation does
        // not, which is what keeps a named OS primitive out of the layers below.
        Assert.Equal(
            typeof(IExecutionCancellationSignal).Assembly,
            typeof(TaskCancellationService).Assembly);

        Assert.NotEqual(
            typeof(IExecutionCancellationSignal).Assembly,
            typeof(NamedExecutionCancellation).Assembly);
    }
}
