using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Cli;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

// Phase 2/3 introduced a CoreX.Hub.Execution namespace, so the domain type of the
// same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The human decision commands, driven exactly as the shipped executable drives them.
///
/// The checklist is answered without a terminal, which is the point of having an input port
/// at all: a decision that could only be made by hand is a decision nothing can test.
/// </summary>
public sealed class AcceptanceCliTests
{
    [Fact]
    public async Task Accept_asks_for_each_human_verdict_and_records_the_decision()
    {
        using var hub = TempHub.Create();
        var seeded = await SeedPassingExecutionAsync(hub);

        var (exitCode, output) = await RunAcceptAsync(hub, ["HUB-001#1"], ["met:read it twice"]);

        Assert.Equal(ExitCodes.Success, exitCode);

        // Machine truth is shown, and shown as read-only context.
        Assert.Contains("machine criteria (read-only", output, StringComparison.Ordinal);
        Assert.Contains("Met", output, StringComparison.Ordinal);
        Assert.Contains("Accept", output, StringComparison.Ordinal);
        Assert.Contains("unchanged", output, StringComparison.Ordinal);

        await using var db = hub.NewContext();

        var task = await db.Tasks.FirstAsync();
        Assert.Equal(TaskState.Accepted, task.State);

        var decision = await db.AcceptanceDecisions.FirstAsync();
        Assert.Equal(AcceptanceDecisionKind.Accept, decision.Decision);
        Assert.False(decision.OverrideUsed);
        Assert.Equal(seeded.ExecutionId, decision.ExecutionId);

        var execution = await db.Executions.FirstAsync();
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
    }

    [Fact]
    public async Task Verdicts_can_be_supplied_on_the_command_line()
    {
        using var hub = TempHub.Create();
        await SeedPassingExecutionAsync(hub);

        // Nothing is typed at all: the port is never asked for a line.
        var (exitCode, _) = await RunAcceptAsync(
            hub, ["HUB-001#1", "--verdict", "2=notmet:the heading is wrong"], []);

        Assert.Equal(ExitCodes.Success, exitCode);

        await using var db = hub.NewContext();
        var decision = await db.AcceptanceDecisions.FirstAsync();

        var verdict = Assert.Single(decision.HumanCriterionVerdicts);
        Assert.Equal(HumanCriterionVerdict.NotMet, verdict.Verdict);
        Assert.Equal("the heading is wrong", verdict.Note);
    }

    [Fact]
    public async Task A_decision_names_the_attempt_it_is_about()
    {
        using var hub = TempHub.Create();
        await SeedPassingExecutionAsync(hub);

        var bare = await hub.RunAsync("reject", "HUB-001");

        Assert.Equal(ExitCodes.Usage, bare.ExitCode);
        Assert.Contains("does not name an execution", bare.All, StringComparison.Ordinal);

        await using var db = hub.NewContext();
        Assert.Equal(0, await db.AcceptanceDecisions.CountAsync());
    }

    [Fact]
    public async Task Request_changes_returns_the_task_to_the_queue()
    {
        using var hub = TempHub.Create();
        await SeedPassingExecutionAsync(hub);

        var result = await hub.RunAsync("request-changes", "HUB-001#1", "--reason", "the empty state is wrong");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("RequestChanges", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Ready", result.StdOut, StringComparison.Ordinal);

        await using var db = hub.NewContext();
        Assert.Equal(TaskState.Ready, (await db.Tasks.FirstAsync()).State);
        Assert.Equal(
            AcceptanceDecisionKind.RequestChanges,
            (await db.AcceptanceDecisions.FirstAsync()).Decision);
    }

    [Fact]
    public async Task Reject_ends_the_task()
    {
        using var hub = TempHub.Create();
        await SeedPassingExecutionAsync(hub);

        var result = await hub.RunAsync("reject", "HUB-001#1", "--reason", "not doing this");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Reject", result.StdOut, StringComparison.Ordinal);

        await using var db = hub.NewContext();
        Assert.Equal(TaskState.Rejected, (await db.Tasks.FirstAsync()).State);
    }

    [Fact]
    public async Task A_second_decision_on_the_same_attempt_is_refused()
    {
        using var hub = TempHub.Create();
        await SeedPassingExecutionAsync(hub);

        Assert.Equal(ExitCodes.Success, (await hub.RunAsync("reject", "HUB-001#1", "--reason", "no")).ExitCode);

        var again = await hub.RunAsync("request-changes", "HUB-001#1");

        Assert.Equal(ExitCodes.OperationFailed, again.ExitCode);
        Assert.Contains("never revised", again.All, StringComparison.Ordinal);

        await using var db = hub.NewContext();
        Assert.Equal(1, await db.AcceptanceDecisions.CountAsync());
    }

    [Fact]
    public async Task There_is_no_flag_that_states_a_machine_result()
    {
        // I-A6 at the command line. Declaring a machine criterion when a task is written is
        // fine - that says which step decides it. What must not exist is any way to say what
        // that step did.
        foreach (var stating in new[]
                 {
                     "--machine-verdict", "--machine-result", "--force-verification",
                     "--mark-", "--set-verification", "--verification-status",
                 })
        {
            Assert.DoesNotContain(stating, HubCli.UsageText, StringComparison.OrdinalIgnoreCase);
        }

        // Declaring one still is.
        Assert.Contains("--machine-criterion <ref>=<text>", HubCli.UsageText, StringComparison.Ordinal);

        Assert.Contains("--verdict", HubCli.UsageText, StringComparison.Ordinal);
        Assert.Contains("cannot be answered by hand", HubCli.UsageText, StringComparison.Ordinal);
    }

    /// <summary>Drives the accept command with a scripted answer for each prompt.</summary>
    private static async Task<(int ExitCode, string Output)> RunAcceptAsync(
        TempHub hub,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> answers)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var argv = new List<string> { "accept" };
        argv.AddRange(arguments);

        var args = ArgParser.Parse(argv);

        await using var session = await HubSession.OpenAsync(
            hub.DataDirectory, hub.ConfigPath, stderr, CancellationToken.None);

        try
        {
            var exitCode = await Cli.Commands.AcceptanceCommands.AcceptAsync(
                session, args, stdout, new ScriptedConsoleInput(answers), CancellationToken.None);

            return (exitCode, stdout.ToString() + stderr.ToString());
        }
        catch (AcceptanceException ex)
        {
            return (ExitCodes.OperationFailed, stdout + stderr.ToString() + ex.Message);
        }
    }

    private sealed record Seeded(Guid TaskId, Guid ExecutionId);

    /// <summary>
    /// A task with one machine and one human criterion, and a passing execution with the
    /// verification truth that execution really recorded.
    /// </summary>
    private static async Task<Seeded> SeedPassingExecutionAsync(TempHub hub)
    {
        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "acceptance", "--allow", "src/**",
            "--machine-criterion", "test=The suite passes.",
            "--human-criterion", "The wording reads well.");

        Assert.Equal(0, created.ExitCode);

        var ready = await hub.RunAsync("task", "ready", "HUB-001", "--org", "hub-dev");
        Assert.Equal(0, ready.ExitCode);

        await using var db = hub.NewContext();
        var task = await db.Tasks.FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var execution = new DomainExecution(
            Guid.CreateVersion7(), task.OrganizationId, task.Id, 1, new string('a', 40),
            "agent/HUB-001/1", @"D:\cx\_fixtures\hub-dev\hub-sample\HUB-001-1", null, now);

        var verify = new ExecutionStep(
            Guid.CreateVersion7(), task.OrganizationId, execution.Id, 5, ExecutionStepKind.Verify, now);

        verify.Succeed(now);

        execution.RecordResultCommit(new string('c', 40), now);
        execution.Complete(ExecutionOutcome.Passed, now);

        task.MarkRunning(now);
        task.MarkAwaitingAcceptance(now);

        db.Executions.Add(execution);
        db.ExecutionSteps.Add(verify);

        db.CommandResults.Add(new CommandResult(
            Guid.CreateVersion7(), task.OrganizationId, verify.Id, 1, "test", "fixture",
            ["report", "results.trx"], VerificationCommandStatus.Passed, required: true,
            exitCode: 0, durationMilliseconds: 900, testsPassed: 11, testsFailed: 0, testsSkipped: 0, now));

        await db.SaveChangesAsync();

        return new Seeded(task.Id, execution.Id);
    }
}
