using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Phase 2/3 introduced a CoreX.Hub.Execution namespace, so the domain type of the
// same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Whether a Machine acceptance criterion can still be answered once the process that ran
/// the verification is gone.
///
/// That is the only situation that matters: a run happens, the machine exits, and some time
/// later a person decides. Everything the decision needs therefore has to be in SQLite, in a
/// form nobody has to interpret - not in a result object, not in a sentence written for a
/// human, and not inferred from an exit code.
///
/// Every test here closes the database between writing and reading, so nothing in memory can
/// make it pass.
/// </summary>
public sealed class VerificationTruthPersistenceTests
{
    [Fact]
    public async Task A_step_name_still_resolves_to_its_own_status_after_the_database_is_reopened()
    {
        using var hub = TempHub.Create();

        var executionId = await WriteAsync(
            hub,
            Command(1, "build", VerificationCommandStatus.Passed, required: true, exitCode: 0),
            Command(2, "test", VerificationCommandStatus.Passed, required: true, exitCode: 0, passed: 11, failed: 0),
            Command(3, "lint", VerificationCommandStatus.Failed, required: false, exitCode: 1));

        // Everything the writing process knew is gone from here on.
        await using var reopened = hub.NewContext();
        var reader = new VerificationTruthReader(new EfHubStore(reopened));

        var test = await reader.ResolveAsync(executionId, "test", CancellationToken.None);
        var lint = await reader.ResolveAsync(executionId, "lint", CancellationToken.None);

        Assert.True(test.Found);
        Assert.True(test.Command!.IsPass);
        Assert.Equal(VerificationCommandStatus.Passed, test.Command.Status);
        Assert.Equal(11, test.Command.TestsPassed);

        Assert.True(lint.Found);
        Assert.False(lint.Command!.IsPass);
        Assert.Equal(VerificationCommandStatus.Failed, lint.Command.Status);

        // A name nobody recorded is an absence, not a failure to be guessed at.
        var missing = await reader.ResolveAsync(executionId, "typecheck", CancellationToken.None);

        Assert.False(missing.Found);
        Assert.Equal(MachineTruthProblem.NotRecorded, missing.Problem);
        Assert.Contains("typecheck", missing.Detail, StringComparison.Ordinal);

        // So is a criterion that names nothing at all.
        var unnamed = await reader.ResolveAsync(executionId, "   ", CancellationToken.None);

        Assert.False(unnamed.Found);
        Assert.Equal(MachineTruthProblem.NotRecorded, unnamed.Problem);
    }

    [Fact]
    public async Task Passed_and_ParseFailed_stay_apart_although_both_exited_zero()
    {
        // The collision the exit code cannot survive. A criterion answered from ExitCode == 0
        // would call ParseFailed a pass - the Hub ran the command, could not read what it
        // produced, and would report the work verified.
        using var hub = TempHub.Create();

        var executionId = await WriteAsync(
            hub,
            Command(1, "test", VerificationCommandStatus.Passed, required: true, exitCode: 0),
            Command(2, "contract", VerificationCommandStatus.ParseFailed, required: true, exitCode: 0));

        await using var reopened = hub.NewContext();
        var reader = new VerificationTruthReader(new EfHubStore(reopened));

        var passed = await reader.ResolveAsync(executionId, "test", CancellationToken.None);
        var unreadable = await reader.ResolveAsync(executionId, "contract", CancellationToken.None);

        Assert.Equal(0, passed.Command!.ExitCode);
        Assert.Equal(0, unreadable.Command!.ExitCode);
        Assert.Equal(passed.Command.ExitCode, unreadable.Command.ExitCode);

        Assert.True(passed.Command.IsPass);
        Assert.False(unreadable.Command.IsPass);

        // And the reason survives too, so a person is told what happened rather than only
        // that something did not pass.
        Assert.Equal(VerificationCommandStatus.ParseFailed, unreadable.Command.Status);
    }

    [Fact]
    public async Task A_step_that_was_optional_when_it_ran_stays_optional_afterwards()
    {
        // The profile is configuration and configuration moves. An execution has to keep
        // being read by the rules that were in force when it happened, or a decision about
        // last week's run is made under this week's policy without anyone saying so.
        using var hub = TempHub.Create();

        var executionId = await WriteAsync(
            hub,
            Command(1, "test", VerificationCommandStatus.Passed, required: true, exitCode: 0),
            Command(2, "lint", VerificationCommandStatus.Failed, required: false, exitCode: 1));

        await using var reopened = hub.NewContext();
        var reader = new VerificationTruthReader(new EfHubStore(reopened));

        var lint = await reader.ResolveAsync(executionId, "lint", CancellationToken.None);
        var test = await reader.ResolveAsync(executionId, "test", CancellationToken.None);

        Assert.False(lint.Command!.Required);
        Assert.True(test.Command!.Required);

        // Requiredness came off the row. No verification profile was consulted to learn it,
        // and none needed to exist in this process at all.
        Assert.False(lint.Command.IsPass);
        Assert.True(test.Command.IsPass);
    }

    [Fact]
    public async Task One_step_name_can_only_mean_one_thing_within_an_execution()
    {
        // Two rows claiming the same name would not be a tie to break at read time - they
        // would be machine truth contradicting itself. The database refuses them where they
        // would be written.
        using var hub = TempHub.Create();

        await using var db = await hub.NewMigratedContextAsync();
        var (execution, verifyStep) = await SeedExecutionAsync(hub, db);

        db.CommandResults.Add(CommandFor(
            execution, verifyStep, Command(1, "test", VerificationCommandStatus.Passed, true, 0)));
        await db.SaveChangesAsync();

        db.CommandResults.Add(CommandFor(
            execution, verifyStep, Command(2, "test", VerificationCommandStatus.Failed, true, 1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_command_must_say_how_it_ended()
    {
        // Unavailable exists for rows written before machine truth was durable. A run that
        // actually happened knows how it went, so writing that status is refused.
        using var hub = TempHub.Create();

        await using var db = await hub.NewMigratedContextAsync();
        var (execution, verifyStep) = await SeedExecutionAsync(hub, db);

        Assert.Throws<Domain.Common.DomainException>(() => CommandFor(
            execution,
            verifyStep,
            Command(1, "test", VerificationCommandStatus.Unavailable, required: true, exitCode: 0)));
    }

    /// <summary>What one verification step reported, for a test to write and read back.</summary>
    private sealed record Recorded(
        int Order,
        string StepName,
        VerificationCommandStatus Status,
        bool Required,
        int? ExitCode,
        int? Passed,
        int? Failed);

    private static Recorded Command(
        int order,
        string stepName,
        VerificationCommandStatus status,
        bool required,
        int? exitCode,
        int? passed = null,
        int? failed = null) =>
        new(order, stepName, status, required, exitCode, passed, failed);

    /// <summary>
    /// Writes an execution and its verification commands, then closes the database
    /// completely. What comes back is only an id: the caller has to reopen to learn anything.
    /// </summary>
    private static async Task<Guid> WriteAsync(TempHub hub, params Recorded[] commands)
    {
        var db = await hub.NewMigratedContextAsync();

        try
        {
            var (execution, verifyStep) = await SeedExecutionAsync(hub, db);

            foreach (var command in commands)
            {
                db.CommandResults.Add(CommandFor(execution, verifyStep, command));
            }

            await db.SaveChangesAsync();
            return execution.Id;
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    private static async Task<(DomainExecution Execution, ExecutionStep VerifyStep)> SeedExecutionAsync(
        TempHub hub,
        HubDbContext db)
    {
        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "machine truth", "--allow", "src/**", "--human-criterion", "looks right");

        Assert.Equal(0, created.ExitCode);

        var task = await db.Tasks.FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            task.OrganizationId,
            task.Id,
            attemptNo: 1,
            baseCommitSha: new string('a', 40),
            branchName: "agent/HUB-001/1",
            worktreePath: @"D:\cx\_fixtures\hub-dev\hub-sample\HUB-001-1",
            previousExecutionId: null,
            now);

        var verifyStep = new ExecutionStep(
            Guid.CreateVersion7(), task.OrganizationId, execution.Id, 5, ExecutionStepKind.Verify, now);

        db.Executions.Add(execution);
        db.ExecutionSteps.Add(verifyStep);
        await db.SaveChangesAsync();

        return (execution, verifyStep);
    }

    private static CommandResult CommandFor(
        DomainExecution execution,
        ExecutionStep verifyStep,
        Recorded command) =>
        new(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            verifyStep.Id,
            command.Order,
            command.StepName,
            "fixture",
            ["report", command.StepName],
            command.Status,
            command.Required,
            command.ExitCode,
            durationMilliseconds: 1200,
            command.Passed,
            command.Failed,
            testsSkipped: null,
            DateTimeOffset.UtcNow);
}
