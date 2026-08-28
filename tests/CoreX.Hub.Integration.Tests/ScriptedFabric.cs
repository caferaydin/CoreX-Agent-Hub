using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A network plane that records what it was asked to do and does none of it.
///
/// Shared, because two different questions need the same answer. Whether an attachment is really
/// torn down is the real fabric's business and is proven against a machine elsewhere; what these
/// tests ask is whether teardown is called at all, and by whom - and that answer must not depend on
/// which machine the suite runs on.
/// </summary>
internal sealed class ScriptedFabric : IHardNetworkFabric
{
    public int Prepared { get; private set; }

    public int Released { get; private set; }

    public List<string> PreparedFor { get; } = [];

    public List<string> ReleasedFor { get; } = [];

    public bool Refuse { get; set; }

    public bool ThrowOnRelease { get; set; }

    public Task<HardNetworkAttachment?> PrepareAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
    {
        Prepared++;
        PreparedFor.Add(organizationSlug);

        return Task.FromResult(Refuse
            ? null
            : HardNetworkIdentity.Describe(organizationSlug, "/proc/1234/ns/net", "net:[4026532999]"));
    }

    public Task<IReadOnlyList<ReadinessCheck>> VerifyAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReadinessCheck>>([]);

    public Task ReleaseAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
    {
        Released++;
        ReleasedFor.Add(organizationSlug);

        return ThrowOnRelease
            ? throw new InvalidOperationException("the veth would not go away")
            : Task.CompletedTask;
    }
}
