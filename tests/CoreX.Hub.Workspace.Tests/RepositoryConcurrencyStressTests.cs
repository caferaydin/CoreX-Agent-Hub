using System.Globalization;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Deterministic stress for the same repository critical section.
///
/// The intermittent failure this suite exists for was a shared &lt;repo&gt;/.git/config
/// access violation, so a single run proves nothing: the scenario is repeated until a
/// race that survives would have to be extremely unlikely, and every failure category is
/// reported rather than counted as one number.
/// </summary>
[Collection(StressCollection.Name)]
public sealed class RepositoryConcurrencyStressTests
{
    private const int Iterations = 50;
    private const int ConcurrentPreparationsPerIteration = 4;

    [Fact]
    public async Task Repeated_concurrent_preparation_on_one_repository_never_corrupts_shared_config()
    {
        var failures = new List<string>();
        var passes = 0;

        for (var iteration = 1; iteration <= Iterations; iteration++)
        {
            using var fixture = GitFixture.Create();
            await fixture.InitialiseRepositoryAsync();

            var attempts = Enumerable.Range(1, ConcurrentPreparationsPerIteration).ToArray();

            var results = await Task.WhenAll(attempts.Select(attempt =>
                fixture.Manager.PrepareAsync(fixture.Request(attempt: attempt), CancellationToken.None)));

            var iterationFailures = results
                .Where(r => !r.Succeeded)
                .Select(r => Categorise(iteration, r))
                .ToList();

            if (iterationFailures.Count > 0)
            {
                failures.AddRange(iterationFailures);
                continue;
            }

            // Every branch was created exactly once, and git agrees about every worktree.
            var branches = results
                .Select(r => r.Workspace!.BranchName)
                .OrderBy(b => b, StringComparer.Ordinal)
                .ToArray();

            if (!branches.SequenceEqual(attempts.Select(a => $"agent/HUB-001/{a}").Order(StringComparer.Ordinal)))
            {
                failures.Add($"iteration {iteration}: unexpected branch set [{string.Join(", ", branches)}]");
                continue;
            }

            var listed = await fixture.RequireGitAsync(
                fixture.RepositoryPath, "worktree", "list", "--porcelain");

            var missing = results
                .Where(r => !GitFixture.ToGitPath(listed).Contains(
                    GitFixture.ToGitPath(r.Workspace!.WorkspacePath), StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Workspace!.WorkspacePath)
                .ToList();

            if (missing.Count > 0)
            {
                failures.Add($"iteration {iteration}: git did not list [{string.Join(", ", missing)}]");
                continue;
            }

            // The shared repository config must still be readable and still declare the
            // worktree extension exactly once.
            var extension = await fixture.RequireGitAsync(
                fixture.RepositoryPath, "config", "--local", "--get-all", "extensions.worktreeConfig");

            if (extension.Trim() != "true")
            {
                failures.Add(
                    $"iteration {iteration}: extensions.worktreeConfig read back as '{extension.Trim()}'");
                continue;
            }

            passes++;
        }

        EvidenceLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"Stress(same-repo prepare x{ConcurrentPreparationsPerIteration}): iterations={Iterations} passed={passes} failed={failures.Count}"));

        foreach (var failure in failures)
        {
            EvidenceLog.Write("Stress failure: " + failure);
        }

        Assert.Equal(Iterations, passes);
        Assert.Empty(failures);
    }

    private static string Categorise(int iteration, WorkspacePreparationResult result)
    {
        var issue = result.Issues.FirstOrDefault();
        return $"iteration {iteration}: {issue?.Code.ToString() ?? "Unknown"}: {issue?.Detail}";
    }
}

/// <summary>
/// The stress suite saturates the machine with git processes, so it does not run beside
/// the other workspace tests: parallel unrelated suites would make its failure categories
/// ambiguous.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StressCollection
{
    public const string Name = "workspace-stress";
}
