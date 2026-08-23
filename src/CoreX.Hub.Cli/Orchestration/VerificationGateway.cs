using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Cli.Verification;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// Composition layer adapter joining the orchestrator to the Phase 4 verification engine.
///
/// It measures two different things and is careful never to confuse them. The current
/// result is measured in the execution worktree, which now holds the Hub's result commit.
/// The baseline is measured in a disposable tree pinned to the execution's own base
/// commit - never the default branch, which may have moved, and never the worktree, whose
/// contents are the thing being judged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationGateway : IExecutionVerifier
{
    private readonly WorkspaceManager _workspace;
    private readonly VerificationBaselineService _baselines;
    private readonly Func<string, VerificationEngine> _engineFor;
    private readonly Func<Guid, VerificationSetup?> _setupFor;
    private readonly TimeSpan _disposalBudget;

    public VerificationGateway(
        WorkspaceManager workspace,
        VerificationBaselineService baselines,
        Func<string, VerificationEngine> engineFor,
        Func<Guid, VerificationSetup?> setupFor,
        TimeSpan? snapshotDisposalBudget = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _engineFor = engineFor ?? throw new ArgumentNullException(nameof(engineFor));
        _setupFor = setupFor ?? throw new ArgumentNullException(nameof(setupFor));
        _disposalBudget = snapshotDisposalBudget ?? TimeSpan.FromMinutes(2);
    }

    public async Task<VerificationOutcomeView> VerifyAsync(
        Guid repositoryId,
        PreparedExecutionWorkspace workspace,
        string baseCommitSha,
        CancellationToken ct)
    {
        var setup = _setupFor(repositoryId);

        if (setup is null)
        {
            return new VerificationOutcomeView(null, null, null, "no verification profile is configured");
        }

        // Current result: the execution worktree, which is where the result commit is.
        var current = await _engineFor(workspace.WorkspacePath).RunAsync(
            setup.Profile,
            new VerificationRequest
            {
                WorkspaceRoot = workspace.WorkspacePath,
                EnvironmentAllowlist = setup.EnvironmentAllowlist,
                Label = "result",
            },
            ct);

        var view = new VerificationRunView(
            current.RequiredOutcome == VerificationOutcome.Passed,
            current.ProfileHash,
            [.. current.Steps.Select(s => ViewOf(s, setup.Profile))],
            current.TestSummary?.Total,
            current.TestSummary?.Passed,
            current.TestSummary?.FailedOrErrored);

        // A run the caller already stopped has nothing left to compare against, and asking
        // for a baseline now would start a fresh measurement under a token that is already
        // cancelled - producing a partial result that must never be remembered as this
        // commit's baseline. The current run stands on its own; cancellation shows up in it
        // as required steps that did not pass.
        if (ct.IsCancellationRequested)
        {
            return new VerificationOutcomeView(
                view, null, null, current.Describe() + "; cancelled before a baseline was measured");
        }

        var profileHash = ProfileHasher.Compute(setup.Profile);
        var key = new BaselineKey(repositoryId, baseCommitSha, profileHash);

        string? residual = null;

        var baseline = await _baselines.GetOrCaptureAsync(
            key,
            token => ProduceBaselineAsync(setup, baseCommitSha, token, r => residual = r),
            ct);

        var baselineView = baseline.Entry is { } entry
            ? new BaselineView(
                entry.Key.BaseCommitSha,
                entry.Summary.Passed,
                entry.Summary.FailedOrErrored,
                BaselineComparer.Compare(entry.Summary, current.TestSummary ?? entry.Summary).Description)
            : null;

        return new VerificationOutcomeView(view, baselineView, residual, current.Describe());
    }

    /// <summary>
    /// Flattens one step's machine result for the record that outlives this process.
    ///
    /// The executable is the resolved absolute path when there is one, and the step's
    /// declared executable when the command never got far enough to resolve it - both are
    /// what the Hub was actually asked to run, and nothing is invented when neither is
    /// available beyond saying so.
    /// </summary>
    private static VerificationStepView ViewOf(VerificationStepResult step, VerificationProfile profile)
    {
        // Profile step names are unique by construction, so this identifies the declaration
        // this result came from rather than merely one that looks like it.
        var declared = profile.Steps
            .FirstOrDefault(s => string.Equals(s.Name, step.StepName, StringComparison.Ordinal));

        return new VerificationStepView
        {
            StepName = step.StepName,
            Required = step.Required,
            Status = StatusOf(step.Status),
            Executable = step.ResolvedExecutable ?? declared?.Executable ?? "(unresolved)",
            Arguments = step.Arguments,
            ExitCode = step.ExitCode,
            DurationMilliseconds = step.Duration == TimeSpan.Zero
                ? null
                : (long)step.Duration.TotalMilliseconds,
            TestsPassed = step.TestSummary?.Passed,
            TestsFailed = step.TestSummary?.FailedOrErrored,
            TestsSkipped = step.TestSummary?.Skipped,
        };
    }

    /// <summary>
    /// The one conversion between the verification module's status vocabulary and the
    /// domain's.
    ///
    /// Exhaustive on purpose, with no fallback arm. A status this does not know is not
    /// something to record as a failure and move on from - it means the two vocabularies
    /// have drifted, and a persisted row would silently mean the wrong thing for as long as
    /// it exists. So it throws, here, where the drift was introduced.
    /// </summary>
    private static VerificationCommandStatus StatusOf(VerificationStepStatus status) => status switch
    {
        VerificationStepStatus.Passed => VerificationCommandStatus.Passed,
        VerificationStepStatus.Failed => VerificationCommandStatus.Failed,
        VerificationStepStatus.TimedOut => VerificationCommandStatus.TimedOut,
        VerificationStepStatus.Cancelled => VerificationCommandStatus.Cancelled,
        VerificationStepStatus.StartFailed => VerificationCommandStatus.StartFailed,
        VerificationStepStatus.ParseFailed => VerificationCommandStatus.ParseFailed,
        VerificationStepStatus.Skipped => VerificationCommandStatus.Skipped,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Unknown verification step status: machine truth would be persisted with the wrong meaning."),
    };

    /// <summary>
    /// Measures the baseline in a disposable tree pinned to the exact base commit.
    ///
    /// The pin is checked before the run and again after it. Verification commands are
    /// free to fill the tree with build output - that is why the tree is disposable - but
    /// a command that moved HEAD has measured a different commit, and a result like that
    /// must never be cached as this commit's baseline. Returning null is how the producer
    /// says "no baseline", which caches nothing.
    ///
    /// A cancelled measurement is refused on the same grounds and by the same means: half a
    /// suite is not this commit's score. The one thing cancellation does not stop is getting
    /// rid of the tree, which runs on its own bounded token - a snapshot holds no agent
    /// authored work, so leaving it behind buys nobody anything.
    /// </summary>
    private async Task<VerificationRunResult?> ProduceBaselineAsync(
        VerificationSetup setup,
        string baseCommitSha,
        CancellationToken ct,
        Action<string> reportResidual)
    {
        var snapshot = await _workspace.PrepareSnapshotAsync(
            new PinnedSnapshotRequest
            {
                RepositoryPath = setup.RepositoryPath,
                RepositoryTrustRoot = setup.RepositoryTrustRoot,
                SnapshotRoot = setup.SnapshotRoot,
                CommitSha = baseCommitSha,

                // Infrastructure identity only: it names a directory and never reaches a
                // baseline key or a context hash.
                SnapshotId = $"baseline-{baseCommitSha[..12]}-{Guid.CreateVersion7():N}",
            },
            ct);

        if (!snapshot.Succeeded || snapshot.Snapshot is null)
        {
            reportResidual("the baseline snapshot could not be created: " + snapshot.Describe());
            return null;
        }

        var pinned = snapshot.Snapshot;

        try
        {
            var before = await _workspace.VerifySnapshotPinAsync(pinned, ct);
            if (!before.StillPinned)
            {
                reportResidual("the baseline snapshot was not pinned before measuring: " + before.Detail);
                return null;
            }

            var result = await _engineFor(pinned.SnapshotPath).RunAsync(
                setup.Profile,
                new VerificationRequest
                {
                    WorkspaceRoot = pinned.SnapshotPath,
                    EnvironmentAllowlist = setup.EnvironmentAllowlist,
                    Label = "baseline",
                },
                ct);

            // Checked before the closing pin check, because that check would run on the
            // cancelled token and report a failure that says nothing about the commit. A
            // run that was stopped part way through is simply not a measurement.
            if (ct.IsCancellationRequested
                || result.Steps.Any(s => s.Status is VerificationStepStatus.Cancelled))
            {
                reportResidual("the baseline was cancelled before it finished and was not remembered");
                return null;
            }

            var after = await _workspace.VerifySnapshotPinAsync(pinned, ct);
            if (!after.StillPinned)
            {
                reportResidual("the baseline snapshot moved while measuring: " + after.Detail);
                return null;
            }

            return result;
        }
        finally
        {
            // Removal is owed whatever the caller decided, so it does not run on the
            // caller's token - and not on an unlimited one either: a tree that will not go
            // away is reported, never waited on for ever.
            using var disposal = new MandatoryWorkScope(_disposalBudget);

            var release = await _workspace.ReleaseSnapshotAsync(pinned, disposal.Token);

            if (release.State != WorkspaceReleaseState.Released)
            {
                // Said out loud rather than swallowed. It never changes what was measured,
                // and nothing here deletes repository state to force the issue.
                reportResidual($"the baseline snapshot at '{pinned.SnapshotPath}' could not be removed: {release.Detail}");
            }
        }
    }
}

/// <summary>What a repository needs in order to be verified.</summary>
public sealed record VerificationSetup(
    VerificationProfile Profile,
    IReadOnlyList<string> EnvironmentAllowlist,
    string RepositoryPath,
    string RepositoryTrustRoot,
    string SnapshotRoot);
