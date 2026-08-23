using System.Reflection;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// What a recorded verification command is allowed to say, and what nobody may make it say
/// later.
///
/// This row is the only thing a Machine acceptance criterion can be answered from once the
/// run is over, so two properties matter more than the rest: it has to be written by
/// something that actually observed the command, and it has to be impossible to revise
/// afterwards. A verdict that can be edited is not machine truth, it is a second opinion.
/// </summary>
public sealed class VerificationCommandTruthTests
{
    private static CommandResult Command(
        VerificationCommandStatus status,
        bool required = true,
        int? exitCode = 0,
        string stepName = "test") =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            stepName,
            "fixture",
            ["report", "results.trx"],
            status,
            required,
            exitCode,
            durationMilliseconds: 1200,
            testsPassed: 11,
            testsFailed: 0,
            testsSkipped: 0,
            DomainFixtures.Now);

    [Fact]
    public void Only_a_passing_status_is_evidence_that_a_step_passed()
    {
        Assert.True(Command(VerificationCommandStatus.Passed).IsPass);

        foreach (var status in Enum.GetValues<VerificationCommandStatus>()
                     .Where(s => s is not (VerificationCommandStatus.Passed
                         or VerificationCommandStatus.Unavailable)))
        {
            Assert.False(Command(status).IsPass, status.ToString());
        }
    }

    [Fact]
    public void A_zero_exit_code_decides_nothing_on_its_own()
    {
        // The collision that makes the exit code unusable as acceptance truth: a command
        // that ran cleanly and one whose report could not be read look identical by it.
        var passed = Command(VerificationCommandStatus.Passed, exitCode: 0);
        var unreadable = Command(VerificationCommandStatus.ParseFailed, exitCode: 0);

        Assert.Equal(passed.ExitCode, unreadable.ExitCode);
        Assert.True(passed.IsPass);
        Assert.False(unreadable.IsPass);

        // And the reason stays legible rather than collapsing into "not a pass".
        Assert.Equal(VerificationCommandStatus.ParseFailed, unreadable.Status);
    }

    [Fact]
    public void A_command_that_ran_must_say_how_it_ended()
    {
        // Unavailable is what a row written before machine truth was durable reads as. It is
        // never something a run reports about itself.
        var refused = Assert.Throws<DomainException>(() => Command(VerificationCommandStatus.Unavailable));

        Assert.Contains(nameof(VerificationCommandStatus.Unavailable), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_legacy_status_can_never_be_read_as_a_pass()
    {
        // It cannot be constructed, so this asks the rule directly: machine truth nobody
        // captured must not become evidence that something worked.
        Assert.NotEqual(VerificationCommandStatus.Passed, VerificationCommandStatus.Unavailable);
        Assert.Equal(0, (int)VerificationCommandStatus.Unavailable);
    }

    [Fact]
    public void Requiredness_is_recorded_rather_than_looked_up()
    {
        // The profile can be edited between the run and the decision. What this execution
        // was judged by has to travel with the execution.
        Assert.False(Command(VerificationCommandStatus.Failed, required: false).Required);
        Assert.True(Command(VerificationCommandStatus.Failed, required: true).Required);
    }

    [Fact]
    public void Nothing_can_revise_a_recorded_command()
    {
        var type = typeof(CommandResult);

        // No public setter anywhere: what was observed is what stays observed.
        Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => Assert.Null(p.GetSetMethod(nonPublic: false)));

        // And no method offering to change it under another name.
        var mutators = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(mutators);
    }

    [Fact]
    public void A_command_must_name_the_step_it_ran()
    {
        // The name is the join a Machine criterion is resolved through, so a blank one would
        // be a row nothing could ever refer to.
        Assert.Throws<DomainException>(() => Command(VerificationCommandStatus.Passed, stepName: "   "));
    }
}
