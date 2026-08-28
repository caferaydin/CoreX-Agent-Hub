using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Runs an agent runtime as an untrusted hard workload.
///
/// A decorator rather than a new provider, and deliberately so. Which runtime an execution gets is
/// the agent profile's decision and stays that way; what this changes is only where it runs. The
/// inner runtime describes the command it wants - an executable and an argument vector, the same
/// pair it would hand a Windows process launch - and this puts that command inside the boundary.
///
/// The runtime is never told any of it. There is no WSL, systemd, namespace or mount vocabulary in
/// what it receives, which is what keeps provider neutrality real: a runtime cannot ask for a
/// weaker boundary because there is nothing in its contract that could express one, and a hard
/// organization does not get a different provider from a logical one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardAgentRuntime : IAgentRuntime
{
    private readonly IAgentRuntime _inner;
    private readonly IHardCapableAgentRuntime _capable;
    private readonly HardExecutionEnvironment _environment;
    private readonly HardIsolationProfile _profile;
    private readonly string _structuredOutputRoot;
    private readonly Guid _correlationId;

    /// <summary>
    /// Wraps a runtime that has already been established as hard capable.
    ///
    /// The constructor takes the seam rather than testing for it, so an incompatible runtime
    /// cannot reach this type at all. Compatibility is decided during admission, before anything
    /// starts - discovering it here would mean discovering it after an execution row exists.
    /// </summary>
    public HardAgentRuntime(
        IHardCapableAgentRuntime capable,
        HardExecutionEnvironment environment,
        HardIsolationProfile profile,
        string structuredOutputRoot,
        Guid correlationId)
    {
        _capable = capable ?? throw new ArgumentNullException(nameof(capable));
        _inner = capable as IAgentRuntime
                 ?? throw new ArgumentException(
                     "a hard capable runtime must also be an agent runtime.", nameof(capable));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _structuredOutputRoot = structuredOutputRoot
                                ?? throw new ArgumentNullException(nameof(structuredOutputRoot));
        _correlationId = correlationId;
    }


    /// <summary>
    /// The inner runtime's own capabilities, unchanged.
    ///
    /// Isolation does not make a runtime able to take a schema or run read only, and pretending
    /// otherwise here would let a reviewer be composed on a runtime that cannot be validated.
    /// </summary>
    public AgentCapabilities Describe() => _inner.Describe();

    public Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken) =>
        _inner.CheckHealthAsync(cancellationToken);

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        var lease = await _environment.LeaseAsync(cancellationToken);

        if (lease is null)
        {
            return new AgentRunResult
            {
                Outcome = AgentRunOutcome.Failed,
                Duration = stopwatch.Elapsed,
                Diagnostic =
                    "the hard isolation boundary for this execution is not available, so the agent was "
                    + "not started: " + (_environment.Refusal?.ToString() ?? "unknown"),
            };
        }

        // The wrapped runtime's own RunAsync is never called on this path, and that is the whole
        // security property of this type. A runtime like a vendor CLI adapter launches its own
        // host process inside that method; calling it would run repository-controlled work on the
        // developer's machine with every boundary switched off, while every log line still said
        // the execution was hard isolated.
        var command = _capable.DescribeCommand(request);

        var result = await lease.Executor.RunAsync(
            _profile,
            new HardProcessRequest
            {
                Executable = command.Executable,
                Arguments = command.Arguments,

                // The workspace the hard workspace gateway prepared, which is a Linux path under
                // the profile's execution root. The request's own working directory is the same
                // value by construction; this takes it from the request because that is what the
                // orchestrator pinned, and a mismatch is refused by the launch command rather than
                // silently preferred one way or the other.
                WorkingDirectory = request.WorkingDirectory,
                Environment = command.Environment,
                StdinPayload = command.StdinPayload,
                SoftTimeout = request.SoftTimeout,
                IdleTimeout = request.IdleTimeout,
                HardTimeout = request.HardTimeout,
                CorrelationId = _correlationId,
            },
            cancellationToken);

        stopwatch.Stop();

        // Fetched across the boundary rather than read from here: the file the runtime named lives
        // inside the environment, on a filesystem this process cannot see.
        string? structured = null;

        if (command.StructuredOutputPath is { Length: > 0 } path
            && result.Outcome is HardProcessOutcome.Exited)
        {
            var readback = await lease.ReadStructuredOutputAsync(
                _structuredOutputRoot, path, cancellationToken);

            if (!readback.Ok)
            {
                // A result that exists and could not be brought back is not the same as a run that
                // produced none, and collapsing the two would be the Hub deciding that an oversized
                // or redirected document means "nothing to report". The run fails, explicitly, and
                // says why - no truncation, no partial document, no silent null.
                return new AgentRunResult
                {
                    Outcome = AgentRunOutcome.Failed,
                    ExitCode = result.ExitCode,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    Duration = stopwatch.Elapsed,
                    Diagnostic =
                        "the structured result could not be read back from the isolated environment: "
                        + readback.Detail,
                };
            }

            structured = readback.Content;
        }

        // Interpreted by the runtime that owns the rules, so an isolated run and a host run mean
        // the same thing rather than merely looking alike.
        return _capable.Interpret(
            request,
            new HardAgentProcessResult
            {
                Termination = result.Outcome switch
                {
                    HardProcessOutcome.Exited => AgentProcessTermination.Exited,
                    HardProcessOutcome.Cancelled => AgentProcessTermination.Cancelled,
                    HardProcessOutcome.SoftTimeout => AgentProcessTermination.TimedOut,
                    HardProcessOutcome.IdleTimeout => AgentProcessTermination.TimedOut,
                    HardProcessOutcome.HardTimeout => AgentProcessTermination.TimedOut,
                    _ => AgentProcessTermination.StartFailed,
                },
                ExitCode = result.ExitCode,
                Stdout = result.Stdout,
                Stderr = result.Stderr,
                Duration = stopwatch.Elapsed,
                StartFailureMessage = result.Outcome is HardProcessOutcome.StartFailed
                    ? result.StartFailureMessage
                    : null,
                StructuredOutput = structured,
            });
    }
}
