using System.Runtime.Versioning;
using CoreX.Hub.Context;

namespace CoreX.Hub.Context.Tests;

/// <summary>
/// Frozen Loop Zero acceptance criterion 6: two executions of the same semantic task
/// context at the same base commit produce the same ContextPackHash.
///
/// Execution identity is not context semantics. The execution id, the attempt number, the
/// generated agent branch, the physical workspace and the materialization root all belong
/// to one attempt rather than to the work, and none of them may move the hash.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExecutionIdentityIsNotContextTests
{
    [Fact]
    public async Task Two_attempts_at_the_same_work_produce_the_same_pack()
    {
        // Two different physical roots, so the materialization path differs too.
        using var attemptOne = ContextFixture.Create();
        using var attemptTwo = ContextFixture.Create();

        var first = attemptOne.Request();

        var second = attemptTwo.Request() with
        {
            ExecutionId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            AttemptNo = 2,
        };

        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.NotEqual(first.AttemptNo, second.AttemptNo);
        Assert.NotEqual(attemptOne.ContextRoot, attemptTwo.ContextRoot);

        var left = await attemptOne.Builder.BuildAsync(first, CancellationToken.None);
        var right = await attemptTwo.Builder.BuildAsync(second, CancellationToken.None);

        Assert.True(left.Succeeded, left.Describe());
        Assert.True(right.Succeeded, right.Describe());

        // The whole point.
        Assert.Equal(left.Pack!.ContextPackHash, right.Pack!.ContextPackHash);

        // And not merely equal hashes: the semantic files are byte identical.
        Assert.Equal(
            left.Pack.Files.Select(f => (f.RelativePath, f.SizeBytes, f.Sha256)),
            right.Pack.Files.Select(f => (f.RelativePath, f.SizeBytes, f.Sha256)));

        foreach (var file in left.Pack.Files)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(left.Pack.PackPath, Physical(file.RelativePath))),
                await File.ReadAllBytesAsync(Path.Combine(right.Pack.PackPath, Physical(file.RelativePath))));
        }
    }

    [Fact]
    public async Task No_semantic_file_mentions_the_attempt_or_a_generated_branch()
    {
        using var fixture = ContextFixture.Create();

        var result = await fixture.Builder.BuildAsync(
            fixture.Request() with { AttemptNo = 7 }, CancellationToken.None);

        foreach (var file in result.Pack!.Files)
        {
            var text = fixture.Read(result.Pack, file.RelativePath);

            Assert.DoesNotContain("Attempt", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("agent/HUB-001", text, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Pack.ExecutionId.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Repository_state_records_the_stable_branch_facts_only()
    {
        using var fixture = ContextFixture.Create();

        var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        var json = fixture.Read(result.Pack!, ContextPackBuilder.RepositoryStateFileName);

        // The stable source branch and the protected branch facts stay: they describe the
        // repository, not this attempt.
        Assert.Contains("\"DefaultBranch\": \"main\"", json, StringComparison.Ordinal);
        Assert.Contains("release", json, StringComparison.Ordinal);
        Assert.Contains(ContextFixture.BaseCommit, json, StringComparison.Ordinal);

        // The generated branch and the physical workspace path are gone entirely: not
        // merely unhashed, but absent, so no later change can quietly start hashing them.
        Assert.DoesNotContain("BranchName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceRelativePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("agent/", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_manifest_records_execution_provenance_without_hashing_it()
    {
        using var first = ContextFixture.Create();
        using var second = ContextFixture.Create();

        var one = await first.Builder.BuildAsync(first.Request(), CancellationToken.None);
        var two = await second.Builder.BuildAsync(
            second.Request() with
            {
                ExecutionId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                AttemptNo = 4,
            },
            CancellationToken.None);

        var manifestOne = await File.ReadAllTextAsync(
            Path.Combine(one.Pack!.PackPath, ContextPackBuilder.ManifestFileName));
        var manifestTwo = await File.ReadAllTextAsync(
            Path.Combine(two.Pack!.PackPath, ContextPackBuilder.ManifestFileName));

        // The manifests differ, because provenance is worth recording...
        Assert.NotEqual(manifestOne, manifestTwo);
        Assert.Contains("\"AttemptNo\": 4", manifestTwo, StringComparison.Ordinal);

        // ...and the hash does not, because manifest.json is excluded from the canonical
        // payload by construction: it is not in the inventory the hash is taken from.
        Assert.Equal(one.Pack.ContextPackHash, two.Pack.ContextPackHash);
        Assert.DoesNotContain(one.Pack.Files, f => f.RelativePath.Contains("manifest", StringComparison.Ordinal));
        Assert.Equal(ContextPackBuilder.ComputePackHash(two.Pack.Files), two.Pack.ContextPackHash);
    }

    private static string Physical(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);
}
