using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// The mandatory Phase 4 end to end fixture: a broken baseline, a fixed result, and proof
/// that the comparison is evidence rather than permission.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokenToFixedAcceptanceTests
{
    private const string BaseCommit = "abcabcabcabcabcabcabcabcabcabcabcabcabca";

    private static VerificationProfile Profile(string reportKind, int exitCode) =>
        ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "0"]),
            ProfileBuilder.Step(
                "test",
                ["report", "results/results.trx", reportKind, exitCode.ToString(CultureInfo.InvariantCulture)],
                parser: VerificationResultParser.Trx,
                resultPath: "results/results.trx"));

    private static BaselineEntry Entry(BaselineKey key, int passed, int failed) => new()
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
    };

    [Fact]
    public async Task A_broken_baseline_and_a_fixed_result_are_reported_as_an_improvement()
    {
        using var fixture = VerificationFixture.Create();

        // The recipe is identical in both trees; only the tree differs.
        var profileHash = ProfileHasher.Compute(Profile("trx-fixed", 0));
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, profileHash);

        Assert.Equal(ProfileBuilder.RepositoryId, key.RepositoryId);
        Assert.Equal(BaseCommit, key.BaseCommitSha);
        Assert.Equal(profileHash, key.ProfileHash);

        var service = new VerificationBaselineService(fixture.BaselineCache);

        var baselineTree = fixture.CreateTree("baseline-tree");
        var baseline = await service.GetOrCaptureAsync(
            key,
            ct => baselineTree.Engine.RunAsync(Profile("trx-broken", 1), baselineTree.Request, ct)!,
            CancellationToken.None);

        Assert.False(baseline.CacheHit);
        Assert.NotNull(baseline.Entry);
        Assert.Equal(12, baseline.Entry!.Summary.Total);
        Assert.Equal(11, baseline.Entry.Summary.Passed);
        Assert.Equal(1, baseline.Entry.Summary.Failed);

        var resultTree = fixture.CreateTree("result-tree");
        var current = await resultTree.Engine.RunAsync(
            Profile("trx-fixed", 0), resultTree.Request, CancellationToken.None);

        Assert.Equal(VerificationOutcome.Passed, current.RequiredOutcome);
        Assert.Equal(profileHash, current.ProfileHash);
        Assert.Equal(12, current.TestSummary!.Total);
        Assert.Equal(12, current.TestSummary.Passed);
        Assert.Equal(0, current.TestSummary.FailedOrErrored);

        var compared = VerificationBaselineService.Attach(current, baseline.Entry.Summary);
        var comparison = compared.BaselineComparison!;

        Assert.Equal(BaselineVerdict.Improved, comparison.Verdict);
        Assert.True(comparison.FixedAllFailures);
        Assert.False(comparison.IntroducedNewFailures);
        Assert.Equal(-1, comparison.FailedDelta);
        Assert.Equal(1, comparison.PassedDelta);

        // The run passes because its own required step passed, not because the comparison
        // looked good.
        Assert.Equal(VerificationOutcome.Passed, compared.RequiredOutcome);
        Assert.All(
            compared.Steps.Where(s => s.Required),
            s => Assert.Equal(VerificationStepStatus.Passed, s.Status));

        // Both machine summaries are evidence: the baseline in the cache, the current
        // report as a stored blob.
        Assert.True(File.Exists(fixture.BaselineCache.ResolveEntryPath(key)));
        var report = compared.Steps.Single(s => s.StepName == "test").TestReportEvidence;
        Assert.NotNull(report);
        Assert.True(fixture.EvidenceStore.Exists(report!.Sha256));
    }

    [Fact]
    public async Task A_better_baseline_can_never_turn_a_failing_run_into_a_pass()
    {
        using var fixture = VerificationFixture.Create();

        var profile = Profile("trx-broken", 1);
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(profile));

        // A baseline that was much worse: ten failures against this base commit.
        await fixture.BaselineCache.SaveAsync(Entry(key, passed: 2, failed: 10), CancellationToken.None);

        var tree = fixture.CreateTree("result-tree");
        var current = await tree.Engine.RunAsync(profile, tree.Request, CancellationToken.None);

        var stored = await fixture.BaselineCache.TryGetAsync(key, CancellationToken.None);
        var compared = VerificationBaselineService.Attach(current, stored!.Summary);

        // Fewer failures than the baseline, so the comparison is an improvement...
        Assert.Equal(BaselineVerdict.Improved, compared.BaselineComparison!.Verdict);
        Assert.Equal(-9, compared.BaselineComparison.FailedDelta);
        Assert.False(compared.BaselineComparison.FixedAllFailures);

        // ...and the run still failed, because the required step still failed.
        Assert.Equal(VerificationOutcome.Failed, compared.RequiredOutcome);
        Assert.Equal(
            VerificationStepStatus.Failed,
            compared.Steps.Single(s => s.StepName == "test").Status);
        Assert.Equal(1, compared.TestSummary!.FailedOrErrored);
    }

    [Fact]
    public async Task An_equally_broken_result_is_unchanged_and_still_fails()
    {
        using var fixture = VerificationFixture.Create();

        var profile = Profile("trx-broken", 1);
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(profile));
        var service = new VerificationBaselineService(fixture.BaselineCache);

        var baselineTree = fixture.CreateTree("baseline-tree");
        var baseline = await service.GetOrCaptureAsync(
            key,
            ct => baselineTree.Engine.RunAsync(profile, baselineTree.Request, ct)!,
            CancellationToken.None);

        var resultTree = fixture.CreateTree("result-tree");
        var current = await resultTree.Engine.RunAsync(profile, resultTree.Request, CancellationToken.None);

        var compared = VerificationBaselineService.Attach(current, baseline.Entry!.Summary);

        Assert.Equal(BaselineVerdict.Unchanged, compared.BaselineComparison!.Verdict);
        Assert.Equal(0, compared.BaselineComparison.FailedDelta);
        Assert.Equal(VerificationOutcome.Failed, compared.RequiredOutcome);
    }

    [Fact]
    public async Task A_regression_against_the_baseline_is_named_as_one()
    {
        using var fixture = VerificationFixture.Create();

        var profile = Profile("trx-broken", 1);
        var key = new BaselineKey(ProfileBuilder.RepositoryId, BaseCommit, ProfileHasher.Compute(profile));

        await fixture.BaselineCache.SaveAsync(Entry(key, passed: 12, failed: 0), CancellationToken.None);

        var tree = fixture.CreateTree("result-tree");
        var current = await tree.Engine.RunAsync(profile, tree.Request, CancellationToken.None);
        var stored = await fixture.BaselineCache.TryGetAsync(key, CancellationToken.None);

        var compared = VerificationBaselineService.Attach(current, stored!.Summary);

        Assert.Equal(BaselineVerdict.Regressed, compared.BaselineComparison!.Verdict);
        Assert.True(compared.BaselineComparison.IntroducedNewFailures);
        Assert.Equal(VerificationOutcome.Failed, compared.RequiredOutcome);
    }
}
