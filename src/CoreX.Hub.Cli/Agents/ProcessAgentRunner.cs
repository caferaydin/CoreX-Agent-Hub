using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Cli.Agents;

/// <summary>
/// Composition layer adapter: an agent runtime asks for a child process, and it is run
/// through ProcessRunner like every other process in the system.
///
/// E.1 #1 keeps its single launch point - job object, three level timeouts, bounded
/// capture, empty based environment and the runtime no-shell policy - while E.1 #4 keeps
/// the agents module free of any reference to the execution module.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessAgentRunner : IAgentProcessRunner
{
    private readonly FileSystemGuard _guard;
    private readonly ProcessRunner _runner;

    public ProcessAgentRunner(string trustRoot)
    {
        _guard = FileSystemGuard.Create(trustRoot);
        _runner = new ProcessRunner(_guard);
    }

    public string TrustRoot => _guard.TrustRoot;

    public async Task<AgentProcessResult> RunAsync(
        AgentProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTimeOffset.UtcNow;

        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessRequest
                {
                    Executable = request.Executable,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                    EnvironmentAllowlist = request.EnvironmentAllowlist,
                    AdditionalEnvironment = request.Environment,
                    SoftTimeout = request.SoftTimeout,
                    IdleTimeout = request.IdleTimeout,
                    HardTimeout = request.HardTimeout,
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is ProcessRequestException or PathTrustException)
        {
            // A request the runner refuses outright never becomes a process.
            return new AgentProcessResult
            {
                Termination = AgentProcessTermination.StartFailed,
                StartedAtUtc = startedAt,
                EndedAtUtc = DateTimeOffset.UtcNow,
                StartFailureMessage = ex.Message,
            };
        }

        return new AgentProcessResult
        {
            Termination = Map(result.TerminationReason),
            ExitCode = result.ExitCode,
            StartedAtUtc = result.StartedAtUtc,
            EndedAtUtc = result.EndedAtUtc,
            Stdout = result.NormalizedStdout,
            Stderr = result.NormalizedStderr,
            StartFailureMessage = result.StartFailureMessage,
        };
    }

    private static AgentProcessTermination Map(ProcessTerminationReason reason) => reason switch
    {
        ProcessTerminationReason.Exited => AgentProcessTermination.Exited,
        ProcessTerminationReason.StartFailed => AgentProcessTermination.StartFailed,
        ProcessTerminationReason.Cancelled => AgentProcessTermination.Cancelled,
        _ => AgentProcessTermination.TimedOut,
    };
}
