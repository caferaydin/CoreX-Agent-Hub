using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Runs one workload inside a WSL2 environment, contained by a transient systemd service.
///
/// The trust split is the design. The Windows Hub is the control plane and enters the
/// distribution as <c>root</c>, because creating and stopping a systemd unit is a privileged
/// operation and there is no way to ask for containment without being allowed to impose it. The
/// workload itself never runs as root: <c>--uid</c> puts it under the profile's agent user, and
/// a profile that names no agent user is refused rather than defaulted. Control and workload are
/// two different identities in the same command, on purpose.
///
/// Everything is checked before anything runs. The agent user has to resolve, the working
/// directory has to exist and be a directory inside the execution root, the unit name has to be
/// free, and every ceiling has to be expressible - and a failure in any of those means the
/// workload was never started, rather than started with less containment than was asked for.
/// There is no path here that runs the work somewhere weaker.
///
/// Ending a run always ends the whole control group. That is not the same as ending the process
/// the Hub launched: killing <c>wsl.exe</c> stops the host's view of the work and leaves the
/// Linux service running, so every abnormal ending - cancellation, any of the three deadlines -
/// is followed by an explicit stop of the unit the Hub owns.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslHardProcessExecutor : IHardProcessExecutor
{
    /// <summary>The trusted control identity. Never the workload's.</summary>
    private const string ControlUser = "root";

    private const string IdExecutable = "/usr/bin/id";
    private const string StatExecutable = "/usr/bin/stat";

    /// <summary>Preflight questions are short; a distribution that has to boot gets this long.</summary>
    private static readonly TimeSpan PreflightDeadline = TimeSpan.FromSeconds(90);

    /// <summary>A stop must outlive whatever went wrong, and must not wait for ever.</summary>
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(60);

    private readonly IWslExecutionGateway? _gateway;
    private readonly string? _unavailable;

    /// <summary>
    /// Where this executor's workloads are placed, when it is executing untrusted work.
    ///
    /// Held per executor instance rather than per request, and that is the point: an execution's
    /// lease builds one executor bound to the namespace it prepared, so every workload that
    /// instance launches lands in that namespace and there is no argument for a caller to get
    /// wrong. Null belongs to the Hub's own trusted preparation, which runs no repository code.
    /// </summary>
    private readonly HardWorkloadPlacement? _placement;

    private WslHardProcessExecutor(
        IWslExecutionGateway? gateway, string? unavailable, HardWorkloadPlacement? placement)
    {
        _gateway = gateway;
        _unavailable = unavailable;
        _placement = placement;
    }

    /// <summary>
    /// Builds an executor for this installation.
    ///
    /// <paramref name="dataDirectory"/> is only the working directory the host side
    /// <c>wsl.exe</c> processes run in. Nothing of it reaches the workload, which works beneath
    /// the profile's own execution root inside the environment.
    /// </summary>
    public static WslHardProcessExecutor Create(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        return WslExecutionGateway.TryCreate(dataDirectory, out var gateway, out var problem)
            ? new WslHardProcessExecutor(gateway, null, null)
            : new WslHardProcessExecutor(null, problem, null);
    }

    /// <summary>
    /// An executor whose workloads run inside one execution's own network namespace.
    ///
    /// This is how repository-controlled work is launched. Everything this instance runs carries
    /// both boundaries, so an execution cannot end up with an isolated agent and a verifier in the
    /// machine's shared namespace.
    /// </summary>
    public static WslHardProcessExecutor CreatePlaced(string dataDirectory, HardWorkloadPlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(placement);

        return WslExecutionGateway.TryCreate(dataDirectory, out var gateway, out var problem)
            ? new WslHardProcessExecutor(gateway, null, placement)
            : new WslHardProcessExecutor(null, problem, placement);
    }

    /// <summary>Whether this executor places its workloads in an execution's own namespace.</summary>
    public bool IsPlaced => _placement is not null;

    /// <summary>An executor over a scripted gateway, for this assembly's tests.</summary>
    internal static WslHardProcessExecutor Scripted(IWslExecutionGateway gateway) => new(gateway, null, null);

    /// <summary>A scripted executor that also places its workloads, for this assembly's tests.</summary>
    internal static WslHardProcessExecutor Scripted(
        IWslExecutionGateway gateway, HardWorkloadPlacement placement) => new(gateway, null, placement);

    public async Task<HardProcessResult> RunAsync(
        HardIsolationProfile profile,
        HardProcessRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTimeOffset.UtcNow;

        if (profile.Backend != HardIsolationBackend.Wsl2)
        {
            return Refused(startedAt, string.Empty, $"'{profile.Backend}' is not an environment this build runs in.");
        }

        if (_gateway is null)
        {
            return Refused(startedAt, string.Empty, _unavailable ?? "WSL cannot be reached.");
        }

        string unitName;

        try
        {
            unitName = HardUnitName.For(request.CorrelationId);
        }
        catch (ArgumentException ex)
        {
            return Refused(startedAt, string.Empty, ex.Message);
        }

        if (!SystemdRunCommand.TryBuild(profile, request, unitName, _placement, out var arguments, out var problem))
        {
            return Refused(startedAt, unitName, problem!);
        }

        var preflight = await PreflightAsync(profile, request, unitName, ct);

        if (preflight is not null)
        {
            return Refused(startedAt, unitName, preflight);
        }

        var run = await _gateway.RunAsync(
            profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = ControlUser,
                SoftTimeout = request.SoftTimeout,
                IdleTimeout = request.IdleTimeout,
                HardTimeout = request.HardTimeout,
                StdinPayload = request.StdinPayload,
                KeepStandardInputOpen = request.KeepStandardInputOpen,
                MaxCapturedCharactersPerStream = request.MaxCapturedCharactersPerStream,
            },
            ct);

        // The host supervisor ending is not the workload ending. Whenever this run did not
        // finish on its own, the unit is stopped explicitly - otherwise a cancelled or timed out
        // execution would leave a live process tree inside the environment and report success at
        // having stopped it.
        if (run.Termination != WslCallTermination.Exited)
        {
            await StopAsync(profile, unitName);
        }

        return new HardProcessResult
        {
            Outcome = Map(run.Termination),
            ExitCode = run.Termination == WslCallTermination.Exited ? run.ExitCode : null,
            UnitName = unitName,
            EnvironmentRuntimeCeilingSeconds = SystemdRunCommand.RuntimeSeconds(request.HardTimeout),
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Stdout = run.Stdout,
            Stderr = run.Stderr,
            StdoutTruncated = run.StdoutTruncated,
            StderrTruncated = run.StderrTruncated,
            StartFailureMessage = run.Termination == WslCallTermination.StartFailed
                ? run.StartFailureMessage ?? "the environment did not start the workload."
                : null,
        };
    }

    /// <summary>
    /// Everything that has to be true before a workload may be created.
    ///
    /// Each of these is a question systemd would otherwise answer with an exit status in its own
    /// reserved range - 217 for an unresolvable user, 200 for a working directory it could not
    /// enter - which is indistinguishable from a workload that happened to exit with the same
    /// number. Asking first turns an ambiguous exit code into an unambiguous refusal, and it is
    /// the reason this executor can treat what systemd-run returns as the workload's own status.
    /// </summary>
    private async Task<string?> PreflightAsync(
        HardIsolationProfile profile,
        HardProcessRequest request,
        string unitName,
        CancellationToken ct)
    {
        var agent = await ControlAsync(profile, [IdExecutable, "-u", profile.AgentUser.Trim()], ct);

        if (!agent.Ok)
        {
            return $"the environment has no account '{profile.AgentUser}'; a workload never runs as root instead.";
        }

        if (agent.Lines.FirstOrDefault() is not { Length: > 0 } uid || uid == "0")
        {
            return $"the account '{profile.AgentUser}' resolves to uid 0; a hard isolated workload may not be root.";
        }

        var directory = await ControlAsync(
            profile, [StatExecutable, "-c", "%A", request.WorkingDirectory.Trim()], ct);

        if (!directory.Ok)
        {
            return $"the working directory '{request.WorkingDirectory}' does not exist inside the environment.";
        }

        if (directory.Lines.FirstOrDefault() is not { Length: > 0 } access || !access.StartsWith('d'))
        {
            return $"the working directory '{request.WorkingDirectory}' is not a directory inside the environment.";
        }

        var loaded = await ControlAsync(profile, SystemdRunCommand.BuildLoadStateQuery(unitName), ct);

        if (!loaded.Ok)
        {
            return "the environment could not be asked whether this run's unit name is free.";
        }

        // Anything other than not-found means a unit of this name already exists. It cannot be
        // one of ours - the name carries a fresh correlation id - so it is somebody else's, and
        // attaching to it or reusing the name is how one run comes to control another.
        if (loaded.Lines.FirstOrDefault() is not "LoadState=not-found")
        {
            return $"the unit '{unitName}' already exists in the environment; this run will not reuse it.";
        }

        return null;
    }

    /// <summary>
    /// Ends the run's entire control group.
    ///
    /// Deliberately not given the caller's cancellation token: this is the cleanup that runs
    /// because the caller cancelled, and a token that is already cancelled would abandon the very
    /// process tree it was meant to end.
    /// </summary>
    private async Task StopAsync(HardIsolationProfile profile, string unitName)
    {
        if (_gateway is null || !SystemdRunCommand.TryBuildStop(unitName, out var arguments, out _))
        {
            return;
        }

        await _gateway.RunAsync(
            profile.EnvironmentId,
            new WslCall { Arguments = arguments, LinuxUser = ControlUser, HardTimeout = StopDeadline },
            CancellationToken.None);
    }

    private Task<WslCallResult> ControlAsync(
        HardIsolationProfile profile,
        IReadOnlyList<string> arguments,
        CancellationToken ct) =>
        _gateway!.RunAsync(
            profile.EnvironmentId,
            new WslCall { Arguments = arguments, LinuxUser = ControlUser, HardTimeout = PreflightDeadline },
            ct);

    private static HardProcessResult Refused(DateTimeOffset startedAt, string unitName, string message) =>
        new()
        {
            Outcome = HardProcessOutcome.StartFailed,
            UnitName = unitName,
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            StartFailureMessage = message,
        };

    private static HardProcessOutcome Map(WslCallTermination termination) => termination switch
    {
        WslCallTermination.Exited => HardProcessOutcome.Exited,
        WslCallTermination.StartFailed => HardProcessOutcome.StartFailed,
        WslCallTermination.Cancelled => HardProcessOutcome.Cancelled,
        WslCallTermination.SoftTimeout => HardProcessOutcome.SoftTimeout,
        WslCallTermination.IdleTimeout => HardProcessOutcome.IdleTimeout,
        _ => HardProcessOutcome.HardTimeout,
    };
}
