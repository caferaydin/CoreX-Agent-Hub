using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The crash that cannot be closed by writing a terminal state: the host died after the Hub
/// had already committed its result.
///
/// Every terminal error state asserts the attempt produced nothing, and this one produced a
/// commit that is really in the repository - so recovery goes forward from what is already
/// true. These tests take a run that finished, wind the record back to the exact moment a
/// crash would have left it, and ask what recovery makes of it.
///
/// Winding back is done with SQL on purpose. The aggregates have no method for it, which is
/// the point: this is damage, not a transition, and injecting it any other way would mean
/// giving the domain a way to produce it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PostCommitRecoveryTests
{
    /// <summary>A verifier that fails the test if anything asks it to measure again.</summary>
    private sealed class RefusingVerifier : IExecutionVerifier
    {
        public Task<VerificationOutcomeView> VerifyAsync(
            Guid executionId,
            Guid repositoryId,
            string baseCommitSha,
            string resultCommitSha,
            CancellationToken ct) =>
            throw new InvalidOperationException(
                "recovery re-measured a commit whose verification was already durable");
    }

    private static async Task<(OrchestrationHarness Hub, ExecutionRunReport Report)> RunPassingAsync()
    {
        var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        return (hub, report);
    }

    /// <summary>Puts the record back to the moment before the execution was completed.</summary>
    private static async Task RewindAsync(OrchestrationHarness hub, Guid executionId, string sql)
    {
        await hub.ReopenStoreAsync();

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(hub.Root, "hub.db")}");
        await connection.OpenAsync();

        await using var probe = connection.CreateCommand();
        probe.CommandText = "select quote(Id) from Executions limit 1";
        var stored = (string?)await probe.ExecuteScalarAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", executionId.ToString("D").ToUpperInvariant());

        var affected = await command.ExecuteNonQueryAsync();

        Assert.True(affected > 0, $"the rewind matched no rows; stored id looks like {stored}");
    }

    private static PostCommitRecoveryRequired Pending(ExecutionRunReport report, Guid taskId) =>
        new(report.ExecutionId, taskId, report.ResultCommitSha!, ExecutionState.Verifying, "the owning process is gone");

    [Fact]
    public async Task Verification_that_was_already_recorded_is_used_as_it_stands_and_never_run_again()
    {
        var (hub, report) = await RunPassingAsync();

        await using (hub)
        {
            var taskId = hub.TaskId;

            // The crash window where the commands and the verify step were saved - they are
            // saved together - but the execution had not been completed yet.
            await RewindAsync(
                hub,
                report.ExecutionId,
                "update Executions set State = 'Verifying', Outcome = null where Id = $id;"
                + " update Tasks set State = 'Running' where Id in (select TaskId from Executions where Id = $id);");

            var store = hub.OpenStore();

            var service = new PostCommitRecoveryService(hub.OpenStore, new RefusingVerifier(), hub.Clock);
            var recovered = await service.RecoverAsync([Pending(report, taskId)], CancellationToken.None);

            var one = Assert.Single(recovered.Recovered);

            Assert.Equal(PostCommitRecoveryState.Reused, one.State);
            Assert.Equal(ExecutionOutcome.Passed, one.Outcome);
            Assert.Equal(report.ResultCommitSha, one.ResultCommitSha);

            var execution = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);
            var task = await store.FindTaskAsync(taskId, CancellationToken.None);

            Assert.Equal(ExecutionOutcome.Passed, execution!.Outcome);
            Assert.Equal(report.ResultCommitSha, execution.ResultCommitSha);
            Assert.Equal(TaskState.AwaitingAcceptance, task!.State);
        }
    }

    [Fact]
    public async Task A_verify_step_that_claims_an_outcome_with_nothing_behind_it_is_refused()
    {
        var (hub, report) = await RunPassingAsync();

        await using (hub)
        {
            var taskId = hub.TaskId;

            // The record contradicts itself: a finished verify step with no commands under it.
            // That cannot happen - they are written in one transaction - so it means damage,
            // and damage is reported rather than interpreted.
            await RewindAsync(
                hub,
                report.ExecutionId,
                "update Executions set State = 'Verifying', Outcome = null where Id = $id;"
                + " update Tasks set State = 'Running' where Id in (select TaskId from Executions where Id = $id);"
                + " delete from CommandResults where ExecutionStepId in"
                + " (select Id from ExecutionSteps where ExecutionId = $id);");

            var store = hub.OpenStore();

            var service = new PostCommitRecoveryService(hub.OpenStore, new RefusingVerifier(), hub.Clock);
            var recovered = await service.RecoverAsync([Pending(report, taskId)], CancellationToken.None);

            var one = Assert.Single(recovered.Recovered);

            Assert.Equal(PostCommitRecoveryState.FailedClosed, one.State);
            Assert.Null(one.Outcome);
            Assert.Contains("nothing behind the claim", one.Detail, StringComparison.Ordinal);

            // Nothing was written: the execution is exactly as damaged as it was, and the
            // commit it made is untouched.
            var execution = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);

            Assert.Null(execution!.Outcome);
            Assert.Equal(report.ResultCommitSha, execution.ResultCommitSha);
            Assert.False(execution.IsTerminal);
        }
    }

    [Fact]
    public async Task A_verify_step_that_never_finished_is_measured_again_at_the_same_commit()
    {
        var (hub, report) = await RunPassingAsync();

        await using (hub)
        {
            var taskId = hub.TaskId;

            // The other crash window: the commit exists, verification had started, and nothing
            // durable came of it.
            await RewindAsync(
                hub,
                report.ExecutionId,
                "update Executions set State = 'Verifying', Outcome = null where Id = $id;"
                + " update Tasks set State = 'Running' where Id in (select TaskId from Executions where Id = $id);"
                + " delete from CommandResults where ExecutionStepId in"
                + " (select Id from ExecutionSteps where ExecutionId = $id);"
                + " update ExecutionSteps set State = 'Running', StateReason = null where ExecutionId = $id and Kind = 'Verify';");

            var store = hub.OpenStore();

            var service = new PostCommitRecoveryService(
                hub.OpenStore, hub.CreateVerificationGateway(), hub.Clock);

            var recovered = await service.RecoverAsync([Pending(report, taskId)], CancellationToken.None);
            var one = Assert.Single(recovered.Recovered);

            Assert.True(one.State == PostCommitRecoveryState.Reverified, one.Detail);
            Assert.Equal(ExecutionOutcome.Passed, one.Outcome);

            var execution = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);
            var task = await store.FindTaskAsync(taskId, CancellationToken.None);

            // Same commit, measured again - not a new one.
            Assert.Equal(report.ResultCommitSha, execution!.ResultCommitSha);
            Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
            Assert.Equal(TaskState.AwaitingAcceptance, task!.State);

            var commands = await store.ListVerificationCommandsAsync(report.ExecutionId, CancellationToken.None);

            Assert.NotEmpty(commands);
            Assert.All(commands, c => Assert.True(c.IsPass));

            var steps = await store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);

            Assert.Equal(ExecutionStepState.Succeeded, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
            Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Review).State);
        }
    }

    [Fact]
    public async Task A_task_an_operator_already_abandoned_is_left_terminal_while_the_execution_is_finished()
    {
        var (hub, report) = await RunPassingAsync();

        await using (hub)
        {
            var taskId = hub.TaskId;

            // The execution never completed, and in the meantime a person cancelled the task.
            await RewindAsync(
                hub,
                report.ExecutionId,
                "update Executions set State = 'Verifying', Outcome = null where Id = $id;"
                + " update Tasks set State = 'Cancelled' where Id in (select TaskId from Executions where Id = $id);");

            var store = hub.OpenStore();

            var service = new PostCommitRecoveryService(hub.OpenStore, new RefusingVerifier(), hub.Clock);
            var recovered = await service.RecoverAsync([Pending(report, taskId)], CancellationToken.None);

            Assert.Equal(PostCommitRecoveryState.Reused, Assert.Single(recovered.Recovered).State);

            var execution = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);
            var task = await store.FindTaskAsync(taskId, CancellationToken.None);

            // Machine truth is recorded; the operator's decision is not overwritten by it.
            Assert.Equal(ExecutionOutcome.Passed, execution!.Outcome);
            Assert.Equal(TaskState.Cancelled, task!.State);
        }
    }
}
