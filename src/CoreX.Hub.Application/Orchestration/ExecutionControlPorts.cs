namespace CoreX.Hub.Application.Orchestration;

/// <summary>
/// How one process asks another to stop a particular execution.
///
/// The Hub is a local-first tool with no daemon, so "the process running the work" and
/// "the process the operator typed into" are two command line invocations on one machine
/// and nothing else. This is the whole of the channel between them: no socket, no queue,
/// no broker and no database poll pretending to be one.
///
/// Everything is keyed by execution id rather than by task, and that is the safety
/// property. A request names one attempt; a signal left over from an attempt that has
/// already finished cannot reach the next one, because the next one has a different id.
///
/// The port exists so the Application can express "ask the runner to stop" without knowing
/// what an operating system handle is. Its implementation lives in the composition layer.
/// </summary>
public interface IExecutionCancellationSignal
{
    /// <summary>
    /// Listens for a request to cancel <paramref name="executionId"/>, for as long as the
    /// returned handle is held.
    ///
    /// Registering is what makes an execution cancellable at all: an execution nobody is
    /// listening for cannot be stopped by a signal, and a caller must treat that as a
    /// refusal rather than as success.
    /// </summary>
    IDisposable Listen(Guid executionId, Action onRequested);

    /// <summary>
    /// Asks whoever is running <paramref name="executionId"/> to stop, and reports whether
    /// there was anybody there to hear it.
    ///
    /// False means no listener - which is not the same as "cancelled": nothing has been
    /// stopped, and the caller must not record a stop that did not happen.
    /// </summary>
    bool TryRequest(Guid executionId);
}

/// <summary>
/// The one-execution-at-a-time rule, across processes on this machine.
///
/// <see cref="ExecutionOrchestratorOptions.MaxConcurrentExecutions"/> is the rule inside a
/// process. A second command line invocation is a second process and a second semaphore,
/// so the in-process rule says nothing about it - and two runs of the same local tool on
/// the same repository root would trip over each other's git operations.
///
/// This is deliberately a machine-local claim and nothing more. It is not a distributed
/// lock, it does not survive the machine, and it does not coordinate anything beyond
/// "somebody here is already running an execution".
/// </summary>
public interface IHubExecutionSlot
{
    /// <summary>
    /// Claims the right to run an execution on this machine, or returns null when another
    /// process already holds it. The claim is released when the handle is disposed, and
    /// also when the holding process dies.
    /// </summary>
    IDisposable? TryClaim();
}
