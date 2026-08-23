using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Whether a real run leaves its verification truth behind.
///
/// The persistence tests prove that rows written by hand survive; these prove the rows get
/// written at all. That distinction is the whole failure being fixed: the pipeline already
/// computed the name, requiredness and status of every verification step and then dropped
/// them on the floor when the process ended, so a decision made later had nothing to read.
///
/// Everything here runs the real thing - real git, real processes, real verification - and
/// then closes the database before looking.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationTruthWiringTests
{
    [Fact]
    public async Task A_real_run_records_what_each_verification_step_did()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        var reader = new VerificationTruthReader(await hub.ReopenStoreAsync());

        var command = Assert.Single(await reader.ListAsync(report.ExecutionId, CancellationToken.None));

        Assert.Equal("test", command.StepName);
        Assert.Equal(VerificationCommandStatus.Passed, command.Status);
        Assert.True(command.Required);
        Assert.True(command.IsPass);
        Assert.Equal(0, command.ExitCode);
        Assert.NotNull(command.TestsPassed);

        // What the command actually was, not a sentence about it.
        Assert.NotEmpty(command.Executable);
        Assert.Contains("report", command.Args, StringComparer.Ordinal);

        var resolved = await reader.ResolveAsync(report.ExecutionId, "test", CancellationToken.None);

        Assert.True(resolved.Found);
        Assert.Equal(command.Id, resolved.Command!.Id);
    }

    [Fact]
    public async Task The_baseline_run_never_appears_among_the_execution_commands()
    {
        // Both runs use the same profile and therefore the same step names. Only the run
        // measured in the execution's own worktree is that execution's machine truth; the
        // baseline belongs to a commit, is measured in a disposable tree, and would be a
        // second contradictory answer to the same criterion if it leaked in here.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        // A baseline really was measured, so the separation is being tested rather than
        // trivially satisfied.
        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "Baseline");

        var reader = new VerificationTruthReader(await hub.ReopenStoreAsync());
        var commands = await reader.ListAsync(report.ExecutionId, CancellationToken.None);

        Assert.Single(commands);
        Assert.Single(commands, c => c.StepName == "test");
    }

    [Fact]
    public async Task An_optional_step_that_failed_is_recorded_as_optional_and_failed()
    {
        // The case that separates "the execution passed" from "every criterion is met". The
        // execution passes, because only required steps decide that. The criterion pointing
        // at the optional step does not, and a person deciding acceptance has to be able to
        // see both.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;
        hub.VerificationProfile = TestAndLintProfile(hub);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        var reader = new VerificationTruthReader(await hub.ReopenStoreAsync());

        var test = await reader.ResolveAsync(report.ExecutionId, "test", CancellationToken.None);
        var lint = await reader.ResolveAsync(report.ExecutionId, "lint", CancellationToken.None);

        Assert.True(test.Found);
        Assert.True(test.Command!.Required);
        Assert.True(test.Command.IsPass);

        Assert.True(lint.Found);
        Assert.False(lint.Command!.Required);
        Assert.False(lint.Command.IsPass);
        Assert.Equal(VerificationCommandStatus.Failed, lint.Command.Status);

        // The execution passed with a failed step inside it, and both facts are readable
        // from the database alone.
        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
    }

    /// <summary>A required step that passes and an optional one that does not.</summary>
    private static VerificationProfile TestAndLintProfile(OrchestrationHarness hub) =>
        new(
            Guid.CreateVersion7(),
            hub.OrganizationId,
            hub.RepositoryId,
            "fixture-verification",
            true,
            ".",
            [
                new VerificationStep(
                    "test",
                    "fixture",
                    ["report", "results/test.trx", "trx-fixed", "0"],
                    120,
                    required: true,
                    VerificationResultParser.Trx,
                    "results/test.trx"),
                new VerificationStep(
                    "lint",
                    "fixture",
                    ["report", "results/lint.trx", "trx-broken", "1"],
                    120,
                    required: false,
                    VerificationResultParser.Trx,
                    "results/lint.trx"),
            ],
            hub.Clock.GetUtcNow());
}
