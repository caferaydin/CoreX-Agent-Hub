using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// The channel between two command line processes on one machine, and nothing more.
///
/// A named event is the narrowest thing that does this job on Windows. It needs no daemon,
/// no port, no broker and no database poll; the kernel already knows how to let one process
/// signal another, and it already knows how to forget the whole arrangement when the
/// listening process dies. That last part is the useful one: a runner that was killed leaves
/// nothing behind that could be mistaken for a runner that is still there.
///
/// The name carries the execution id, so a request is about one attempt and cannot be
/// misdelivered. A stale request for a finished attempt reaches nobody, because the object
/// it named ceased to exist with the process that created it, and the next attempt has a
/// different id and therefore a different name.
///
/// Session-local on purpose: this coordinates one person's Hub commands, not a machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedExecutionCancellation : IExecutionCancellationSignal
{
    private const string Prefix = @"Local\CoreXAgentHub.Cancel.";

    public IDisposable Listen(Guid executionId, Action onRequested)
    {
        ArgumentNullException.ThrowIfNull(onRequested);

        // Manual reset: the request is a fact about this execution once it has been made,
        // not a token one waiter can consume before another sees it.
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, Prefix + executionId.ToString("N"));

        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, timedOut) =>
            {
                if (timedOut)
                {
                    return;
                }

                try
                {
                    onRequested();
                }
                catch (ObjectDisposedException)
                {
                    // The run finished between the request arriving and this callback
                    // getting to it. Nothing left to stop, and a background thread must not
                    // take the process down over it.
                }
            },
            state: null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);

        return new Listener(handle, registration);
    }

    public bool TryRequest(Guid executionId)
    {
        if (!EventWaitHandle.TryOpenExisting(Prefix + executionId.ToString("N"), out var handle))
        {
            // Nobody is listening: either the run finished or its host is gone. Both mean
            // this request stops nothing, and the caller has to be told so rather than being
            // allowed to record a stop that did not happen.
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    private sealed class Listener(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }
}
