using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The provenance the seam exists for.
///
/// A baseline keyed on BaseCommitSha has to be a measurement of that commit. Before this
/// seam nothing could materialise a tree at a commit the caller already held: the only
/// way to get a working tree resolved the default branch again, so a branch that moved
/// between pinning and measuring would silently produce a baseline for the wrong commit.
///
/// This proves the seam, not the Phase 5C wiring: no verification runs here and no
/// baseline is cached.
/// </summary>
public sealed class BaselineProvenanceTests
{
    [Fact]
    public async Task A_baseline_producer_cannot_accidentally_measure_the_moved_branch()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // X: the commit an execution pins, with a deliberately broken fixture in it.
        fixture.WriteRepositoryFile("src/broken.txt", "this is the broken state\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "broken fixture");

        var x = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        // Y: the default branch moves on while the execution is still running.
        fixture.WriteRepositoryFile("src/broken.txt", "this is the fixed state\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "fixed on main");

        var y = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");

        Assert.NotEqual(x, y);

        // The caller holds X and asks for X.
        var result = await fixture.Manager.PrepareSnapshotAsync(
            new PinnedSnapshotRequest
            {
                RepositoryPath = fixture.RepositoryPath,
                RepositoryTrustRoot = fixture.Root,
                SnapshotRoot = fixture.WorkspaceRoot,
                CommitSha = x,
                SnapshotId = "baseline-" + x[..8],
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());

        var snapshot = result.Snapshot!;

        // Requested X, materialised X, while the default branch says Y.
        Assert.Equal(x, snapshot.CommitSha);
        Assert.Equal(x, snapshot.ObservedHeadSha);
        Assert.Equal(x, await fixture.RequireGitAsync(snapshot.SnapshotPath, "rev-parse", "HEAD"));
        Assert.Equal(y, await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main"));

        // And the content really is the broken state, not the fixed one: a producer running
        // here measures X's behaviour, which is what BaselineKey(repository, X, profile)
        // claims to hold.
        var content = await File.ReadAllTextAsync(Path.Combine(snapshot.SnapshotPath, "src", "broken.txt"));
        Assert.Contains("broken state", content, StringComparison.Ordinal);
        Assert.DoesNotContain("fixed state", content, StringComparison.Ordinal);

        // The pin is still verifiable afterwards, which is how a caller will decide
        // whether results may be cached at all.
        var check = await fixture.Manager.VerifySnapshotPinAsync(snapshot, CancellationToken.None);
        Assert.True(check.StillPinned, check.Detail);
        Assert.Equal(x, check.ObservedHeadSha);

        EvidenceLog.Write(
            $"Baseline provenance: requested={x[..12]} snapshotHead={snapshot.ObservedHeadSha[..12]} defaultBranch={y[..12]}");
    }
}
