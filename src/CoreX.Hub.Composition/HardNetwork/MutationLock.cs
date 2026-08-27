using System.Runtime.Versioning;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// One machine, one writer of the trusted network plane.
///
/// The plane is machine-global state: interfaces on a shared namespace, one ruleset every
/// environment sees, and a forwarding flag that belongs to the kernel rather than to any process.
/// Two Hub processes exist in normal use - a CLI beside the API - so a lock inside one process
/// would be a lock that agreed with itself and with nothing else, and the failure it would let
/// through is two writers rendering a policy from two different views of which attachments exist.
///
/// A named system object is the machine's own primitive for exactly this, which is why there is no
/// coordination protocol here. What this type adds is the part that gets forgotten: a wait that
/// gives up rather than blocking forever, and an abandoned lock being treated as acquired - because
/// a process that died holding it left the plane in whatever state it was in, and the next writer
/// is the one who will reconcile that.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MutationLock : IDisposable
{
    private readonly Mutex? _mutex;

    private MutationLock(Mutex? mutex, bool held)
    {
        _mutex = mutex;
        Held = held;
    }

    /// <summary>Whether this caller may write. A false here is a refusal, never a delay.</summary>
    public bool Held { get; }

    /// <summary>
    /// Waits for the machine's plane lock, or gives up.
    ///
    /// Bounded on purpose. An unbounded wait would turn a stuck peer into a hung Hub, and the
    /// caller's honest answer when it cannot get the lock is that it did not prepare anything.
    /// </summary>
    public static MutationLock Acquire(string name, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Mutex? mutex = null;

        try
        {
            mutex = new Mutex(initiallyOwned: false, name);

            bool held;

            try
            {
                held = mutex.WaitOne(timeout, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // The previous writer died holding it. The lock is ours; the state it left behind
                // is what reconciliation is for.
                held = true;
            }

            return new MutationLock(mutex, held);
        }
        catch (Exception) when (mutex is not null)
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (Held)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not ours to release; nothing to undo.
            }
        }

        _mutex.Dispose();
    }
}
