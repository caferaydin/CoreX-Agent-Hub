using System.Runtime.Versioning;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>How one call into the environment ended, in the host supervisor's vocabulary.</summary>
internal enum WslCallTermination
{
    Exited,
    StartFailed,
    Cancelled,
    SoftTimeout,
    IdleTimeout,
    HardTimeout,
}

/// <summary>What one call into the environment produced.</summary>
internal sealed record WslCallResult(
    WslCallTermination Termination,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    string? StartFailureMessage)
{
    public static WslCallResult NotStarted(string message) =>
        new(WslCallTermination.StartFailed, null, string.Empty, string.Empty, false, false, message);

    /// <summary>The call ran inside the environment and reported success.</summary>
    public bool Ok => Termination == WslCallTermination.Exited && ExitCode == 0;

    public IReadOnlyList<string> Lines =>
        [.. Stdout.Split('\n').Select(l => l.Trim('\r', ' ', '\t')).Where(l => l.Length > 0)];
}

/// <summary>One command to run inside the environment, with its deadlines.</summary>
internal sealed record WslCall
{
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Which Linux identity <c>wsl.exe</c> enters as. Not the workload's identity.</summary>
    public required string LinuxUser { get; init; }

    public TimeSpan? SoftTimeout { get; init; }

    public TimeSpan? IdleTimeout { get; init; }

    public required TimeSpan HardTimeout { get; init; }

    public string? StdinPayload { get; init; }

    public bool KeepStandardInputOpen { get; init; }

    public int MaxCapturedCharactersPerStream { get; init; } = 256 * 1024;
}

/// <summary>
/// Runs a command inside a WSL environment, behind a seam.
///
/// The seam is here for the same reason the readiness probe has one: what the executor concludes
/// from an answer is worth proving without a machine, and the answers that matter - a rejected
/// property, an unresolvable user, a unit name already taken - cannot be arranged on the
/// operator's own distribution without damaging it.
/// </summary>
internal interface IWslExecutionGateway
{
    Task<WslCallResult> RunAsync(string environmentId, WslCall call, CancellationToken ct);
}

/// <summary>
/// The real gateway: <c>wsl.exe</c> as an ordinary Windows child, through the one process
/// boundary this system has (ADR-0006, E.1 #1).
///
/// Separate from the readiness probe's runner on purpose, and not a widening of it. Readiness
/// asks short read-only questions with a fixed deadline and a small capture ceiling; execution
/// streams a workload's output, carries its standard input, and honours the caller's three
/// deadlines. Teaching one component to do both would have meant giving the read-only probe a
/// way to run arbitrary commands, which is precisely what its closed tool surface exists to
/// prevent. Two authorities, one process boundary.
///
/// The environment variable list is the same measured minimum the probe uses: <c>SystemRoot</c>
/// alone, because <c>wsl.exe</c> reaches the WSL service over RPC and that call fails without it.
/// Nothing of the Windows caller's environment crosses into the distribution - WSL forwards only
/// what <c>WSLENV</c> names, and nothing here sets one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WslExecutionGateway : IWslExecutionGateway
{
    private const string Utf8Switch = "WSL_UTF8";
    private const string SystemRootVariable = "SystemRoot";

    private readonly ProcessRunner _runner;
    private readonly string _executable;
    private readonly string _workingDirectory;

    private WslExecutionGateway(ProcessRunner runner, string executable, string workingDirectory)
    {
        _runner = runner;
        _executable = executable;
        _workingDirectory = workingDirectory;
    }

    /// <summary>Prepares a gateway, or says why WSL cannot be reached at all.</summary>
    public static bool TryCreate(string workingDirectory, out WslExecutionGateway? gateway, out string? problem)
    {
        gateway = null;
        problem = null;

        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            problem = $"WSL is not installed on this machine ('{executable}' does not exist).";
            return false;
        }

        var rooted = FileSystemGuard.TryCreate(workingDirectory, out var guard);

        if (!rooted.IsAllowed || guard is null)
        {
            problem = $"the hard execution gateway has no usable working directory: {rooted.Detail}";
            return false;
        }

        gateway = new WslExecutionGateway(new ProcessRunner(guard), executable, guard.TrustRoot);
        return true;
    }

    public async Task<WslCallResult> RunAsync(string environmentId, WslCall call, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            var result = await _runner.RunAsync(
                new ProcessRequest
                {
                    Executable = _executable,

                    // --exec, always. Without it wsl.exe hands the remainder to the
                    // distribution's login shell, which would put a shell back into a system
                    // that has spent considerable effort not having one - and would let a
                    // dotfile the agent can edit decide what actually ran.
                    Arguments = ["-d", environmentId, "-u", call.LinuxUser, "--exec", .. call.Arguments],
                    WorkingDirectory = _workingDirectory,
                    EnvironmentAllowlist = [SystemRootVariable],
                    AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Utf8Switch] = "1",
                    },
                    SoftTimeout = call.SoftTimeout,
                    IdleTimeout = call.IdleTimeout,
                    HardTimeout = call.HardTimeout,
                    StdinPayload = call.StdinPayload,
                    KeepStandardInputOpen = call.KeepStandardInputOpen,
                    MaxCapturedCharactersPerStream = call.MaxCapturedCharactersPerStream,
                },
                ct);

            return new WslCallResult(
                Map(result.TerminationReason),
                result.ExitCode,
                result.NormalizedStdout,
                result.NormalizedStderr,
                result.StdoutTruncated,
                result.StderrTruncated,
                result.StartFailureMessage);
        }
        catch (ProcessRequestException ex)
        {
            return WslCallResult.NotStarted(ex.Message);
        }
        catch (PathTrustException ex)
        {
            return WslCallResult.NotStarted(ex.Message);
        }
    }

    private static WslCallTermination Map(ProcessTerminationReason reason) => reason switch
    {
        ProcessTerminationReason.Exited => WslCallTermination.Exited,
        ProcessTerminationReason.StartFailed => WslCallTermination.StartFailed,
        ProcessTerminationReason.Cancelled => WslCallTermination.Cancelled,
        ProcessTerminationReason.SoftTimeout => WslCallTermination.SoftTimeout,
        ProcessTerminationReason.IdleTimeout => WslCallTermination.IdleTimeout,
        _ => WslCallTermination.HardTimeout,
    };
}
