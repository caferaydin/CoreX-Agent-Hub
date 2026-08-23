using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Phase 2/3 introduced a CoreX.Hub.Execution namespace, so the domain type of the
// same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A verification row from before machine truth was durable.
///
/// The aggregate refuses to write one, so the only way it can exist is on disk - which is
/// exactly the situation worth testing, because that is how it would actually turn up. What
/// matters is that it reads as "nobody recorded this" rather than as "this failed": the
/// first stops a decision, the second would quietly let one proceed on evidence nobody has.
/// </summary>
public sealed class LegacyMachineTruthTests
{
    [Fact]
    public async Task A_row_with_no_recorded_status_stops_the_decision()
    {
        using var hub = TempHub.Create();
        var seeded = await SeedAsync(hub);

        // Written straight to the table, because the domain will not construct it.
        await using (var writer = hub.NewContext())
        {
            await writer.Database.ExecuteSqlRawAsync(
                """
                UPDATE "CommandResults" SET "Status" = 'Unavailable' WHERE "StepName" = 'test';
                """);
        }

        await using var db = hub.NewContext();
        var store = new EfHubStore(db);
        var service = new AcceptanceService(store, new VerificationTruthReader(store));

        var command = Assert.Single(
            await store.ListVerificationCommandsAsync(seeded.ExecutionId, CancellationToken.None));

        Assert.Equal(VerificationCommandStatus.Unavailable, command.Status);
        Assert.False(command.IsPass);

        var preview = await service.PreviewAsync("HUB-001", 1, null, CancellationToken.None);

        // Not a criterion that failed - a criterion that cannot be answered.
        Assert.Empty(preview.MachineCriteria);
        Assert.Single(preview.MachineTruthProblems);
        Assert.Contains("no status was recorded", preview.MachineTruthProblems[0], StringComparison.Ordinal);

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() => service.AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                HumanVerdicts = [new HumanCriterionVerdictRecord(seeded.HumanCriterionId, HumanCriterionVerdict.Met, null)],
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        Assert.Contains("Machine truth could not be established", refused.Message, StringComparison.Ordinal);

        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(seeded.ExecutionId, CancellationToken.None));
        Assert.Equal(TaskState.AwaitingAcceptance, (await db.Tasks.FirstAsync()).State);
    }

    private sealed record Seeded(Guid ExecutionId, Guid HumanCriterionId);

    private static async Task<Seeded> SeedAsync(TempHub hub)
    {
        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "legacy truth", "--allow", "src/**",
            "--machine-criterion", "test=The suite passes.",
            "--human-criterion", "The wording reads well.");

        Assert.Equal(0, created.ExitCode);
        Assert.Equal(0, (await hub.RunAsync("task", "ready", "HUB-001", "--org", "hub-dev")).ExitCode);

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

        // Written legitimately first; the raw update below is what ages it.
        db.CommandResults.Add(new CommandResult(
            Guid.CreateVersion7(), task.OrganizationId, verify.Id, 1, "test", "fixture",
            ["report", "results.trx"], VerificationCommandStatus.Passed, required: true,
            exitCode: 0, durationMilliseconds: 900, testsPassed: 11, testsFailed: 0, testsSkipped: 0, now));

        await db.SaveChangesAsync();

        // Read from the table rather than the navigation: this context loaded the task
        // without its criteria, and the point here is the row, not the aggregate.
        var human = await db.AcceptanceCriteria.SingleAsync(c => c.Kind == VerificationKind.Human);

        return new Seeded(execution.Id, human.Id);
    }
}
