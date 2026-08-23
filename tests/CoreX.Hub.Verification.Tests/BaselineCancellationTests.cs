using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// What the baseline cache is allowed to remember when a measurement was interrupted.
///
/// A baseline is a claim about one commit: this is what that tree scores. A run that was
/// stopped part way through scored nothing, and caching it would make every later execution
/// on that commit compare against a number nobody measured - quietly, and for as long as
/// the entry survives. Since a baseline is never permission, the damage would be silent
/// context rather than a wrong verdict, which is precisely why it has to be refused here
/// rather than noticed later.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BaselineCancellationTests
{
    private const string BaseCommit = "1234567890abcdef1234567890abcdef12345678";

    private static VerificationProfile TestProfile() =>
        ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["report", "results.trx", "trx-broken", "1"],
                parser: VerificationResultParser.Trx,
                resultPath: "results.trx"));

    private static BaselineKey Key() =>
        new(ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(TestProfile()));

    [Fact]
    public async Task A_producer_that_was_cancelled_leaves_no_entry_behind()
    {
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        using var cancellation = new CancellationTokenSource();

        var lookup = await service.GetOrCaptureAsync(
            Key(),
            ct =>
            {
                // The shape of an interrupted measurement: the caller is cancelled and the
                // producer answers "no baseline" rather than handing back what it had.
                cancellation.Cancel();
                return Task.FromResult<VerificationRunResult?>(null);
            },
            cancellation.Token);

        Assert.False(lookup.CacheHit);
        Assert.Null(lookup.Entry);
        Assert.Empty(Directory.GetFiles(fixture.BaselineCache.Directory, "*.json"));
    }

    [Fact]
    public async Task A_refused_baseline_is_retried_rather_than_remembered_as_a_miss()
    {
        // Nothing was written, so the commit still has no baseline - which is the difference
        // between "we could not measure it" and "it scores nothing".
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var key = Key();

        var first = await service.GetOrCaptureAsync(
            key, _ => Task.FromResult<VerificationRunResult?>(null), CancellationToken.None);

        Assert.Null(first.Entry);

        var (engine, request) = fixture.CreateTree("baseline");

        var second = await service.GetOrCaptureAsync(
            key,
            ct => engine.RunAsync(TestProfile(), request, ct)!,
            CancellationToken.None);

        Assert.False(second.CacheHit);
        Assert.NotNull(second.Entry);
        Assert.Equal(1, second.Entry!.Summary.Failed);

        Assert.Single(Directory.GetFiles(fixture.BaselineCache.Directory, "*.json"));
    }

    [Fact]
    public async Task A_cached_baseline_is_still_only_read_for_its_own_commit()
    {
        // The interrupted case must not become a way to attach one commit's numbers to
        // another: the key is the whole identity, and cancellation does not widen it.
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var (engine, request) = fixture.CreateTree("baseline");

        await service.GetOrCaptureAsync(
            Key(), ct => engine.RunAsync(TestProfile(), request, ct)!, CancellationToken.None);

        var otherCommit = new BaselineKey(
            ProfileBuilder.RepositoryId,
            "fedcba0987654321fedcba0987654321fedcba09",
            ProfileHasher.Compute(TestProfile()));

        Assert.Null(await service.TryGetAsync(otherCommit, CancellationToken.None));
    }
}
