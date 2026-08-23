using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The difference between asking work to stop and having stopped it.
///
/// The end to end behaviour is proved by the Loop Zero suite, with two real processes and a
/// real named signal. What is pinned here is the decision the service makes when the answer
/// is not "it stopped": nothing is written, and the operator is told which of the two things
/// happened.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskCancellationServiceTests
{
    /// <summary>A signal whose answers the test decides, standing in for another process.</summary>
    private sealed class FakeSignal(bool listening) : IExecutionCancellationSignal
    {
        public int Requests { get; private set; }

        public IDisposable Listen(Guid executionId, Action onRequested) => new Nothing();

        public bool TryRequest(Guid executionId)
        {
            Requests++;
            return listening;
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static TaskCancellationService Service(
        OrchestrationHarness hub,
        IExecutionCancellationSignal? signal,
        TimeSpan? deadline = null) =>
        new(hub.OpenStore, signal, TimeProvider.System)
        {
            StopDeadline = deadline ?? TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(50),
        };

    [Fact]
    public async Task A_ready_task_is_abandoned_without_anything_being_signalled()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var signal = new FakeSignal(listening: true);

        var result = await Service(hub, signal).CancelAsync(
            "HUB-001", null, "not wanted", CancellationToken.None);

        Assert.Equal(TaskCancellationState.Cancelled, result.State);
        Assert.Equal(TaskState.Cancelled, result.TaskState);
        Assert.Null(result.ExecutionId);
        Assert.Equal(0, signal.Requests);

        var store = hub.OpenStore();
        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);

        Assert.Equal(TaskState.Cancelled, task!.State);
        Assert.Empty(await store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task An_execution_nobody_is_listening_for_cannot_be_stopped_and_nothing_is_written()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        var execution = await RunningExecutionAsync(hub);

        var signal = new FakeSignal(listening: false);

        var result = await Service(hub, signal).CancelAsync("HUB-001", null, null, CancellationToken.None);

        Assert.Equal(TaskCancellationState.Refused, result.State);
        Assert.Equal(1, signal.Requests);
        Assert.Contains("listening", result.Detail, StringComparison.Ordinal);

        // The task is exactly as it was: a terminal task over an execution still recorded as
        // running would be a record that contradicts the machine.
        var store = hub.OpenStore();
        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);
        var unchanged = await store.FindExecutionAsync(execution, CancellationToken.None);

        Assert.NotEqual(TaskState.Cancelled, task!.State);
        Assert.False(unchanged!.IsTerminal);
    }

    [Fact]
    public async Task A_run_that_was_asked_to_stop_and_has_not_is_reported_as_requested_not_as_cancelled()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        var execution = await RunningExecutionAsync(hub);

        // The request is delivered and nothing stops, which is the case the operator most
        // needs told apart from success.
        var result = await Service(hub, new FakeSignal(listening: true))
            .CancelAsync("HUB-001", null, null, CancellationToken.None);

        Assert.Equal(TaskCancellationState.CancellationRequested, result.State);
        Assert.Equal(execution, result.ExecutionId);
        Assert.Contains("asked to stop", result.Detail, StringComparison.Ordinal);

        var store = hub.OpenStore();
        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);

        Assert.NotEqual(TaskState.Cancelled, task!.State);
        Assert.False((await store.FindExecutionAsync(execution, CancellationToken.None))!.IsTerminal);
    }

    [Fact]
    public async Task Without_a_way_to_signal_at_all_a_running_task_is_refused_rather_than_flipped()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        var execution = await RunningExecutionAsync(hub);

        var result = await Service(hub, signal: null).CancelAsync("HUB-001", null, null, CancellationToken.None);

        Assert.Equal(TaskCancellationState.Refused, result.State);

        var store = hub.OpenStore();
        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);

        Assert.NotEqual(TaskState.Cancelled, task!.State);
        Assert.False((await store.FindExecutionAsync(execution, CancellationToken.None))!.IsTerminal);
    }

    /// <summary>
    /// A real execution, wound back to the moment it was still running.
    ///
    /// Running one for real and leaving it running would mean a live agent process for the
    /// length of the test; winding a finished one back gives the same record deterministically.
    /// The SQL is deliberate: the aggregate has no method that would undo a completion.
    /// </summary>
    private static async Task<Guid> RunningExecutionAsync(OrchestrationHarness hub)
    {
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        await hub.ReopenStoreAsync();

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(hub.Root, "hub.db")}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "update Executions set State = 'RunningAgent', Outcome = null, ResultCommitSha = null where Id = $id;"
            + " update Tasks set State = 'Running' where Id in (select TaskId from Executions where Id = $id);";

        command.Parameters.AddWithValue("$id", report.ExecutionId.ToString("D").ToUpperInvariant());

        Assert.True(await command.ExecuteNonQueryAsync() > 0, "the rewind matched no rows");

        return report.ExecutionId;
    }
}
