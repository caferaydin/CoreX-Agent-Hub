using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Where a verification run is allowed to happen.
///
/// Both measurements are of a commit, and both belong in a throwaway tree pinned to it.
/// The agent's worktree is not one of them - not because it would be untidy, but because a
/// worktree full of build output cannot be removed without forcing, and forcing an
/// execution worktree is exactly what the Hub does not do. These guards keep the shortcut
/// from creeping back: measuring the worktree, or cleaning it afterwards.
/// </summary>
public sealed class ResultVerificationInvariantTests
{
    private static string GatewaySource() =>
        File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "VerificationGateway.cs", StringComparison.Ordinal)));

    [Fact]
    public void The_verifier_is_never_told_where_the_worktree_is()
    {
        // The strongest form of "verification does not run in the execution worktree": the
        // port carries two commit ids and no path, so there is nothing to measure it with.
        var verify = typeof(IExecutionVerifier).GetMethod(nameof(IExecutionVerifier.VerifyAsync))!;

        var parameters = verify.GetParameters().Select(p => p.Name).ToList();

        Assert.Equal(["repositoryId", "baseCommitSha", "resultCommitSha", "ct"], parameters);
    }

    [Fact]
    public void Both_runs_are_measured_in_a_tree_pinned_to_a_commit()
    {
        var gateway = GatewaySource();

        // Two snapshots, two pins, two disposals - and the commits they are pinned to are
        // the two the port was given.
        Assert.Contains("CommitSha = resultCommitSha", gateway, StringComparison.Ordinal);
        Assert.Contains("CommitSha = baseCommitSha", gateway, StringComparison.Ordinal);

        Assert.Equal(2, Occurrences(gateway, "_workspace.PrepareSnapshotAsync("));
        Assert.Equal(2, Occurrences(gateway, "await DisposeSnapshotAsync("));

        // Every engine the gateway asks for is rooted at a snapshot it just materialised,
        // and there is no other kind of root it could be handed.
        Assert.Equal(2, Occurrences(gateway, "_engineFor("));
        Assert.Equal(2, Occurrences(gateway, "_engineFor(pinned.SnapshotPath)"));
        Assert.Equal(2, Occurrences(gateway, "WorkspaceRoot = pinned.SnapshotPath"));
    }

    [Fact]
    public void The_result_run_never_reaches_the_baseline_cache()
    {
        // A baseline keyed on the result commit would cache the post-change score as the
        // commit the execution started from, and every later execution on that base would
        // then look regression free. The key is built from the base commit and nothing else.
        var gateway = GatewaySource();

        Assert.Contains("new BaselineKey(repositoryId, baseCommitSha, profileHash)", gateway, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(gateway, "new BaselineKey("));
        Assert.Equal(1, Occurrences(gateway, "GetOrCaptureAsync("));

        // Only the baseline producer is registered with the caching service.
        Assert.Contains("ProduceBaselineAsync(setup, baseCommitSha", gateway, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_cleans_the_execution_worktree_to_make_it_releasable()
    {
        // The fix is to put the build output somewhere else, never to delete it afterwards.
        string[] workarounds =
        [
            "\"clean\"", "\"reset\"", "\"checkout\", \".\"", "--force", "Directory.Delete", "File.Delete",
        ];

        var scanned = RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Cli" + Path.DirectorySeparatorChar
                + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(scanned);

        foreach (var file in scanned)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var workaround in workarounds)
            {
                Assert.False(code.Contains(workaround, StringComparison.Ordinal), $"{name}: {workaround}");
            }
        }
    }

    [Fact]
    public void Snapshot_trees_are_still_created_and_removed_only_by_the_workspace_module()
    {
        // The gateway asks; it does not implement. Adding a second worktree command here
        // would put git in the composition layer.
        var gateway = GatewaySource();

        foreach (var owned in new[]
                 {
                     "PrepareSnapshotAsync", "VerifySnapshotPinAsync", "ReleaseSnapshotAsync",
                 })
        {
            Assert.Contains("_workspace." + owned, gateway, StringComparison.Ordinal);
        }

    }

    [Fact]
    public void A_tree_that_could_not_be_measured_still_reports_every_declared_step()
    {
        // Silence would make a Machine criterion unanswerable rather than unmet. The
        // gateway mirrors what the engine itself does when it cannot start a run: one row
        // per declared step, saying why.
        var gateway = GatewaySource();

        Assert.Contains("profile.Steps.Select(step => new VerificationStepResult", gateway, StringComparison.Ordinal);

        // And it can never be invented as a pass. Not a spelling the file happens to avoid -
        // the synthesis refuses the status outright, so a future caller reaching for it
        // fails rather than fabricating a verdict.
        Assert.Contains(
            "if (status == VerificationStepStatus.Passed)",
            gateway,
            StringComparison.Ordinal);

        Assert.Contains(
            "A run that did not happen cannot be reported as passing.",
            gateway,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_closing_pin_check_cannot_be_skipped_by_a_late_cancellation()
    {
        // It is what authorises calling the run this commit's result, so it is owed work:
        // a run that finished everything and was stopped a moment later must not be reported
        // as passing without anyone confirming which tree produced it.
        var gateway = GatewaySource();

        Assert.Contains(
            "_workspace.VerifySnapshotPinAsync(pinned, confirm.Token)",
            gateway,
            StringComparison.Ordinal);

        // Bounded, never CancellationToken.None - which the sequencing guard also forbids.
        Assert.Contains("using var confirm = new MandatoryWorkScope(", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", gateway, StringComparison.Ordinal);
    }

    [Fact]
    public void Preparing_a_result_tree_can_never_throw_out_of_the_pipeline()
    {
        // Taking the repository critical section throws outright on an already cancelled
        // token, and an escape here would strand an execution that already holds a result
        // commit - no terminal state, task stuck Running, waiting for the reconciler.
        var gateway = GatewaySource();

        // Every place that takes the repository critical section is wrapped, because
        // gate.WaitAsync throws outright on an already cancelled token and nothing between
        // here and the orchestrator's own call would catch it.
        Assert.Equal(3, Occurrences(gateway, "catch (OperationCanceledException)"));

        // Both trees: the result run, and the baseline the caching service produces.
        Assert.Contains("baseline = BaselineLookup.Miss;", gateway, StringComparison.Ordinal);

        // Disposal runs in a finally, where a throw would replace an already computed
        // verdict with a message about a directory.
        Assert.Contains("catch (Exception ex)", gateway, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        var at = 0;

        while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += token.Length;
        }

        return count;
    }

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
