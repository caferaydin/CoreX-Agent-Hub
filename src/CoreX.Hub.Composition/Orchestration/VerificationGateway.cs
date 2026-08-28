using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Composition.Verification;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Composition layer adapter joining the orchestrator to the Phase 4 verification engine.
///
/// It measures two commits and is careful never to confuse them: the result at the Hub's
/// own commit, the baseline at the execution's base commit. Both are measured in a
/// disposable tree pinned to that exact commit, and neither is measured in the agent's
/// worktree.
///
/// That is not tidiness. Verification fills a tree with build output, and a worktree full
/// of build output cannot be removed without forcing - which the Hub never does to an
/// execution worktree. Measuring a throwaway copy instead is what lets the real worktree
/// stay exactly as the commit left it, and so be released once a person has decided. It
/// also makes the measurement honestly about the commit rather than about a directory that
/// happens to contain it.
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
        Guid executionId,
        Guid repositoryId,
        string baseCommitSha,
        string resultCommitSha,
        CancellationToken ct)
    {
        var setup = _setupFor(repositoryId);

        if (setup is null)
        {
            return new VerificationOutcomeView(null, null, null, "no verification profile is configured");
        }

        var residuals = new List<string>();

        // The result: measured in a throwaway tree pinned to the Hub's own commit.
        var measured = await MeasureResultAsync(setup, resultCommitSha, ct, residuals.Add);
        var current = measured.Run;

        // What the run says about itself, and - when the commit could not be measured at
        // all - the reason, which no list of step statuses can carry on its own.
        var detail = measured.Problem is { } problem
            ? current.Describe() + "; " + problem
            : current.Describe();

        // Passing is a claim about a measurement that happened. RequiredOutcome alone cannot
        // carry it: a profile may declare no required step at all, and then "no required
        // step failed" is vacuously true of a run that never ran. So a reported problem, and
        // a caller who has already stopped, both deny the claim outright rather than relying
        // on the step statuses to deny it for them.
        var passed = measured.Problem is null
                     && !ct.IsCancellationRequested
                     && current.RequiredOutcome == VerificationOutcome.Passed;

        var view = new VerificationRunView(
            passed,
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
                view,
                null,
                Combine(residuals),
                detail + "; cancelled before a baseline was measured");
        }

        var profileHash = ProfileHasher.Compute(setup.Profile);
        var key = new BaselineKey(repositoryId, baseCommitSha, profileHash);

        BaselineLookup baseline;

        try
        {
            baseline = await _baselines.GetOrCaptureAsync(
                key,
                token => ProduceBaselineAsync(setup, baseCommitSha, token, residuals.Add),
                ct);
        }
        catch (OperationCanceledException)
        {
            // The check above is not a guarantee: cancellation can arrive between it and the
            // repository critical section, which throws outright. Nothing between here and
            // the orchestrator's own call catches that, so an escape would leave an execution
            // holding a result commit with no terminal state and a task stuck Running.
            // A baseline is comparison context, never a verdict, so its loss is a sentence.
            residuals.Add("the baseline was cancelled before it could be measured");
            baseline = BaselineLookup.Miss;
        }

        // Comparing the baseline against itself would read as "unchanged", which is a claim
        // about this commit's score. A run that produced no counters at all - because it
        // could not be measured - has no score to make that claim from, so the comparison is
        // declined rather than fabricated.
        var baselineView = baseline.Entry is { } entry
            ? new BaselineView(
                entry.Key.BaseCommitSha,
                entry.Summary.Passed,
                entry.Summary.FailedOrErrored,
                current.TestSummary is { } summary
                    ? BaselineComparer.Compare(entry.Summary, summary).Description
                    : "this run produced no test counters, so there is nothing to compare")
            : null;

        return new VerificationOutcomeView(view, baselineView, Combine(residuals), detail);
    }

    /// <summary>Both snapshots can leave a residual, and neither may hide the other.</summary>
    private static string? Combine(IReadOnlyList<string> residuals) =>
        residuals.Count == 0 ? null : string.Join("; ", residuals);

    /// <summary>
    /// Measures the result in a disposable tree pinned to the exact commit the Hub made.
    ///
    /// The pin is checked before the run and again after it, for the same reason the
    /// baseline checks it: build output is expected, but a command that moved HEAD has
    /// measured a different commit. When that happens the measurement is thrown away - a
    /// result attributed to the wrong commit is worse than no result, because it would be
    /// persisted as this execution's machine truth and answered against ever afterwards.
    ///
    /// Something is always returned, never null and never an exception. Every way of not
    /// measuring the commit is reported as one step result per declared step, the way the
    /// engine itself reports a run it could not start: a criterion that names a step then
    /// says why it did not pass instead of saying nothing at all, and no caller has to
    /// decide what an absent run means.
    /// </summary>
    private async Task<(VerificationRunResult Run, string? Problem)> MeasureResultAsync(
        VerificationSetup setup,
        string resultCommitSha,
        CancellationToken ct,
        Action<string> reportResidual)
    {
        // Asked before anything is created. Preparing a snapshot takes the repository
        // critical section, which throws outright on a token that is already cancelled, and
        // an exception here would escape the whole pipeline and strand an execution that
        // already holds a result commit.
        if (ct.IsCancellationRequested)
        {
            return Reported(setup.Profile, VerificationStepStatus.Cancelled, "the run was cancelled before the result could be measured");
        }

        PinnedSnapshotResult snapshot;

        try
        {
            snapshot = await _workspace.PrepareSnapshotAsync(
                new PinnedSnapshotRequest
                {
                    RepositoryPath = setup.RepositoryPath,
                    RepositoryTrustRoot = setup.RepositoryTrustRoot,
                    SnapshotRoot = setup.SnapshotRoot,
                    CommitSha = resultCommitSha,

                    // Infrastructure identity only: it names a directory and never reaches a
                    // baseline key, a context hash or a criterion. The prefix keeps a result
                    // tree and a baseline tree apart on disk for anyone looking at them.
                    SnapshotId = $"result-{resultCommitSha[..12]}-{Guid.CreateVersion7():N}",
                },
                ct);
        }
        catch (OperationCanceledException)
        {
            return Reported(setup.Profile, VerificationStepStatus.Cancelled, "the run was cancelled while the result tree was being prepared");
        }

        if (!snapshot.Succeeded || snapshot.Snapshot is null)
        {
            return Reported(setup.Profile, VerificationStepStatus.StartFailed, "the result could not be measured: " + snapshot.Describe());
        }

        var pinned = snapshot.Snapshot;

        try
        {
            var before = await _workspace.VerifySnapshotPinAsync(pinned, ct);

            if (!before.StillPinned)
            {
                return Reported(setup.Profile, VerificationStepStatus.StartFailed, "the result tree was not pinned before measuring: " + before.Detail);
            }

            var result = await _engineFor(pinned.SnapshotPath).RunAsync(
                setup.Profile,
                new VerificationRequest
                {
                    WorkspaceRoot = pinned.SnapshotPath,
                    EnvironmentAllowlist = setup.EnvironmentAllowlist,
                    Label = "result",
                },
                ct);

            // The closing check is what authorises calling this run the commit's result, so
            // it is owed work rather than forward work: it runs on its own bounded token and
            // is never skipped because the caller cancelled. Skipping it would let a run
            // that finished everything and was stopped a moment later be reported as passing
            // without anyone having confirmed which tree produced it.
            using var confirm = new MandatoryWorkScope(_disposalBudget);

            var after = await _workspace.VerifySnapshotPinAsync(pinned, confirm.Token);

            if (!after.StillPinned)
            {
                return Reported(
                    setup.Profile,
                    VerificationStepStatus.StartFailed,
                    "the result tree moved while measuring, so what it produced is not this commit's "
                    + "result: " + after.Detail);
            }

            return (result, null);
        }
        catch (OperationCanceledException)
        {
            return Reported(setup.Profile, VerificationStepStatus.Cancelled, "the run was cancelled while the result was being measured");
        }
        finally
        {
            await DisposeSnapshotAsync(pinned, "result", reportResidual);
        }
    }

    /// <summary>
    /// A run that never happened, described one declared step at a time.
    ///
    /// The engine already answers this way when it cannot start a run at all, and matching
    /// it keeps one shape everywhere: every step of the profile gets a row, so a Machine
    /// criterion naming any of them can be told why it is not met rather than finding
    /// nothing recorded. It is never a pass, because only Passed is.
    /// </summary>
    private static (VerificationRunResult Run, string? Problem) Reported(
        VerificationProfile profile,
        VerificationStepStatus status,
        string diagnostic) =>
        (Unmeasured(profile, status, diagnostic), diagnostic);

    private static VerificationRunResult Unmeasured(
        VerificationProfile profile,
        VerificationStepStatus status,
        string diagnostic)
    {
        // The one thing this may never say. A tree that was not measured cannot have passed,
        // and a caller reaching for Passed here would be inventing the verdict the whole
        // pinning exercise exists to protect.
        if (status == VerificationStepStatus.Passed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "A run that did not happen cannot be reported as passing.");
        }

        var now = DateTimeOffset.UtcNow;

        return new VerificationRunResult
        {
            ProfileName = profile.Name,
            ProfileHash = ProfileHasher.Compute(profile),
            StartedAtUtc = now,
            EndedAtUtc = now,
            Steps =
            [
                .. profile.Steps.Select(step => new VerificationStepResult
                {
                    StepName = step.Name,
                    Required = step.Required,
                    Status = status,
                    Arguments = step.Args,
                    Diagnostic = diagnostic,
                }),
            ],
        };
    }

    /// <summary>
    /// Gets rid of a throwaway tree, and never lets that get in the way of the answer.
    ///
    /// Removal is owed whatever the caller decided, so it runs on its own bounded token
    /// rather than the caller's - and it runs inside a finally, where a thrown exception
    /// would replace a verdict that had already been computed with a message about a
    /// directory. So nothing is allowed out of here except a sentence.
    /// </summary>
    private async Task DisposeSnapshotAsync(
        PinnedSnapshot snapshot,
        string purpose,
        Action<string> reportResidual)
    {
        try
        {
            using var disposal = new MandatoryWorkScope(_disposalBudget);

            var release = await _workspace.ReleaseSnapshotAsync(snapshot, disposal.Token);

            if (release.State != WorkspaceReleaseState.Released)
            {
                // Said out loud and nothing more. A throwaway tree that would not go away
                // changes nothing about what was measured, and the agent's own worktree is
                // never touched to make up for it.
                reportResidual(
                    $"the {purpose} tree at '{snapshot.SnapshotPath}' could not be removed: {release.Detail}");
            }
        }
        catch (Exception ex)
        {
            reportResidual($"the {purpose} tree at '{snapshot.SnapshotPath}' could not be removed: {ex.Message}");
        }
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
            await DisposeSnapshotAsync(pinned, "baseline", reportResidual);
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
