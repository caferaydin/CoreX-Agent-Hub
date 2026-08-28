using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A host that died between the review and the row that records its verdict.
///
/// Recovery holds no reviewer and no workspace - there is no code path from it to a
/// provider - so what it can do is bounded, and the bound is the point. It reads what is
/// already durable and finishes the execution from that, or it says the review is missing.
/// It never obtains one, and it never invents a pass.
///
/// The window that matters most is the narrow one: the findings and the review step are on
/// the record, and only the execution's own completion is missing. Reading the findings and
/// asking the frozen gate is the only answer that agrees with what the pipeline would have
/// written; assuming a pass would accept work a reviewer had already blocked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReviewRecoveryTests
{
    [Fact]
    public async Task A_review_that_had_already_blocked_is_reconstructed_rather_than_assumed_clean()
    {
        await using var hub = await ReviewedAsync(ScriptedReviewer.Blocking());

        var report = hub.Report;

        Assert.Equal(ExecutionOutcome.ReviewBlocked, report.Outcome);

        // Wind the record back to the moment before the execution was completed. The review
        // step and its findings are exactly as the run left them.
        await RewindAsync(hub.Harness, report.ExecutionId, verifyStays: true);

        var recovered = await RecoverAsync(hub.Harness, report);

        Assert.Equal(ExecutionOutcome.ReviewBlocked, recovered.Outcome);
        Assert.Equal(TaskState.Ready, recovered.TaskState);

        var store = await hub.Harness.ReopenStoreAsync();

        // Nothing was re-run and nothing was deleted: the same findings still answer for it.
        Assert.Single(await store.ListFindingsAsync(report.ExecutionId, CancellationToken.None));

        var execution = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.ReviewBlocked, execution!.Outcome);
        Assert.Equal(report.ResultCommitSha, execution.ResultCommitSha);
    }

    [Fact]
    public async Task A_review_that_had_already_passed_is_reconstructed_as_a_pass()
    {
        await using var hub = await ReviewedAsync(ScriptedReviewer.Clean());

        var report = hub.Report;

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        await RewindAsync(hub.Harness, report.ExecutionId, verifyStays: true);

        var recovered = await RecoverAsync(hub.Harness, report);

        Assert.Equal(ExecutionOutcome.Passed, recovered.Outcome);
        Assert.Equal(TaskState.AwaitingAcceptance, recovered.TaskState);
    }

    [Fact]
    public async Task A_result_no_reviewer_ever_saw_is_never_recovered_into_a_pass()
    {
        await using var hub = await ReviewedAsync(ScriptedReviewer.Clean());

        var report = hub.Report;

        // The review step is wound back too: this is the host dying before the reviewer was
        // ever asked, which is the case recovery cannot repair and must not paper over.
        await RewindAsync(hub.Harness, report.ExecutionId, verifyStays: true, reviewPending: true);

        var recovered = await RecoverAsync(hub.Harness, report);

        Assert.Equal(ExecutionOutcome.ReviewUnusable, recovered.Outcome);
        Assert.Equal(TaskState.Ready, recovered.TaskState);

        var store = await hub.Harness.ReopenStoreAsync();
        var steps = await store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        var review = steps.Single(s => s.Kind == ExecutionStepKind.Review);

        Assert.Equal(ExecutionStepState.Skipped, review.State);
        Assert.Equal("ReviewNotRunAfterHostLoss", review.StateReason);
    }

    [Fact]
    public async Task A_review_left_running_by_a_dead_host_is_closed_rather_than_left_mid_flight()
    {
        await using var hub = await ReviewedAsync(ScriptedReviewer.Clean());

        var report = hub.Report;

        await RewindAsync(hub.Harness, report.ExecutionId, verifyStays: true, reviewRunning: true);

        var recovered = await RecoverAsync(hub.Harness, report);

        Assert.Equal(ExecutionOutcome.ReviewUnusable, recovered.Outcome);

        var store = await hub.Harness.ReopenStoreAsync();
        var review = (await store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None))
            .Single(s => s.Kind == ExecutionStepKind.Review);

        Assert.Equal(ExecutionStepState.Skipped, review.State);
    }

    /// <summary>Recovery has no reviewer to call, and the port it would need does not exist here.</summary>
    [Fact]
    public void Recovery_cannot_reach_a_provider_at_all()
    {
        var parameters = typeof(PostCommitRecoveryService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.DoesNotContain("IExecutionReviewer", parameters);
        Assert.DoesNotContain("IExecutionWorkspace", parameters);
        Assert.DoesNotContain("IAgentRuntimeGateway", parameters);
    }

    private sealed record Reviewed(OrchestrationHarness Harness, ExecutionRunReport Report) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private static async Task<Reviewed> ReviewedAsync(IExecutionReviewer reviewer)
    {
        var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;
        hub.Reviewer = reviewer;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.NotNull(report.ResultCommitSha);

        return new Reviewed(hub, report);
    }

    /// <summary>
    /// Puts the record back to the moment a crash would have left it.
    ///
    /// Written with SQL because the aggregates have no method for it, which is the point:
    /// this is damage, not a transition, and giving the domain a way to produce it would be
    /// giving it a way to produce it in production.
    /// </summary>
    private static async Task RewindAsync(
        OrchestrationHarness hub,
        Guid executionId,
        bool verifyStays,
        bool reviewPending = false,
        bool reviewRunning = false)
    {
        await hub.ReopenStoreAsync();

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(hub.Root, "hub.db")}");
        await connection.OpenAsync();

        var id = executionId.ToString("D").ToUpperInvariant();

        await ExecuteAsync(
            connection,
            "update Executions set State = 'Reviewing', Outcome = null where Id = $id;"
            + " update Tasks set State = 'Running' where Id in (select TaskId from Executions where Id = $id);",
            id);

        if (reviewPending || reviewRunning)
        {
            await ExecuteAsync(
                connection,
                "update ExecutionSteps set State = $state, StateReason = null, EndedAtUtc = null "
                + "where ExecutionId = $id and Kind = 'Review'",
                id,
                ("$state", reviewPending ? "Pending" : "Running"));
        }

        Assert.True(verifyStays, "the verify step is deliberately left as the run recorded it");
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        string id,
        params (string Name, object Value)[] extra)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);

        foreach (var (name, value) in extra)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.True(await command.ExecuteNonQueryAsync() > 0, $"the rewind matched no rows: {sql}");
    }

    private static async Task<PostCommitRecovery> RecoverAsync(
        OrchestrationHarness hub,
        ExecutionRunReport report)
    {
        var service = new PostCommitRecoveryService(
            hub.OpenStore, new RefusingVerifier(), hub.Clock, reviewRequired: true);

        var recovered = await service.RecoverAsync(
            [
                new PostCommitRecoveryRequired(
                    report.ExecutionId,
                    hub.TaskId,
                    report.ResultCommitSha!,
                    ExecutionState.Reviewing,
                    "the owning process is gone"),
            ],
            CancellationToken.None);

        return Assert.Single(recovered.Recovered);
    }

    /// <summary>Fails the test if anything asks for a measurement that is already durable.</summary>
    private sealed class RefusingVerifier : IExecutionVerifier
    {
        public Task<VerificationOutcomeView> VerifyAsync(
            Guid executionId,
            Guid repositoryId,
            string baseCommitSha,
            string resultCommitSha,
            CancellationToken ct) =>
            throw new InvalidOperationException("recovery re-measured a commit it should have read");
    }
}
