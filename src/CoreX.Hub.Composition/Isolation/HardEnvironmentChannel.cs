using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// The two things a lease needs from the machine, behind one seam.
///
/// Opening a trusted channel into the environment and placing an executor in a namespace are the
/// only parts of acquiring a lease that touch an operating system. Everything else - the order the
/// boundaries are established in, what is unwound when a stage refuses, which identity owns the
/// network claim - is policy, and policy is what has to be provable.
///
/// So the machine is a seam and the policy is not. A test can drive the real acquisition path with
/// a scripted environment and prove that two executions of one organization share an attachment,
/// without a distribution existing and without touching a developer's WSL - which is a rule this
/// repository has already been bitten by breaking.
/// </summary>
internal interface IHardEnvironmentChannel
{
    /// <summary>Opens the trusted control channel, or says why the environment cannot be reached.</summary>
    bool TryOpen(out IWslExecutionGateway? gateway, out string? problem);

    /// <summary>An executor already bound to one placement, and to no other.</summary>
    IHardProcessExecutor PlacedExecutor(HardWorkloadPlacement placement);
}

/// <summary>The production channel: real WSL, one host working directory, nothing else.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WslHardEnvironmentChannel : IHardEnvironmentChannel
{
    private readonly string _hostWorkingDirectory;

    public WslHardEnvironmentChannel(string hostWorkingDirectory) =>
        _hostWorkingDirectory = hostWorkingDirectory
                                ?? throw new ArgumentNullException(nameof(hostWorkingDirectory));

    public bool TryOpen(out IWslExecutionGateway? gateway, out string? problem)
    {
        var opened = WslExecutionGateway.TryCreate(_hostWorkingDirectory, out var created, out problem);
        gateway = created;

        return opened;
    }

    public IHardProcessExecutor PlacedExecutor(HardWorkloadPlacement placement) =>
        WslHardProcessExecutor.CreatePlaced(_hostWorkingDirectory, placement);
}
