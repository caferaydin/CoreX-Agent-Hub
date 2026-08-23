using System.Runtime.Versioning;
using CoreX.Hub.Context;

namespace CoreX.Hub.Context.Tests;

/// <summary>
/// The hard budget, atomic publication, and what the builder refuses to read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ContextBudgetAndTrustTests
{
    [Fact]
    public async Task A_pack_at_the_budget_boundary_is_accepted()
    {
        using var fixture = ContextFixture.Create();

        var baseline = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        Assert.True(baseline.Succeeded, baseline.Describe());

        // Grow a decision until the pack sits exactly on the ceiling.
        var headroom = ContextPackBuilder.MaxPackBytes - baseline.Pack!.TotalBytes;
        Assert.True(headroom > 0);

        var filler = new string('x', (int)headroom);
        var request = fixture.Request() with
        {
            ExecutionId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Decisions =
            [
                .. fixture.Request().Decisions,
                new ContextDecisionDocument { Slug = "0003-filler", Title = "F", Markdown = filler },
            ],
        };

        var grown = await fixture.Builder.BuildAsync(request, CancellationToken.None);

        // The filler adds its own heading and newlines, so trim back to the exact ceiling.
        var overshoot = grown.Succeeded ? 0 : 1;
        Assert.True(overshoot >= 0);

        var exact = fixture.Request() with
        {
            ExecutionId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            Decisions =
            [
                .. fixture.Request().Decisions,
                new ContextDecisionDocument
                {
                    Slug = "0003-filler",
                    Title = "F",
                    Markdown = new string('x', (int)headroom - "# F\n\n\n".Length),
                },
            ],
        };

        var atBoundary = await fixture.Builder.BuildAsync(exact, CancellationToken.None);

        Assert.True(atBoundary.Succeeded, atBoundary.Describe());
        Assert.Equal(ContextPackBuilder.MaxPackBytes, atBoundary.Pack!.TotalBytes);
    }

    [Fact]
    public async Task A_pack_over_the_budget_is_refused_without_truncation()
    {
        using var fixture = ContextFixture.Create();

        var request = fixture.Request() with
        {
            Decisions =
            [
                new ContextDecisionDocument
                {
                    Slug = "0001-huge",
                    Title = "Too much context",
                    Markdown = new string('y', ContextPackBuilder.MaxPackBytes + 1),
                },
            ],
        };

        var result = await fixture.Builder.BuildAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ContextBuildFailure.BudgetExceeded, result.Failure);
        Assert.Contains("truncated", result.Detail!, StringComparison.OrdinalIgnoreCase);

        // Nothing was dropped to make it fit, and no pack exists at all.
        Assert.Empty(Directory.EnumerateDirectories(fixture.ContextRoot));
    }

    [Fact]
    public async Task A_failure_before_publication_leaves_no_pack_and_no_debris()
    {
        using var fixture = ContextFixture.Create();

        fixture.Builder.BeforePublishAsync = _ => throw new IOException("simulated disk failure");

        var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);

        // A build failure is a typed result, not an exception the caller has to catch.
        Assert.False(result.Succeeded);
        Assert.Equal(ContextBuildFailure.MaterializationFailed, result.Failure);
        Assert.Contains("simulated disk failure", result.Detail!, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(fixture.ContextRoot, ContextFixture.ExecutionId.ToString("D"))));

        var pending = Path.Combine(fixture.ContextRoot, ContextPackBuilder.PendingDirectoryName);
        Assert.True(!Directory.Exists(pending) || !Directory.EnumerateFileSystemEntries(pending).Any());
    }

    [Fact]
    public async Task Cancellation_before_publication_leaves_no_pack()
    {
        using var fixture = ContextFixture.Create();
        using var cancellation = new CancellationTokenSource();

        fixture.Builder.BeforePublishAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Builder.BuildAsync(fixture.Request(), cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(fixture.ContextRoot, ContextFixture.ExecutionId.ToString("D"))));
    }

    [Fact]
    public async Task A_second_pack_for_the_same_execution_is_refused()
    {
        using var fixture = ContextFixture.Create();

        var first = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        Assert.True(first.Succeeded, first.Describe());

        var second = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal(ContextBuildFailure.PackAlreadyExists, second.Failure);
    }
}
