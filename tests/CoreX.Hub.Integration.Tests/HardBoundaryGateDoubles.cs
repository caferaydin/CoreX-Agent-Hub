using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A boundary that was measured and found sound, for tests about something else.
///
/// Explicit rather than absent. The production environment refuses to hand out a lease that has not
/// been through a gate, so a test double that skipped one would be exercising a shape the product
/// does not have.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SoundBoundaryGate : IHardBoundaryGate
{
    /// <summary>What the gate concluded. Null is sound; anything else refuses the execution.</summary>
    public string? Unsound { get; set; }

    /// <summary>How many times a boundary was actually measured.</summary>
    public int Measured { get; private set; }

    public Task<string?> MeasureAsync(
        HardExecutionLease lease,
        HardIsolationProfile profile,
        string organizationSlug,
        CancellationToken ct)
    {
        Measured++;

        return Task.FromResult(Unsound);
    }
}
