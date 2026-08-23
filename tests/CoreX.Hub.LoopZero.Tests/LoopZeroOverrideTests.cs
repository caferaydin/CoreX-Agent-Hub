using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// Loop Zero criterion 17: the override is for one thing only, and this is everything it is
/// not for.
///
/// A review override exists so a person can accept work a blocking review objected to. It is
/// not a way past a gate, a crash, a timeout or a failed suite - and a passing run does not
/// need it, so asking for it there is refused as well rather than quietly treated as an
/// ordinary acceptance.
/// </summary>
public sealed class LoopZeroOverrideTests
{
    [Theory]
    [InlineData("out-of-scope-write", "ScopeViolation")]
    [InlineData("secret-write", "SecretGuardBlocked")]
    [InlineData("unexpected-commit", "RuntimePolicyViolation")]
    [InlineData("partial-write", "Failed")]
    [InlineData("apply-patch", "Passed")]
    public async Task Criterion17_an_override_is_refused_and_leaves_the_task_exactly_as_it_was(
        string agentMode,
        string expected)
    {
        using var hub = LoopZeroHub.Create(agentMode: agentMode);
        await hub.SeedReadyTaskAsync();
        await hub.ExecuteAsync("run", "HUB-001");

        await AssertOverrideRefusedAsync(hub, expected);
    }

    [Fact]
    public async Task Criterion17_an_override_is_refused_for_a_run_that_ran_out_of_time()
    {
        using var hub = LoopZeroHub.Create(agentMode: "hang", timeouts: AgentTimeouts.Impatient);
        await hub.SeedReadyTaskAsync();
        await hub.ExecuteAsync("run", "HUB-001");

        await AssertOverrideRefusedAsync(hub, "TimedOut");
    }

    private static async Task AssertOverrideRefusedAsync(LoopZeroHub hub, string expected)
    {
        var before = hub.OpenStore();
        var task = Assert.Single(await before.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await before.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        var reached = execution.Outcome?.ToString() ?? execution.State.ToString();
        Assert.Equal(expected, reached);

        var stateBefore = task.State;

        var overridden = await hub.ExecuteAsync(
            "accept", "HUB-001#1", "--override", "--reason", "the reviewer was wrong");

        Assert.NotEqual(0, overridden.ExitCode);

        var after = hub.OpenStore();
        var unchanged = Assert.Single(await after.FindTasksByKeyAsync("HUB-001", CancellationToken.None));

        Assert.Equal(stateBefore, unchanged.State);
        Assert.NotEqual(TaskState.Accepted, unchanged.State);
        Assert.Equal(0, await after.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
    }
}
