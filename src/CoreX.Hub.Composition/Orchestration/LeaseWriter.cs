using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Keeps a lease alive on its own connection.
///
/// The orchestrator's store is busy inside an agent or a verification run when a beat is
/// due, and an EF DbContext cannot be used from two places at once. Each beat therefore
/// opens its own short lived store, writes one column and closes - which is what SQLite
/// in WAL mode handles well, and what keeps the heartbeat from having to wait for the
/// stage it is reporting on.
/// </summary>
public sealed class LeaseWriter : IExecutionLeaseWriter
{
    private readonly Func<IHubStore> _open;

    public LeaseWriter(Func<IHubStore> open) =>
        _open = open ?? throw new ArgumentNullException(nameof(open));

    public async Task<bool> BeatAsync(Guid executionId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var store = _open();

        var execution = await store.FindExecutionAsync(executionId, ct);

        if (execution is null || execution.IsTerminal)
        {
            return false;
        }

        execution.Heartbeat(nowUtc);
        await store.SaveChangesAsync(ct);

        return true;
    }
}
