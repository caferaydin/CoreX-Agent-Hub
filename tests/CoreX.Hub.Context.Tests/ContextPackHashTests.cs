using System.Runtime.Versioning;
using CoreX.Hub.Context;

namespace CoreX.Hub.Context.Tests;

/// <summary>
/// ContextPackHash is an identity: it changes when the pack means something different and
/// never when only the machine, the path or the clock differs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ContextPackHashTests
{
    [Fact]
    public async Task The_same_semantic_input_produces_the_same_hash()
    {
        using var first = ContextFixture.Create();
        using var second = ContextFixture.Create();

        var left = await first.Builder.BuildAsync(first.Request(), CancellationToken.None);
        var right = await second.Builder.BuildAsync(second.Request(), CancellationToken.None);

        Assert.True(left.Succeeded, left.Describe());
        Assert.True(right.Succeeded, right.Describe());

        // Two different physical roots, two different temporary directory names, same
        // meaning - so the same hash.
        Assert.NotEqual(first.ContextRoot, second.ContextRoot);
        Assert.Equal(left.Pack!.ContextPackHash, right.Pack!.ContextPackHash);
        Assert.Equal(64, left.Pack.ContextPackHash.Length);
        Assert.True(left.Pack.ContextPackHash.All(Uri.IsHexDigit));
    }

    [Fact]
    public async Task Rebuilding_the_same_pack_twice_produces_identical_bytes()
    {
        using var fixture = ContextFixture.Create();

        var first = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        Assert.True(first.Succeeded, first.Describe());

        // The physical pack is gone, the meaning is not.
        Directory.Delete(first.Pack!.PackPath, recursive: true);

        var second = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        Assert.True(second.Succeeded, second.Describe());

        Assert.Equal(first.Pack.ContextPackHash, second.Pack!.ContextPackHash);
        Assert.Equal(
            first.Pack.Files.Select(f => (f.RelativePath, f.Sha256)),
            second.Pack.Files.Select(f => (f.RelativePath, f.Sha256)));
    }

    [Theory]
    [MemberData(nameof(SemanticChanges))]
    public async Task A_semantic_change_changes_the_hash(string label, Func<ContextBuildRequest, ContextBuildRequest> change)
    {
        using var baseline = ContextFixture.Create();
        using var changed = ContextFixture.Create();

        var original = await baseline.Builder.BuildAsync(baseline.Request(), CancellationToken.None);
        var modified = await changed.Builder.BuildAsync(change(changed.Request()), CancellationToken.None);

        Assert.True(original.Succeeded, original.Describe());
        Assert.True(modified.Succeeded, modified.Describe());

        Assert.True(
            original.Pack!.ContextPackHash != modified.Pack!.ContextPackHash,
            $"changing the {label} did not change the context pack hash");
    }

    public static TheoryData<string, Func<ContextBuildRequest, ContextBuildRequest>> SemanticChanges() => new()
    {
        { "objective", r => r with { Objective = "Something else entirely." } },
        { "allowed scope", r => r with { AllowedScope = ["src/", "docs/"] } },
        { "forbidden scope", r => r with { ForbiddenScope = [] } },
        {
            "acceptance criterion",
            r => r with
            {
                AcceptanceCriteria = [.. r.AcceptanceCriteria.Select(c =>
                    c.Order == 1 ? c with { Text = "The test suite passes twice." } : c)],
            }
        },
        {
            "verification reference",
            r => r with
            {
                AcceptanceCriteria = [.. r.AcceptanceCriteria.Select(c =>
                    c.Order == 1 ? c with { VerificationRef = "build" } : c)],
            }
        },
        {
            "repository rule",
            r => r with { RepositoryRules = new ContextRuleDocument { Title = "sample-repo", Markdown = "Touch infra freely." } }
        },
        {
            "organization rule",
            r => r with { OrganizationRules = null }
        },
        {
            "decision",
            r => r with
            {
                Decisions = [.. r.Decisions.Where(d => d.Slug != "0002-logging")],
            }
        },
        {
            "base commit",
            r => r with
            {
                RepositoryState = r.RepositoryState with
                {
                    BaseCommitSha = "fedcba0987654321fedcba0987654321fedcba09",
                },
            }
        },
        {
            // The stable source branch is a genuine semantic fact; the generated agent
            // branch of one attempt is not, and no longer reaches the pack at all.
            "default branch",
            r => r with { RepositoryState = r.RepositoryState with { DefaultBranch = "trunk" } }
        },
        {
            "protected branches",
            r => r with { RepositoryState = r.RepositoryState with { ProtectedBranches = ["main"] } }
        },
        { "task key", r => r with { TaskKey = "HUB-002" } },
    };
}
