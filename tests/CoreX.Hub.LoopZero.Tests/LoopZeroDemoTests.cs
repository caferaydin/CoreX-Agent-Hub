using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// Loop Zero criteria 1, 2 and 3: the canonical demo, and the two rules that stop it being a
/// rubber stamp.
///
/// Every command is a separate operating system process, and nothing is carried between them
/// except what was written to SQLite and to the evidence store. That is the point: if the
/// answer to "did this pass" lived in memory, the second process could not ask it.
/// </summary>
public sealed class LoopZeroDemoTests
{
    [Fact]
    public async Task Criterion01_a_run_stops_at_awaiting_acceptance_and_a_later_process_accepts_it()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        var run = await hub.ExecuteAsync("run", "HUB-001");

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Contains("AwaitingAcceptance", run.Stdout, StringComparison.Ordinal);

        // Nothing accepted itself.
        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        Assert.Equal(TaskState.AwaitingAcceptance, task.State);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));

        // A different process reads the evidence...
        var evidence = await hub.ExecuteAsync("evidence", "show", "HUB-001#1", "--diff");
        Assert.Equal(0, evidence.ExitCode);
        Assert.Contains(execution.ResultCommitSha![..12], evidence.Stdout, StringComparison.Ordinal);

        // ...and a third records the decision.
        var accept = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met", "--actor", "loop-zero");
        Assert.Equal(0, accept.ExitCode);

        var after = hub.OpenStore();
        var decided = Assert.Single(await after.FindTasksByKeyAsync("HUB-001", CancellationToken.None));

        Assert.Equal(TaskState.Accepted, decided.State);
        Assert.Equal(1, await after.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));

        // The execution keeps what it earned; acceptance is a separate record.
        var final = await after.FindExecutionAsync(execution.Id, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Passed, final!.Outcome);
        Assert.Equal(execution.ResultCommitSha, final.ResultCommitSha);
    }

    [Fact]
    public async Task Criterion02_a_second_decision_is_refused_and_so_is_a_decision_on_the_wrong_state()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        // Before anything ran there is nothing to decide on.
        var early = await hub.CliAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.NotEqual(0, early.ExitCode);

        await hub.ExecuteAsync("run", "HUB-001");

        var first = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.Equal(0, first.ExitCode);

        var second = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.NotEqual(0, second.ExitCode);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        Assert.Equal(1, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
        Assert.Equal(TaskState.Accepted, task.State);
    }

    [Fact]
    public async Task Criterion03_machine_criteria_come_from_evidence_and_the_human_one_must_be_answered()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();
        await hub.ExecuteAsync("run", "HUB-001");

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));

        Assert.Equal(2, task.AcceptanceCriteria.Count(c => c.Kind == VerificationKind.Machine));
        Assert.Equal(1, task.AcceptanceCriteria.Count(c => c.Kind == VerificationKind.Human));

        // Without the human answer, accept refuses: it is not a question the Hub may answer.
        var unanswered = await hub.ExecuteAsync("accept", "HUB-001#1");
        Assert.NotEqual(0, unanswered.ExitCode);

        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));
        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));

        // The machine answers were never asked for; they were read from the recorded commands.
        var commands = await store.ListVerificationCommandsAsync(execution.Id, CancellationToken.None);
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.True(c.IsPass));

        var answered = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.Equal(0, answered.ExitCode);
    }
}
