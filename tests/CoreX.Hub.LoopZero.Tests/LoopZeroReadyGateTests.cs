using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// Loop Zero criterion 4: a task cannot become runnable while anybody could still disagree
/// about how it will be judged.
///
/// The rule belongs to the ready gate and is exercised here through the shipped command, so
/// what is proved is that the command line really is behind that gate rather than beside it.
/// </summary>
public sealed class LoopZeroReadyGateTests
{
    [Fact]
    public async Task Criterion04_a_criterion_that_does_not_say_how_it_is_verified_keeps_the_task_in_draft()
    {
        using var hub = LoopZeroHub.Create();

        var created = await hub.CliAsync(
            "task", "create",
            "--org", "hub-dev",
            "--project", "hub-sample",
            "--repo", "hub-sample-repo",
            "--key", "HUB-004",
            "--objective", "Declare nothing about how this is checked.",
            "--allow", "src/**",
            "--undeclared-criterion", "it should feel right");

        Assert.True(created.ExitCode == 0, created.Describe());

        var ready = await hub.CliAsync("task", "ready", "HUB-004");

        Assert.Equal(3, ready.ExitCode);
        Assert.Contains("NOT SATISFIED", ready.Stdout, StringComparison.Ordinal);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-004", CancellationToken.None));

        Assert.Equal(TaskState.Draft, task.State);

        // And nothing can be run from Draft.
        var run = await hub.ExecuteAsync("run", "HUB-004");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Criterion04_a_machine_criterion_naming_a_step_that_does_not_exist_keeps_the_task_in_draft()
    {
        using var hub = LoopZeroHub.Create();

        await hub.SeedTaskAsync("HUB-005", machineRefs: ["nosuchstep"]);

        var ready = await hub.CliAsync("task", "ready", "HUB-005");

        Assert.Equal(3, ready.ExitCode);
        Assert.Contains("nosuchstep", ready.Stdout, StringComparison.Ordinal);
        Assert.Contains("does not resolve", ready.Stdout, StringComparison.Ordinal);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-005", CancellationToken.None));

        Assert.Equal(TaskState.Draft, task.State);

        // The same reference is fine once it names a step the profile really declares.
        await hub.SeedTaskAsync("HUB-006", machineRefs: ["test"]);

        var accepted = await hub.CliAsync("task", "ready", "HUB-006");

        Assert.True(accepted.ExitCode == 0, accepted.Describe());
    }
}
