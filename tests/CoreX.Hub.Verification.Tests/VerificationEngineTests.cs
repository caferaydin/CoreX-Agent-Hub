using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Engine behaviour against the real fixture executable, launched through ProcessRunner.
/// Nothing here mocks the process boundary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationEngineTests
{
    [Fact]
    public async Task Steps_run_in_the_order_the_profile_declares()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("first", ["stdout", "one"]),
            ProfileBuilder.Step("second", ["stdout", "two"]),
            ProfileBuilder.Step("third", ["stdout", "three"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(["first", "second", "third"], run.Steps.Select(s => s.StepName));
        Assert.Equal(VerificationOutcome.Passed, run.RequiredOutcome);

        // Order is observable in wall clock terms too, because the engine is sequential.
        for (var i = 1; i < run.Steps.Count; i++)
        {
            Assert.True(
                run.Steps[i].StartedAtUtc >= run.Steps[i - 1].EndedAtUtc,
                $"step {run.Steps[i].StepName} started before {run.Steps[i - 1].StepName} ended");
        }
    }

    [Fact]
    public async Task A_profile_whose_required_steps_all_succeed_passes()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("restore", ["exit", "0"]),
            ProfileBuilder.Step("build", ["exit", "0"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationOutcome.Passed, run.RequiredOutcome);
        Assert.All(run.Steps, s => Assert.Equal(VerificationStepStatus.Passed, s.Status));
        Assert.False(run.HasOptionalFailures);
    }

    [Fact]
    public async Task A_required_step_exiting_non_zero_fails_the_run()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "3"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
        Assert.Equal(VerificationStepStatus.Failed, run.Steps[0].Status);
        Assert.Equal(3, run.Steps[0].ExitCode);
    }

    [Fact]
    public async Task An_optional_step_failing_does_not_fail_the_run_and_stays_visible()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "0"]),
            ProfileBuilder.Step("lint", ["exit", "7"], required: false),
            ProfileBuilder.Step("package", ["exit", "0"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        // The optional failure is reported as a failure, never rewritten as Passed.
        var lint = run.Steps.Single(s => s.StepName == "lint");
        Assert.Equal(VerificationStepStatus.Failed, lint.Status);
        Assert.Equal(7, lint.ExitCode);
        Assert.True(run.HasOptionalFailures);

        // And it does not stop the run or flip the required result.
        Assert.Equal(VerificationOutcome.Passed, run.RequiredOutcome);
        Assert.Equal(VerificationStepStatus.Passed, run.Steps.Single(s => s.StepName == "package").Status);
    }

    [Fact]
    public async Task Later_steps_are_deterministically_skipped_after_a_required_failure()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "1"]),
            ProfileBuilder.Step("test", ["exit", "0"]),
            ProfileBuilder.Step("smoke", ["exit", "0"], required: false));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.Failed, run.Steps[0].Status);
        Assert.Equal(VerificationStepStatus.Skipped, run.Steps[1].Status);
        Assert.Equal(VerificationStepStatus.Skipped, run.Steps[2].Status);

        // A skipped step never started, so it has no exit code to misread.
        Assert.Null(run.Steps[1].ExitCode);
        Assert.Null(run.Steps[1].ResolvedExecutable);
        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
    }

    [Fact]
    public async Task A_step_that_exceeds_its_timeout_fails_deterministically()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("hang", ["silent", "60"], timeoutSeconds: 2));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.TimedOut, run.Steps[0].Status);
        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
        Assert.True(run.Steps[0].Duration < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Cancellation_produces_a_deterministic_result()
    {
        using var fixture = VerificationFixture.Create();
        using var cancellation = new CancellationTokenSource();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("hang", ["silent", "60"], timeoutSeconds: 60),
            ProfileBuilder.Step("never", ["exit", "0"]));

        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), cancellation.Token);

        Assert.Equal(VerificationStepStatus.Cancelled, run.Steps[0].Status);
        Assert.Equal(VerificationStepStatus.Skipped, run.Steps[1].Status);
        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
    }

    /// <summary>
    /// The exact two step shape the MACHINE acceptance criteria depend on: an optional
    /// failure first, a required success after it. The optional failure must survive as a
    /// failure - with its evidence - while the required result still passes.
    /// </summary>
    [Fact]
    public async Task An_optional_failure_followed_by_a_required_success_passes_without_hiding_the_failure()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "step-a-optional",
                ["both", "lint findings", "lint failed", "3"],
                required: false),
            ProfileBuilder.Step("step-b-required", ["stdout", "packaged"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        var stepA = run.Steps.Single(s => s.StepName == "step-a-optional");
        var stepB = run.Steps.Single(s => s.StepName == "step-b-required");

        // Step A ran, failed, and stayed failed.
        Assert.False(stepA.Required);
        Assert.Equal(VerificationStepStatus.Failed, stepA.Status);
        Assert.NotEqual(VerificationStepStatus.Passed, stepA.Status);
        Assert.NotEqual(VerificationStepStatus.Skipped, stepA.Status);
        Assert.Equal(3, stepA.ExitCode);
        Assert.False(stepA.Succeeded);

        // Step B ran after it and passed.
        Assert.True(stepB.Required);
        Assert.Equal(VerificationStepStatus.Passed, stepB.Status);
        Assert.Equal(0, stepB.ExitCode);
        Assert.NotNull(stepB.StartedAtUtc);
        Assert.True(stepB.StartedAtUtc >= stepA.EndedAtUtc, "step B must run after step A");

        // The required result is Passed, and the optional failure is still visible on it.
        Assert.Equal(VerificationOutcome.Passed, run.RequiredOutcome);
        Assert.True(run.HasOptionalFailures);
        Assert.Contains(run.Steps, s => !s.Required && s.Status == VerificationStepStatus.Failed);

        // Step A's evidence is preserved rather than discarded with its failure.
        Assert.NotNull(stepA.StdoutEvidence);
        Assert.NotNull(stepA.StderrEvidence);
        Assert.True(fixture.EvidenceStore.Exists(stepA.StdoutEvidence!.Sha256));
        Assert.True(fixture.EvidenceStore.Exists(stepA.StderrEvidence!.Sha256));

        var stdout = await fixture.EvidenceStore.ReadTextAsync(
            stepA.StdoutEvidence.Sha256, CancellationToken.None);
        var stderr = await fixture.EvidenceStore.ReadTextAsync(
            stepA.StderrEvidence.Sha256, CancellationToken.None);

        Assert.Contains("lint findings", stdout, StringComparison.Ordinal);
        Assert.Contains("lint failed", stderr, StringComparison.Ordinal);
    }
}
