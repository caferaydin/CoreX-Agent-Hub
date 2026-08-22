using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Baseline cache identity, persistence and comparison semantics.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BaselineTests
{
    private const string BaseCommit = "1234567890abcdef1234567890abcdef12345678";
    private const string OtherCommit = "fedcba0987654321fedcba0987654321fedcba09";

    private static VerificationProfile TestProfile(string reportKind, int exitCode) =>
        ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["report", "results.trx", reportKind, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                parser: VerificationResultParser.Trx,
                resultPath: "results.trx"));

    [Fact]
    public async Task A_saved_baseline_is_returned_for_the_same_key()
    {
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(TestProfile("trx-broken", 1)));

        var (engine, request) = fixture.CreateTree("baseline");
        var captured = 0;

        var first = await service.GetOrCaptureAsync(
            key,
            async ct =>
            {
                captured++;
                return await engine.RunAsync(TestProfile("trx-broken", 1), request, ct);
            },
            CancellationToken.None);

        Assert.False(first.CacheHit);
        Assert.Equal(1, captured);
        Assert.NotNull(first.Entry);
        Assert.Equal(11, first.Entry!.Summary.Passed);
        Assert.Equal(1, first.Entry.Summary.Failed);

        var second = await service.GetOrCaptureAsync(
            key,
            ct => throw new InvalidOperationException("a cache hit must not produce a baseline"),
            CancellationToken.None);

        Assert.True(second.CacheHit);
        Assert.Equal(1, captured);
        Assert.Equal(first.Entry.Summary.Passed, second.Entry!.Summary.Passed);
        Assert.Equal(first.Entry.Summary.Failed, second.Entry.Summary.Failed);
    }

    [Fact]
    public async Task A_different_profile_hash_misses_the_cache()
    {
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var stored = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(TestProfile("trx-broken", 1)));
        await Save(fixture, stored, passed: 11, failed: 1);

        var changed = new BaselineKey(
            ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(TestProfile("trx-fixed", 0)));

        Assert.NotEqual(stored.ProfileHash, changed.ProfileHash);
        Assert.NotNull(await service.TryGetAsync(stored, CancellationToken.None));
        Assert.Null(await service.TryGetAsync(changed, CancellationToken.None));
    }

    [Fact]
    public async Task A_different_base_commit_misses_the_cache()
    {
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var hash = ProfileHasher.Compute(TestProfile("trx-broken", 1));
        var stored = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, hash);
        await Save(fixture, stored, passed: 11, failed: 1);

        var moved = new BaselineKey(ProfileBuilder.RepositoryId, OtherCommit, hash);

        Assert.NotNull(await service.TryGetAsync(stored, CancellationToken.None));
        Assert.Null(await service.TryGetAsync(moved, CancellationToken.None));
    }

    [Fact]
    public async Task A_different_repository_misses_the_cache()
    {
        using var fixture = VerificationFixture.Create();
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var hash = ProfileHasher.Compute(TestProfile("trx-broken", 1));
        var stored = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, hash);
        await Save(fixture, stored, passed: 11, failed: 1);

        var other = new BaselineKey(Guid.Parse("99999999-9999-9999-9999-999999999999"), BaseCommit, hash);

        Assert.Null(await service.TryGetAsync(other, CancellationToken.None));
    }

    [Fact]
    public void The_key_is_exactly_repository_base_commit_and_profile_hash()
    {
        var hash = ProfileHasher.Compute(TestProfile("trx-broken", 1));
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, hash);

        Assert.Contains(ProfileBuilder.RepositoryId.ToString("D"), key.CanonicalText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(BaseCommit, key.CanonicalText, StringComparison.Ordinal);
        Assert.Contains(hash, key.CanonicalText, StringComparison.Ordinal);

        // Nothing else participates: three lines, three facts.
        Assert.Equal(3, key.CanonicalText.Split('\n').Length);
    }

    [Fact]
    public async Task A_cache_entry_is_written_atomically_and_leaves_no_partial_file()
    {
        using var fixture = VerificationFixture.Create();

        var hash = ProfileHasher.Compute(TestProfile("trx-broken", 1));
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, hash);

        // Concurrent savers of the same key: the last write wins and every reader sees a
        // complete entry, never a half written one.
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Save(fixture, key, passed: 10 + i, failed: 1)));

        var entry = await fixture.BaselineCache.TryGetAsync(key, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(1, entry!.Summary.Failed);

        // One entry, and no temporary debris beside it.
        var files = Directory.GetFiles(fixture.BaselineCache.Directory);
        Assert.Single(files);
        Assert.EndsWith(".json", files[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_cache_entry_is_a_miss_rather_than_a_wrong_baseline()
    {
        using var fixture = VerificationFixture.Create();

        var hash = ProfileHasher.Compute(TestProfile("trx-broken", 1));
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, hash);

        await Save(fixture, key, passed: 11, failed: 1);
        var path = fixture.BaselineCache.ResolveEntryPath(key);

        await File.WriteAllTextAsync(path, "{ \"Total\": 12, \"Passed\":");

        Assert.Null(await fixture.BaselineCache.TryGetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task An_entry_whose_stored_key_does_not_match_is_ignored()
    {
        using var fixture = VerificationFixture.Create();

        var hash = ProfileHasher.Compute(TestProfile("trx-broken", 1));
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, hash);

        await Save(fixture, key, passed: 11, failed: 1);

        // The file name derives from the key, but the key is stored inside as well, so a
        // hand edited or stale file cannot be served for another identity.
        var path = fixture.BaselineCache.ResolveEntryPath(key);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(BaseCommit, OtherCommit, StringComparison.Ordinal));

        Assert.Null(await fixture.BaselineCache.TryGetAsync(key, CancellationToken.None));
    }

    private static Task Save(VerificationFixture fixture, BaselineKey key, int passed, int failed) =>
        fixture.BaselineCache.SaveAsync(
            new BaselineEntry
            {
                Key = key,
                StepName = "test",
                CapturedAtUtc = DateTimeOffset.UnixEpoch,
                Summary = new TestSummary
                {
                    Format = TestReportFormat.Trx,
                    Total = passed + failed,
                    Passed = passed,
                    Failed = failed,
                    Errors = 0,
                    Skipped = 0,
                },
            },
            CancellationToken.None);
}
