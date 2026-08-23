namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    /// <summary>
    /// Keeps the lease current while a long external stage runs, and stops the moment the
    /// execution is over.
    ///
    /// One timer for the whole execution rather than one per stage, and a bounded cadence
    /// rather than a write per progress event: the point is to prove somebody is still
    /// driving, not to narrate.
    /// </summary>
    private sealed class LeaseHeartbeat : IDisposable
    {
        private readonly ExecutionOrchestrator _orchestrator;
        private readonly Guid _executionId;
        private readonly CancellationTokenSource _stopped = new();
        private readonly Task _loop;

        public LeaseHeartbeat(
            ExecutionOrchestrator orchestrator,
            Guid executionId,
            TimeSpan interval,
            TimeProvider clock)
        {
            _orchestrator = orchestrator;
            _executionId = executionId;
            _loop = BeatAsync(interval, clock, _stopped.Token);
        }

        private async Task BeatAsync(TimeSpan interval, TimeProvider clock, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(interval, clock, ct);

                    // A terminal execution has already released its lease; saying it is
                    // alive would be a lie and would keep the reconciler away from it.
                    var stillRunning = await _orchestrator._lease.BeatAsync(_executionId, clock.GetUtcNow(), ct);

                    if (!stillRunning)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The execution finished; nothing to report.
            }
        }

        public void Dispose()
        {
            _stopped.Cancel();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // A heartbeat that could not stop cleanly must not fail the execution.
            }

            _stopped.Dispose();
        }
    }
}
