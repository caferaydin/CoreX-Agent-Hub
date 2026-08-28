using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Composition.HardWorkspace;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// The verifier for a hard isolated organization: the same measurement, on the other side of the
/// boundary.
///
/// Verification is where repository-controlled code is most obviously executed - a test suite is a
/// program the repository wrote, and it runs minutes after an agent was free to edit it. Until this
/// existed a hard organization's tests ran through the Windows executor, as the developer, on the
/// developer's filesystem, which meant an agent perfectly contained for its own turn could put
/// anything it liked in a test file and have the Hub run it with every boundary switched off.
///
/// So every part of the run that touches repository content is inside the environment: the tree is
/// a Linux native throwaway worktree pinned to the commit, the commands are placed in this
/// execution's own namespace and mount sandbox, the tool a logical name resolves to is settled by
/// the organization's own map and measured inside the environment, and the report is read back
/// through the trusted service identity. No Windows filesystem authority appears anywhere on this
/// path.
///
/// What is deliberately identical to the logical gateway is everything downstream of the bytes.
/// The same <see cref="VerificationEngine"/> decides step ordering and status, the same parsers read
/// the report, the same baseline service compares runs and the same views are persisted. Two
/// engines would be two places for that meaning to drift, and two organizations would then report
/// the same verdict having answered different questions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardVerificationGateway : IExecutionVerifier
{
    private readonly HardVerificationSnapshots _snapshots;
    private readonly VerificationBaselineService _baselines;
    private readonly Func<Guid, string, VerificationEngine> _engineFor;
    private readonly Func<Guid, VerificationSetup?> _setupFor;
    private readonly TimeSpan _disposalBudget;

    /// <summary>
    /// Built from the execution's own environment, never from a ready-made executor.
    /// </summary>
    /// <param name="engineFor">
    /// Given the execution and the pinned tree, an engine whose commands land inside that
    /// execution's boundary. It takes the execution identity because the boundary is per execution:
    /// an engine built without one could only be built against some other execution's namespace.
    /// </param>
    internal HardVerificationGateway(
        HardVerificationSnapshots snapshots,
        VerificationBaselineService baselines,
        Func<Guid, string, VerificationEngine> engineFor,
        Func<Guid, VerificationSetup?> setupFor,
        TimeSpan? snapshotDisposalBudget = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
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
        // An execution identity is required rather than optional. Every boundary below is resolved
        // from it, and Guid.Empty is not "unknown" or "logical" - it is a caller that does not know
        // which execution's namespace its commands belong in.
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);

        var setup = _setupFor(repositoryId);

        if (setup is null)
        {
            return new VerificationOutcomeView(null, null, null, "no verification profile is configured");
        }

        var residuals = new List<string>();

        var measured = await MeasureAsync(
            executionId, repositoryId, setup, resultCommitSha, "result", ct, residuals.Add);

        var current = measured.Run;

        var detail = measured.Problem is { } problem
            ? current.Describe() + "; " + problem
            : current.Describe();

        // Passing is a claim about a measurement that happened, and RequiredOutcome alone cannot
        // carry it: a profile may declare no required step, and then "no required step failed" is
        // vacuously true of a run that never ran.
        var passed = measured.Problem is null
                     && !ct.IsCancellationRequested
                     && current.RequiredOutcome == VerificationOutcome.Passed;

        var view = new VerificationRunView(
            passed,
            current.ProfileHash,
            [.. current.Steps.Select(s => HardVerificationViews.Of(s, setup.Profile))],
            current.TestSummary?.Total,
            current.TestSummary?.Passed,
            current.TestSummary?.FailedOrErrored);

        // A run the caller already stopped has nothing to compare against, and starting a baseline
        // now would measure it under a token that is already cancelled - producing a partial result
        // that must never be remembered as this commit's baseline.
        if (ct.IsCancellationRequested)
        {
            return new VerificationOutcomeView(
                view, null, Combine(residuals), detail + "; cancelled before a baseline was measured");
        }

        var profileHash = ProfileHasher.Compute(setup.Profile);
        var key = new BaselineKey(repositoryId, baseCommitSha, profileHash);

        BaselineLookup baseline;

        try
        {
            baseline = await _baselines.GetOrCaptureAsync(
                key,
                token => ProduceBaselineAsync(
                    executionId, repositoryId, setup, baseCommitSha, token, residuals.Add),
                ct);
        }
        catch (OperationCanceledException)
        {
            residuals.Add("the baseline was cancelled before it could be measured");
            baseline = BaselineLookup.Miss;
        }

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

    private static string? Combine(IReadOnlyList<string> residuals) =>
        residuals.Count == 0 ? null : string.Join("; ", residuals);

    /// <summary>
    /// Measures one commit in a disposable Linux tree pinned to it.
    ///
    /// The pin is checked before the run and again afterwards, and the closing check is owed work:
    /// it runs on its own bounded token and is never skipped because the caller cancelled. Skipping
    /// it would let a run that finished and was stopped a moment later be reported as passing
    /// without anyone having confirmed which tree produced it.
    ///
    /// Something is always returned, never null and never an exception. Every way of not measuring
    /// the commit is reported as one step result per declared step, so a criterion naming a step is
    /// told why it did not pass rather than finding nothing recorded.
    /// </summary>
    private async Task<(VerificationRunResult Run, string? Problem)> MeasureAsync(
        Guid executionId,
        Guid repositoryId,
        VerificationSetup setup,
        string commitSha,
        string purpose,
        CancellationToken ct,
        Action<string> reportResidual)
    {
        if (ct.IsCancellationRequested)
        {
            return HardVerificationViews.Reported(
                setup.Profile,
                VerificationStepStatus.Cancelled,
                $"the run was cancelled before the {purpose} could be measured");
        }

        var (snapshot, problem) = await _snapshots.PrepareAsync(repositoryId, commitSha, purpose, ct);

        if (snapshot is null)
        {
            return HardVerificationViews.Reported(
                setup.Profile,
                VerificationStepStatus.StartFailed,
                $"the {purpose} could not be measured: {problem}");
        }

        try
        {
            var before = await _snapshots.VerifyPinAsync(snapshot, ct);

            if (!before.StillPinned)
            {
                return HardVerificationViews.Reported(
                    setup.Profile,
                    VerificationStepStatus.StartFailed,
                    $"the {purpose} tree was not pinned before measuring: {before.Detail}");
            }

            var result = await _engineFor(executionId, snapshot.SnapshotPath).RunAsync(
                setup.Profile,
                new VerificationRequest
                {
                    WorkspaceRoot = snapshot.SnapshotPath,
                    EnvironmentAllowlist = setup.EnvironmentAllowlist,
                    Label = purpose,
                },
                ct);

            using var confirm = new MandatoryWorkScope(_disposalBudget);

            var after = await _snapshots.VerifyPinAsync(snapshot, confirm.Token);

            if (!after.StillPinned)
            {
                return HardVerificationViews.Reported(
                    setup.Profile,
                    VerificationStepStatus.StartFailed,
                    $"the {purpose} tree moved while measuring, so what it produced is not this commit's "
                    + $"result: {after.Detail}");
            }

            return (result, null);
        }
        catch (OperationCanceledException)
        {
            return HardVerificationViews.Reported(
                setup.Profile,
                VerificationStepStatus.Cancelled,
                $"the run was cancelled while the {purpose} was being measured");
        }
        finally
        {
            await DisposeSnapshotAsync(snapshot, purpose, reportResidual);
        }
    }

    /// <summary>
    /// Measures the baseline, and refuses to remember anything that is not this commit's score.
    ///
    /// Returning null is how the producer says "no baseline", which caches nothing. A cancelled
    /// measurement is refused on the same grounds: half a suite is not a score.
    /// </summary>
    private async Task<VerificationRunResult?> ProduceBaselineAsync(
        Guid executionId,
        Guid repositoryId,
        VerificationSetup setup,
        string baseCommitSha,
        CancellationToken ct,
        Action<string> reportResidual)
    {
        var measured = await MeasureAsync(
            executionId, repositoryId, setup, baseCommitSha, "baseline", ct, reportResidual);

        if (measured.Problem is { } problem)
        {
            reportResidual("the baseline was not measured: " + problem);
            return null;
        }

        if (ct.IsCancellationRequested
            || measured.Run.Steps.Any(s => s.Status is VerificationStepStatus.Cancelled))
        {
            reportResidual("the baseline was cancelled before it finished and was not remembered");
            return null;
        }

        return measured.Run;
    }

    /// <summary>
    /// Gets rid of a throwaway tree, and never lets that get in the way of the answer.
    ///
    /// Removal is owed whatever the caller decided, so it runs on its own bounded token; and it runs
    /// inside a finally, where a thrown exception would replace a verdict that had already been
    /// computed with a message about a directory.
    /// </summary>
    private async Task DisposeSnapshotAsync(
        HardPinnedSnapshot snapshot,
        string purpose,
        Action<string> reportResidual)
    {
        try
        {
            using var disposal = new MandatoryWorkScope(_disposalBudget);

            if (await _snapshots.ReleaseAsync(snapshot, disposal.Token) is { } residual)
            {
                // Said out loud and nothing more. A throwaway tree that would not go away changes
                // nothing about what was measured, and the agent's own worktree is never touched to
                // make up for it.
                reportResidual($"the {purpose} tree at '{snapshot.SnapshotPath}' could not be removed: {residual}");
            }
        }
        catch (Exception ex)
        {
            reportResidual($"the {purpose} tree at '{snapshot.SnapshotPath}' could not be removed: {ex.Message}");
        }
    }
}

/// <summary>
/// Turning one isolated step's machine result into the record that outlives this process.
///
/// Held apart from the gateway because it is the part that must not differ between a logical run
/// and a hard one. A status this does not know is not something to record as a failure and move on
/// from - it means two vocabularies have drifted, and a persisted row would silently mean the wrong
/// thing for as long as it exists.
/// </summary>
internal static class HardVerificationViews
{
    public static VerificationStepView Of(VerificationStepResult step, VerificationProfile profile)
    {
        var declared = profile.Steps
            .FirstOrDefault(s => string.Equals(s.Name, step.StepName, StringComparison.Ordinal));

        return new VerificationStepView
        {
            StepName = step.StepName,
            Required = step.Required,
            Status = Status(step.Status),
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

    public static (VerificationRunResult Run, string? Problem) Reported(
        VerificationProfile profile,
        VerificationStepStatus status,
        string diagnostic) =>
        (Unmeasured(profile, status, diagnostic), diagnostic);

    private static VerificationRunResult Unmeasured(
        VerificationProfile profile,
        VerificationStepStatus status,
        string diagnostic)
    {
        // The one thing this may never say. A tree that was not measured cannot have passed, and a
        // caller reaching for Passed here would be inventing the verdict the pinning exists to
        // protect.
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
    /// The one conversion between the verification module's status vocabulary and the domain's.
    ///
    /// Exhaustive, with no fallback arm, and identical to the logical gateway's by construction: a
    /// hard organization and a logical one must mean the same thing by the same word.
    /// </summary>
    private static VerificationCommandStatus Status(VerificationStepStatus status) => status switch
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
}
