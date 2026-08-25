using CoreX.Hub.Application.Abstractions;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Hands out a store on its own connection, from the session that owns the database.
///
/// The same arrangement the lease heartbeat already uses, and for a related reason: an EF
/// DbContext cannot be shared, and here it must not be shared even if it could - the point
/// is a change tracker that knows nothing about the write that was interrupted.
/// </summary>
public sealed class DetachedStore : IDetachedStore
{
    private readonly Func<IHubStore> _open;

    public DetachedStore(Func<IHubStore> open) =>
        _open = open ?? throw new ArgumentNullException(nameof(open));

    public IHubStore Open() => _open();
}
