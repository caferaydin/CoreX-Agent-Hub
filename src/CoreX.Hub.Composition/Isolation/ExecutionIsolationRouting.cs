using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Whether one execution runs inside a hard boundary, and whether it can.
///
/// Two separate questions that are easy to collapse into one and must not be. A machine can be
/// perfectly capable of hard isolation while the runtime this execution selected has no way to be
/// placed inside it - and the answer in that case is a refusal, not a run somewhere weaker.
///
/// Decided per execution from trusted state: the organization's declared level, its profile, the
/// plane's availability and the selected runtime's own capability. Nothing here reads a task
/// prompt, a repository file or a provider's output.
/// </summary>
public sealed record ExecutionIsolationRouting
{
    private ExecutionIsolationRouting(bool isHard, string? refusal)
    {
        IsHard = isHard;
        Refusal = refusal;
    }

    /// <summary>Whether this execution's work runs inside the hard boundary.</summary>
    public bool IsHard { get; }

    /// <summary>Why a hard organization cannot run, when it cannot. Never a reason to run logically.</summary>
    public string? Refusal { get; }

    public bool IsRefused => Refusal is { Length: > 0 };

    /// <summary>An ordinary logical execution, governed by the rules it already had.</summary>
    public static ExecutionIsolationRouting Logical { get; } = new(false, null);

    /// <summary>A hard execution, with everything it needs established.</summary>
    public static ExecutionIsolationRouting Hard { get; } = new(true, null);

    /// <summary>
    /// A hard organization that cannot run.
    ///
    /// Deliberately not a logical execution. An organization declared hard is an operator saying
    /// the work must be contained, and running it uncontained because containment was unavailable
    /// would answer a question nobody asked.
    /// </summary>
    public static ExecutionIsolationRouting Refuse(string reason) => new(true, reason);

    /// <summary>
    /// Decides, from trusted state only.
    ///
    /// The runtime test is a type test rather than a provider name or a declared flag: a runtime
    /// either implements the hard command seam or it does not, and a name-based rule would be a
    /// rule somebody has to keep current as providers change.
    /// </summary>
    public static ExecutionIsolationRouting For(
        Domain.Catalog.IsolationLevel level,
        HardIsolationProfile? profile,
        bool planeAvailable,
        string planeUnavailableReason,
        IAgentRuntime? selectedRuntime,
        string selectedRuntimeKey)
    {
        if (level != Domain.Catalog.IsolationLevel.Hard)
        {
            return Logical;
        }

        if (profile is null)
        {
            return Refuse("the organization declares hard isolation but supplies no hard configuration.");
        }

        if (!planeAvailable)
        {
            return Refuse(planeUnavailableReason);
        }

        if (selectedRuntime is not null && !HardAgentRuntimeCompatibility.IsHardCapable(selectedRuntime))
        {
            return Refuse(
                $"the runtime '{selectedRuntimeKey}' cannot run inside a hard isolated environment, and a "
                + "hard organization does not fall back to running it on the host.");
        }

        return Hard;
    }
}
