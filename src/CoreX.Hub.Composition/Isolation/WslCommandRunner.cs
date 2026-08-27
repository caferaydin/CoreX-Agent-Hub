using System.Runtime.Versioning;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>What one <c>wsl.exe</c> invocation produced.</summary>
/// <param name="Started">False when no process was created at all.</param>
/// <param name="ExitCode">Null unless the process ran to completion.</param>
internal sealed record WslResult(bool Started, int? ExitCode, string Stdout, string Stderr)
{
    public static WslResult NotStarted { get; } = new(false, null, string.Empty, string.Empty);

    /// <summary>The command ran and reported success.</summary>
    public bool Ok => Started && ExitCode == 0;

    /// <summary>
    /// Lines of standard output, blank ones dropped.
    ///
    /// Several probes ask about a list of paths at once and read whatever came back, because
    /// a tool that reports six of seven paths and exits non zero has still told the truth
    /// about the six.
    /// </summary>
    public IReadOnlyList<string> Lines =>
        [.. Stdout.Split('\n').Select(l => l.Trim('\r', ' ', '\t')).Where(l => l.Length > 0)];
}

/// <summary>
/// Runs <c>wsl.exe</c> as an ordinary executable, through the one process boundary the system
/// has (ADR-0006, E.1 #1).
///
/// There is no WSL specific launcher here and no second way to create a process. It is an
/// executable plus an argument vector handed to <see cref="ProcessRunner"/>, which is what
/// gives these probes the job object, the deadlines, the bounded capture and the empty
/// environment that every other child in the system gets.
///
/// Two properties are worth stating explicitly, because they are what make this safe to point
/// at an environment that may be hostile:
///
///   * <c>--exec</c> on every invocation that enters an environment. Without it
///     <c>wsl.exe</c> hands the remainder to the distribution's login shell, which would put a
///     shell back into a system that has spent considerable effort not having one, and would
///     make the result depend on a dotfile the agent can edit. The one invocation that does not
///     use it lists environments from the host side and never enters one.
///   * nothing real of the host crosses the boundary. The child's environment is assembled
///     from an explicit allowlist that names a single variable, and the only other value it is
///     ever given is a deliberately synthetic PATH used to observe whether the environment
///     imports one.
/// </summary>
/// <summary>
/// One wsl.exe invocation, behind a seam.
///
/// The seam exists so the readiness decisions can be proven without a machine. Every check in
/// the probe above it is a judgement about what a command answered, and the situations worth
/// being sure about - an agent in the sudo group, an evidence directory the agent can read, a
/// kernel with no pids controller - cannot be arranged on the operator's own machine without
/// damaging it. Scripting the answers is the only honest way to test those, and the real
/// implementation is still what every shipped path uses.
/// </summary>
internal interface IWslCommandRunner
{
    Task<WslResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct, string? hostPath = null);
}

[SupportedOSPlatform("windows")]
internal sealed class WslCommandRunner : IWslCommandRunner
{
    /// <summary>Makes wsl.exe emit UTF-8 rather than UTF-16 when its output is redirected.</summary>
    private const string Utf8Switch = "WSL_UTF8";

    /// <summary>Required by the RPC runtime wsl.exe reaches the WSL service through.</summary>
    private const string SystemRootVariable = "SystemRoot";

    /// <summary>Generous, because the first call to a stopped distribution boots it.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(45);

    private readonly ProcessRunner _runner;
    private readonly string _executable;
    private readonly string _workingDirectory;

    private WslCommandRunner(ProcessRunner runner, string executable, string workingDirectory)
    {
        _runner = runner;
        _executable = executable;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Prepares a runner, or says why WSL cannot be reached at all.
    ///
    /// The executable is taken from the system directory rather than found on PATH: a probe
    /// that asks the machine about isolation must not itself be steerable by an environment
    /// variable.
    /// </summary>
    public static bool TryCreate(string workingDirectory, out WslCommandRunner? runner, out string? problem)
    {
        runner = null;
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
            problem = $"the readiness probe has no usable working directory: {rooted.Detail}";
            return false;
        }

        runner = new WslCommandRunner(new ProcessRunner(guard), executable, guard.TrustRoot);
        return true;
    }

    /// <summary>
    /// Runs one wsl.exe invocation.
    ///
    /// <paramref name="hostPath"/> is the PATH the child is given, and it exists for exactly
    /// one caller: WSL appends the calling process's Windows PATH to the environment's, so the
    /// only way to observe whether it does that is to supply a PATH worth recognising. It is
    /// never the real one - see the marker the readiness probe passes - and every other call
    /// leaves it null and runs with no PATH at all.
    /// </summary>
    public async Task<WslResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? hostPath = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Utf8Switch] = "1",
        };

        if (hostPath is not null)
        {
            environment["PATH"] = hostPath;
        }

        try
        {
            var result = await _runner.RunAsync(
                new ProcessRequest
                {
                    Executable = _executable,
                    Arguments = arguments,
                    WorkingDirectory = _workingDirectory,

                    // One name, and it was measured rather than assumed. Everything else is
                    // withheld, because a probe that borrowed the operator's environment is a
                    // probe whose answer can be steered by the thing it is asking about; but
                    // wsl.exe reaches the WSL service over RPC, and with SystemRoot missing
                    // that call fails with 0x8007072c before any distribution is contacted.
                    // Listing a distribution still works without it, which is exactly the kind
                    // of half-working that would have turned every guest question into a false
                    // Unknown.
                    EnvironmentAllowlist = [SystemRootVariable],
                    AdditionalEnvironment = environment,
                    HardTimeout = Deadline,
                    IdleTimeout = Idle,

                    // Diagnostics, not logs. A mount table is a few kilobytes; anything that
                    // wanted megabytes here would be something this probe should not be reading.
                    MaxCapturedCharactersPerStream = 256 * 1024,
                },
                ct);

            return result.Started
                ? new WslResult(true, result.ExitCode, result.NormalizedStdout, result.NormalizedStderr)
                : WslResult.NotStarted;
        }
        catch (ProcessRequestException)
        {
            // A request the runner refuses never becomes a process, and an unanswerable
            // question is Unknown - never a pass.
            return WslResult.NotStarted;
        }
        catch (PathTrustException)
        {
            return WslResult.NotStarted;
        }
    }
}
