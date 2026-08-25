using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Composition.Agents;

/// <summary>
/// Composition layer adapter: an agent runtime asks for a child process, and it is run
/// through ProcessRunner like every other process in the system.
///
/// E.1 #1 keeps its single launch point - job object, three level timeouts, bounded
/// capture, empty based environment and the runtime no-shell policy - while E.1 #4 keeps
/// the agents module free of any reference to the execution module.
///
/// It is also the one place every agent child passes through, which is why the git
/// configuration baseline is applied here rather than inside a runtime. A runtime that had
/// to remember to apply it would eventually be written by someone who did not, and the
/// omission would be invisible: the child would simply start reading the developer's git
/// configuration again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessAgentRunner : IAgentProcessRunner
{
    private readonly FileSystemGuard _guard;
    private readonly ProcessRunner _runner;
    private readonly string _gitConfigGlobalPath;

    public ProcessAgentRunner(string trustRoot, string gitConfigGlobalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitConfigGlobalPath);

        _guard = FileSystemGuard.Create(trustRoot);
        _runner = new ProcessRunner(_guard);
        _gitConfigGlobalPath = gitConfigGlobalPath;
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
                    AdditionalEnvironment = ComposeEnvironment(request),
                    SoftTimeout = request.SoftTimeout,
                    IdleTimeout = request.IdleTimeout,
                    HardTimeout = request.HardTimeout,

                    // A prompt on the command line is readable by every process on the
                    // machine, so a runtime that has one hands it over here instead.
                    StdinPayload = request.StandardInputText,
                    StandardOutputObserver = request.StandardOutputObserver,
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

    /// <summary>
    /// The runtime's own variables first, the git baseline on top.
    ///
    /// The order is the point: a runtime may add whatever its tool needs, and may not
    /// weaken the configuration the child's git starts from, because the last writer wins
    /// and it is never the runtime.
    /// </summary>
    private Dictionary<string, string> ComposeEnvironment(AgentProcessRequest request)
    {
        var environment = new Dictionary<string, string>(request.Environment, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in GitEnvironment.ForAgentChild(_gitConfigGlobalPath))
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    private static AgentProcessTermination Map(ProcessTerminationReason reason) => reason switch
    {
        ProcessTerminationReason.Exited => AgentProcessTermination.Exited,
        ProcessTerminationReason.StartFailed => AgentProcessTermination.StartFailed,
        ProcessTerminationReason.Cancelled => AgentProcessTermination.Cancelled,
        _ => AgentProcessTermination.TimedOut,
    };
}
