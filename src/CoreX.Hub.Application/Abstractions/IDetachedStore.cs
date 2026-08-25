namespace CoreX.Hub.Application.Abstractions;

/// <summary>
/// Opens a persistence scope that shares nothing with the one already in use.
///
/// It exists for one situation, and the situation is narrow enough to be worth stating: a
/// write was interrupted, and the work that has to finish afterwards cannot be done through
/// the store that was writing it.
///
/// Two things go wrong if it is. A transition is a mutation followed by a save, and a save
/// that was cancelled leaves the tracked aggregate already mutated while the row is not -
/// so asking that store what state the execution is in returns the intention rather than
/// the fact, because the identity map hands back the instance that was changed. And saving
/// anything at all through it would flush those abandoned mutations, writing a transition
/// that was already given up on.
///
/// Deliberately not a repository abstraction, a unit of work or a transaction manager. It
/// returns the same <see cref="IHubStore"/> everything else uses; the only thing it adds is
/// that the one it returns is new. The lease heartbeat already needed exactly this for its
/// own reason, so the shape is not novel either.
/// </summary>
public interface IDetachedStore
{
    /// <summary>
    /// A store on its own connection and its own change tracker.
    ///
    /// Its lifetime belongs to whoever provides it - the same arrangement the heartbeat's
    /// store has - so callers here do not dispose it.
    /// </summary>
    IHubStore Open();
}
