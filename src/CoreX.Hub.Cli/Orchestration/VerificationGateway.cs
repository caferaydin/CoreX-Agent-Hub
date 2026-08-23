using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Cli.Verification;
using CoreX.Hub.Domain.Catalog;
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

    public VerificationGateway(
        WorkspaceManager workspace,
        VerificationBaselineService baselines,
        Func<string, VerificationEngine> engineFor,
        Func<Guid, VerificationSetup?> setupFor)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _engineFor = engineFor ?? throw new ArgumentNullException(nameof(engineFor));
        _setupFor = setupFor ?? throw new ArgumentNullException(nameof(setupFor));
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

        var profileHash = ProfileHasher.Compute(setup.Profile);
        var key = new BaselineKey(repositoryId, baseCommitSha, profileHash);

        string? residual = null;

        var baseline = await _baselines.GetOrCaptureAsync(
            key,
            token => ProduceBaselineAsync(setup, baseCommitSha, token, r => residual = r),
            ct);

        var view = new VerificationRunView(
            current.RequiredOutcome == VerificationOutcome.Passed,
            current.ProfileHash,
            [.. current.Steps.Select(s => new VerificationStepView(s.StepName, s.Required, s.Status.ToString(), s.ExitCode))],
            current.TestSummary?.Total,
            current.TestSummary?.Passed,
            current.TestSummary?.FailedOrErrored);

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
    /// Measures the baseline in a disposable tree pinned to the exact base commit.
    ///
    /// The pin is checked before the run and again after it. Verification commands are
    /// free to fill the tree with build output - that is why the tree is disposable - but
    /// a command that moved HEAD has measured a different commit, and a result like that
    /// must never be cached as this commit's baseline. Returning null is how the producer
    /// says "no baseline", which caches nothing.
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
            var release = await _workspace.ReleaseSnapshotAsync(pinned, CancellationToken.None);

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
