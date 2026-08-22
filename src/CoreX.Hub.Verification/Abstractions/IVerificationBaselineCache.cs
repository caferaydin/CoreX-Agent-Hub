using CoreX.Hub.Verification.Baseline;

namespace CoreX.Hub.Verification.Abstractions;

/// <summary>
/// The Verification owned port for the baseline cache.
///
/// A baseline is comparison context, never permission: nothing read through this port can
/// turn a currently failing required step into a passing one (Phase 4 brief section 23).
/// </summary>
public interface IVerificationBaselineCache
{
    Task<BaselineEntry?> TryGetAsync(BaselineKey key, CancellationToken cancellationToken);

    Task SaveAsync(BaselineEntry entry, CancellationToken cancellationToken);
}
