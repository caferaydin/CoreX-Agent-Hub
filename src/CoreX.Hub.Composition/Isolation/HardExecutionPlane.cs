using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Whether this build and this machine can run a whole task inside a hard boundary.
///
/// The question it answers is narrower than it looks, and the narrowness is the point. This says a
/// hard execution plane exists here: the environment can be reached, and every stage of a workflow
/// has an implementation that runs inside the boundary rather than beside it - workspace, agent,
/// verification, review and evidence. It does not say that any particular execution may proceed.
///
/// Two further questions are asked elsewhere and neither is folded in here. Whether this
/// organization's environment is really established is measured per organization by readiness, and
/// whether the runtime this execution selected can be placed inside the boundary at all is a
/// property of that runtime. A plane that answered all three would let an available machine imply
/// an admissible execution, which is how a provider that cannot be isolated ends up running
/// somewhere it should not.
///
/// Before Phase 10C-4 the honest answer here was always no: a contained process could be launched,
/// but a whole task could not, and admitting one because part of it could be contained is how work
/// ends up half outside the boundary. That is no longer the case, so this reports what is true.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardExecutionPlane : IHardExecutionPlane
{
    private HardExecutionPlane(bool available, string unavailableReason)
    {
        IsAvailable = available;
        UnavailableReason = unavailableReason;
    }

    public bool IsAvailable { get; }

    public string UnavailableReason { get; }

    /// <summary>
    /// The plane for this installation, or an explanation of why there is not one.
    ///
    /// Availability is decided by whether the environment can be reached at all. Everything else a
    /// hard execution needs is code that either shipped or did not, and it did - so a machine with
    /// WSL has a plane, and a machine without one gets a refusal rather than a logical run.
    /// </summary>
    public static IHardExecutionPlane Create(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        return WslExecutionGateway.TryCreate(dataDirectory, out _, out var problem)
            ? new HardExecutionPlane(true, string.Empty)
            : new HardExecutionPlane(
                false,
                "this machine has no environment a hard organization could run in: "
                + (problem ?? "the environment could not be reached")
                + "; a hard organization is refused rather than run on the host.");
    }
}
