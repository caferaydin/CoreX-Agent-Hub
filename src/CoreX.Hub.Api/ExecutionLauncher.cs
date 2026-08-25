using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Composition;
using CoreX.Hub.Composition.Orchestration;

namespace CoreX.Hub.Api;

/// <summary>
/// Starting an execution from a host that has to answer the request and stay up.
///
/// The shape of the problem: a run takes minutes, an HTTP request must not, and the thing
/// doing the work must still be owned by something after the response is written. The
/// tempting answer is <c>Task.Run(() =&gt; orchestrator.RunAsync(...))</c>, which is not an
/// answer at all - nobody awaits it, nobody knows it exists, and shutdown walks away from a
/// running agent.
///
/// What happens instead is that this class owns the work. Every run is a task held in a
/// dictionary, started under the host's own lifetime token rather than the request's, and
/// awaited at shutdown. Nothing here re-implements ownership: the lease, the heartbeat, the
/// single-writer slot, the process runner and the durable state are the ones Phase 5 built,
/// and the reconciler still recovers anything this process fails to finish.
///
/// The token distinction is the whole safety property. The request token is never passed
/// down. A browser that closes, a proxy that times out, a client that presses stop - none of
/// them reach the agent, because none of them were ever connected to it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExecutionLauncher : IHostedService, IAsyncDisposable
{
    /// <summary>How long a caller waits for the execution row before giving up on the identity.</summary>
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long shutdown waits for running work to finalize durably before letting go.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(90);

    private readonly HubRuntime _runtime;
    private readonly HubApiOptions _options;
    private readonly ILogger<ExecutionLauncher> _log;

    /// <summary>The host's lifetime. Cancelled on shutdown, never by a request.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    private int _disposed;

    public ExecutionLauncher(HubRuntime runtime, HubApiOptions options, ILogger<ExecutionLauncher> log)
    {
        _runtime = runtime;
        _options = options;
        _log = log;
    }

    /// <summary>Executions this process is currently driving. Diagnostic only; the database is authority.</summary>
    public IReadOnlyCollection<Guid> Running => _running.Keys.ToList();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Shutdown asks running work to stop and then waits for it to say it stopped.
    ///
    /// Asking rather than abandoning, because cancellation is a path the orchestrator already
    /// finalizes durably (ADR-0023): the execution is closed with what actually happened
    /// instead of being left Running for the next process to reconcile. Waiting is bounded,
    /// because a host that will not shut down is its own kind of failure - and if the grace
    /// runs out, the reconciler is exactly the thing that handles it.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_running.IsEmpty)
        {
            return;
        }

        _log.LogInformation("shutting down with {Count} execution(s) in flight; asking them to stop", _running.Count);

        await _lifetime.CancelAsync();

        var draining = Task.WhenAll(_running.Values.ToArray());
        var completed = await Task.WhenAny(draining, Task.Delay(ShutdownGrace, cancellationToken)) == draining;

        if (!completed)
        {
            _log.LogWarning(
                "execution(s) did not finalize within the shutdown grace; the next process will reconcile them");
        }
    }

    /// <summary>
    /// Starts an attempt and returns as soon as it has an identity to return.
    ///
    /// The identity comes from the orchestrator's own callback, which fires once the
    /// execution row exists - so the caller is told about something durable, not about a
    /// promise. Everything after that point is watched through the query and event surface.
    /// </summary>
    public async Task<RunStartedDto> StartAsync(
        string taskKey,
        string? organizationSlug,
        string? agentProfile,
        CancellationToken requestAborted)
    {
        if (!_options.AllowExecutionLaunch)
        {
            throw new ExecutionStartException("this host is not configured to start executions.");
        }

        // Opened here and handed to the background task, which owns it from now on: the
        // session has to outlive this method by minutes.
        HubSession? session = await HubSession.OpenAsync(
            _runtime.DataDirectory, _runtime.ConfigPath, TextWriter.Null, requestAborted);

        IDisposable? slot = null;

        try
        {
            var planned = await ExecutionLaunchPlanner.PlanAsync(
                session, taskKey, organizationSlug, agentProfile, requestAborted);

            // One execution at a time for this installation, across processes - the same file
            // handle the CLI claims, so a run started here and a run started at a terminal
            // still cannot happen at once.
            slot = new MachineExecutionSlot(_runtime.DataDirectory).TryClaim();

            if (slot is null)
            {
                throw new ExecutionStartException(
                    "another Hub process is already running an execution for this installation; "
                    + "wait for it to finish or cancel it.");
            }

            var identity = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Handed off. From here the request owns nothing.
            var owned = session!;
            var claimed = slot;
            session = null;
            slot = null;

            var work = DriveAsync(owned, claimed, planned, identity);

            var executionId = await AwaitIdentityAsync(identity, work, requestAborted);

            // Read back rather than guessed: the orchestrator decides which attempt this is.
            await using var reading = _runtime.OpenScope();
            var attemptNo = (await reading.Store.FindExecutionAsync(executionId, CancellationToken.None))?.AttemptNo
                            ?? 0;

            _running[executionId] = work;
            _ = work.ContinueWith(
                _ => _running.TryRemove(executionId, out var _finished),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return new RunStartedDto
            {
                ExecutionId = executionId,
                TaskKey = planned.Task.TaskKey.Value,
                AttemptNo = attemptNo,
                EventsUrl = $"/api/v1/executions/{executionId:D}/events",
            };
        }
        finally
        {
            // Only reached when the hand-off did not happen.
            slot?.Dispose();

            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The background half. Everything it owns is disposed here and nowhere else.
    ///
    /// Runs under the host lifetime token, so the only things that can stop it are a shutdown
    /// or an explicit cancellation through the durable named signal the CLI also uses.
    /// </summary>
    private async Task DriveAsync(
        HubSession session,
        IDisposable slot,
        PlannedExecution planned,
        TaskCompletionSource<Guid> identity)
    {
        // Yield first so the caller's await is never completed inline on this thread.
        await Task.Yield();

        // The same primitive the operator's cancel reaches for, from the same place. A run
        // that listened on something built differently would be a run nobody could stop.
        var signal = HubCancellation.Signal();
        IDisposable? listening = null;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        try
        {
            var report = await planned.Composition.Orchestrator.RunAsync(
                planned.Task.Id,
                planned.AgentProfile.Name,
                stopping.Token,
                executionId =>
                {
                    listening = signal?.Listen(executionId, stopping.Cancel);
                    identity.TrySetResult(executionId);
                });

            identity.TrySetResult(report.ExecutionId);

            _log.LogInformation(
                "execution {ExecutionId} finished as {Outcome}", report.ExecutionId, report.Outcome);
        }
        catch (Exception ex)
        {
            identity.TrySetException(ex);
            _log.LogError(ex, "execution for task {TaskKey} failed to run", planned.Task.TaskKey.Value);
        }
        finally
        {
            listening?.Dispose();
            slot.Dispose();
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Waits for the identity only, and stops waiting if the run fails before producing one.
    ///
    /// A request that gave up is still a run that continues: abandoning the wait never
    /// touches the work, which is the difference between a slow answer and a cancelled agent.
    /// </summary>
    private static async Task<Guid> AwaitIdentityAsync(
        TaskCompletionSource<Guid> identity,
        Task work,
        CancellationToken requestAborted)
    {
        using var give = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        give.CancelAfter(IdentityTimeout);

        var waiting = identity.Task;
        var timeout = Task.Delay(Timeout.Infinite, give.Token);

        var first = await Task.WhenAny(waiting, work, timeout);

        if (first == waiting)
        {
            return await waiting;
        }

        if (first == work)
        {
            // Finished, or threw, before it ever named an execution.
            await work;
            return await waiting;
        }

        throw new ExecutionStartException(
            "the execution did not report an identity in time; it may still be starting - "
            + "check the task's attempts.");
    }

    /// <summary>
    /// Idempotent, because this is reachable twice: the host disposes its services, and a
    /// hosted service is one of them. Disposing a cancelled source a second time must not be
    /// the thing that fails a shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _lifetime.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already gone. Nothing left to ask.
        }

        _lifetime.Dispose();
    }
}
