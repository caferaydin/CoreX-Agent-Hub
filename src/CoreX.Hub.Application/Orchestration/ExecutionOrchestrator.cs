using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

/// <summary>
/// The single owner of execution sequencing.
///
/// It coordinates the workspace, the context pack, an agent runtime, evidence, the four
/// gates, the Hub result commit and verification - through narrow ports, and implements
/// none of them. It runs no process, builds no git command, holds no secret expression,
/// parses no test report and knows no provider: a local harness and a hosted assistant
/// reach exactly the same code here, because there is nothing in it that could tell them
/// apart.
///
/// Two rules shape everything below. Evidence is captured before any verdict, including
/// when the agent itself failed, because partial work has to stay visible. And a database
/// transaction never spans external work: state is committed, the world is touched, then
/// the result is committed.
/// </summary>
public sealed partial class ExecutionOrchestrator
{
    private readonly IHubStore _store;
    private readonly IExecutionWorkspace _workspace;
    private readonly IExecutionContextBuilder _context;
    private readonly IAgentRuntimeGateway _agent;
    private readonly IExecutionVerifier _verifier;
    private readonly IExecutionReviewer? _reviewer;
    private readonly IExecutionEvidenceWriter _evidence;
    private readonly ExecutionSafetyGates _gates;
    private readonly IExecutionLeaseWriter _lease;
    private readonly IProcessLivenessProbe _liveness;
    private readonly TimeProvider _clock;
    private readonly ExecutionOrchestratorOptions _options;
    private readonly SemaphoreSlim _concurrency;

    public ExecutionOrchestrator(
        IHubStore store,
        IExecutionWorkspace workspace,
        IExecutionContextBuilder context,
        IAgentRuntimeGateway agent,
        IExecutionVerifier verifier,
        IExecutionEvidenceWriter evidence,
        ExecutionSafetyGates gates,
        IExecutionLeaseWriter lease,
        IProcessLivenessProbe liveness,
        TimeProvider clock,
        ExecutionOrchestratorOptions options,
        IExecutionReviewer? reviewer = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

        // Optional, and the only optional port here. A repository that names no reviewer is
        // not reviewed, which the Review step records as a skip with a reason - never as a
        // clean review. Composition refuses to build a reviewer it cannot honour, so a null
        // arriving here always means "none was configured" and never "one failed to load".
        _reviewer = reviewer;
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _gates = gates ?? throw new ArgumentNullException(nameof(gates));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _concurrency = new SemaphoreSlim(options.MaxConcurrentExecutions, options.MaxConcurrentExecutions);
    }

    /// <summary>
    /// Runs one attempt at one task, start to finish.
    ///
    /// <paramref name="onExecutionStarted"/> is called once, with this attempt's id, as soon
    /// as the execution row exists and before any external work begins. It exists so a host
    /// can start listening for a request to stop this particular attempt - which it cannot do
    /// earlier, because until the row is written the attempt has no identity to name. The
    /// orchestrator itself learns nothing from it: cancellation still arrives as an ordinary
    /// token, and everything it does about it is the same as if a person had pressed Ctrl-C.
    /// </summary>
    public async Task<ExecutionRunReport> RunAsync(
        Guid taskId,
        string agentProfileName,
        CancellationToken ct,
        Action<Guid>? onExecutionStarted = null)
    {
        // MaxConcurrentExecutions is a local rule for a local-first tool: one semaphore in
        // this process, no queue and no distributed lock. The database's own "one
        // non terminal execution per task" index remains the authority for duplicates.
        await _concurrency.WaitAsync(ct);

        try
        {
            return await RunOnceAsync(taskId, agentProfileName, onExecutionStarted, ct);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
