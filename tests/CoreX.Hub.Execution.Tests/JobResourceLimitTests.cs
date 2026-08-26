using System.Globalization;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Resource ceilings enforced by Windows, observed on real processes.
///
/// The mapping tests next door prove the right bits reach the right fields. They cannot prove
/// that Windows then does anything about it, and a quota nobody can demonstrate is a comment.
/// So these run actual children: a fixture that tries to spawn more processes than the job
/// allows, and one that tries to commit more memory than the job allows, both reporting from
/// the inside what the operating system let them do.
///
/// Everything here is deliberately small. The point is to observe the semantics, not to put
/// the workstation under pressure - the memory ceiling is a couple of hundred megabytes and
/// the fixture stops itself well before that would matter even if no limit were applied.
/// </summary>
public sealed class JobResourceLimitTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    // --- Process count ---------------------------------------------------------------------

    [Fact]
    public async Task The_root_process_itself_counts_towards_the_process_ceiling()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("count-one.txt");
        File.WriteAllText(report, string.Empty);

        // A ceiling of one. Job Object membership includes the process the Hub launched, so
        // this should be a run that may exist and may not spawn - rather than a run with one
        // helper. Measured here instead of assumed from the documentation.
        var request = Fixture.Request(workspace, Deadline, "spawn-probe", report, "1") with
        {
            ResourceLimits = new ProcessResourceLimits { ActiveProcessLimit = 1 },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);

        var lines = File.ReadAllText(report);

        // The root ran - so the ceiling did not stop the run itself.
        Assert.Contains("PARENT=", lines, StringComparison.Ordinal);

        // And it could not add a second member.
        Assert.Contains("SPAWN-FAILED", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("SPAWN-OK", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_ceiling_bounds_how_far_a_tree_can_grow()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("count-two.txt");
        File.WriteAllText(report, string.Empty);

        // Root plus one child fits; the rest do not.
        var request = Fixture.Request(workspace, Deadline, "spawn-probe", report, "4") with
        {
            ResourceLimits = new ProcessResourceLimits { ActiveProcessLimit = 2 },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);

        var lines = File.ReadAllLines(report);

        Assert.Single(lines, l => l.StartsWith("SPAWN-OK=", StringComparison.Ordinal));
        Assert.Equal(3, lines.Count(l => l.StartsWith("SPAWN-FAILED=", StringComparison.Ordinal)));

        // Whatever the job did allow is gone with the job.
        await AssertNoSurvivorsAsync(report);
    }

    // --- Job memory ------------------------------------------------------------------------

    [Fact]
    public async Task A_job_memory_ceiling_stops_a_process_growing_past_it()
    {
        using var workspace = TempWorkspace.Create();

        // Small on purpose. Large enough that a .NET process can start inside it, far smaller
        // than what the fixture would otherwise take.
        const int LimitMiB = 192;
        const int AttemptMiB = 512;

        var request = Fixture.Request(
            workspace,
            Deadline,
            "grow",
            "8",
            AttemptMiB.ToString(CultureInfo.InvariantCulture)) with
        {
            ResourceLimits = new ProcessResourceLimits
            {
                JobMemoryLimitBytes = LimitMiB * 1024L * 1024L,
            },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        // The fixture either reported that it could not continue, or was ended by the job.
        // Both are the ceiling working; what must not happen is reaching the full amount.
        var stdout = result.NormalizedStdout;

        Assert.DoesNotContain($"GREW {AttemptMiB}", stdout, StringComparison.Ordinal);

        if (stdout.Contains("STOPPED", StringComparison.Ordinal))
        {
            var reached = Reached(stdout);

            Assert.True(
                reached < AttemptMiB,
                $"the fixture committed {reached} MiB against a {LimitMiB} MiB job ceiling");
        }
    }

    [Fact]
    public async Task Without_a_memory_ceiling_the_same_fixture_is_not_stopped()
    {
        using var workspace = TempWorkspace.Create();

        // The control. Without this, a fixture that simply always failed would make the test
        // above pass for the wrong reason - it is the difference between the two runs that
        // shows the ceiling did the stopping.
        const int AttemptMiB = 64;

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, Deadline, "grow", "8", AttemptMiB.ToString(CultureInfo.InvariantCulture)),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Contains($"GREW {AttemptMiB}", result.NormalizedStdout, StringComparison.Ordinal);
    }

    // --- Fail closed -------------------------------------------------------------------------

    [Fact]
    public async Task Limits_that_could_not_be_applied_start_no_child_at_all()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("config-failure.txt");
        File.WriteAllText(report, string.Empty);

        var runner = new ProcessRunner(
            workspace.Guard,
            new ProcessLaunchFaults { FailJobConfiguration = true });

        var request = Fixture.Request(workspace, Deadline, "marker", report) with
        {
            ResourceLimits = new ProcessResourceLimits { ActiveProcessLimit = 4 },
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        // No warning and a weaker job: a ceiling that was asked for and refused ends the run.
        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("SetInformationJobObject", result.StartFailureMessage!, StringComparison.Ordinal);

        // The configuration failure happens before anything is created, so the child does not
        // merely fail to run - it never exists.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(string.Empty, File.ReadAllText(report).Trim());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_meaningless_process_ceiling_is_refused_before_anything_starts(int limit)
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("invalid-count.txt");
        File.WriteAllText(report, string.Empty);

        var request = Fixture.Request(workspace, Deadline, "marker", report) with
        {
            ResourceLimits = new ProcessResourceLimits { ActiveProcessLimit = limit },
        };

        // Refused at the request boundary, like any other request that could never behave
        // sensibly - and notably not by starting an unbounded child first.
        await Assert.ThrowsAsync<ProcessRequestException>(
            () => workspace.Runner.RunAsync(request, CancellationToken.None));

        Assert.Equal(string.Empty, File.ReadAllText(report).Trim());
    }

    [Fact]
    public async Task A_meaningless_memory_ceiling_is_refused_before_anything_starts()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, Deadline, "exit", "0") with
        {
            ResourceLimits = new ProcessResourceLimits { JobMemoryLimitBytes = 0 },
        };

        await Assert.ThrowsAsync<ProcessRequestException>(
            () => workspace.Runner.RunAsync(request, CancellationToken.None));
    }

    // --- The guarantees quotas must not cost --------------------------------------------------

    [Fact]
    public async Task A_limited_job_still_reaps_its_tree_when_the_run_is_cancelled()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("limited-tree.txt");
        File.WriteAllText(report, string.Empty);

        using var cancellation = new CancellationTokenSource();

        // Generous enough that the tree really forms, so the reaping is what is being tested
        // rather than the ceiling.
        var request = Fixture.Request(workspace, Deadline, "spawn-tree", report) with
        {
            ResourceLimits = new ProcessResourceLimits { ActiveProcessLimit = 8 },
            GracefulShutdownPeriod = TimeSpan.FromSeconds(1),
        };

        var run = workspace.Runner.RunAsync(request, cancellation.Token);

        var formed = await ProcessLifecycleTests.WaitUntilAsync(
            () => File.ReadAllText(report).Contains("GRANDCHILD", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));

        Assert.True(formed, "the tree should have formed inside a job with room for it");

        await cancellation.CancelAsync();
        var result = await run;

        Assert.Equal(ProcessTerminationReason.Cancelled, result.TerminationReason);

        // Kill-on-close survived the addition of a quota. This is the regression that would
        // matter most: a limited job that stopped reaping would leave trees behind.
        await AssertNoSurvivorsAsync(report);
    }

    [Fact]
    public async Task A_limited_job_still_honours_the_hard_deadline()
    {
        using var workspace = TempWorkspace.Create();

        // Ceilings and deadlines are independent mechanisms; a run well inside its memory and
        // process ceilings is still ended by the clock.
        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(3), "silent", "600") with
        {
            ResourceLimits = new ProcessResourceLimits
            {
                ActiveProcessLimit = 4,
                JobMemoryLimitBytes = 256L * 1024 * 1024,
            },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.HardTimeout, result.TerminationReason);
        Assert.True(result.JobTerminated);
    }

    [Fact]
    public async Task Everything_the_launch_already_guaranteed_still_holds_under_a_ceiling()
    {
        using var workspace = TempWorkspace.Create();

        var marker = "COREX_LIMITED_" + Guid.CreateVersion7().ToString("N")[..8];
        Environment.SetEnvironmentVariable(marker, "still-here");

        try
        {
            var request = Fixture.Request(workspace, Deadline, "echo-args", "a b", "c\\", "q\"uote") with
            {
                EnvironmentAllowlist = [.. Fixture.DotnetHostAllowlist, marker],
                ResourceLimits = new ProcessResourceLimits
                {
                    ActiveProcessLimit = 4,
                    JobMemoryLimitBytes = 256L * 1024 * 1024,
                    ProcessMemoryLimitBytes = 256L * 1024 * 1024,
                },
            };

            var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);

            // argv still round trips exactly, so nothing about the job changed the launch.
            var observed = result.NormalizedStdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.StartsWith("ARG[", StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..])
                .ToList();

            Assert.Equal(["a b", "c\\", "q\"uote"], observed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
        }
    }

    private static int Reached(string stdout)
    {
        var line = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .First(l => l.StartsWith("STOPPED", StringComparison.Ordinal));

        return int.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    /// <summary>Every pid the fixture reported must be gone once the job closed.</summary>
    private static async Task AssertNoSurvivorsAsync(string reportPath)
    {
        // Only the lines that actually carry a process id. The fixture also records spawn
        // attempt numbers in the same file, and probing "pid 1" would ask about a system
        // process this test has no business touching.
        string[] levels = ["PARENT", "CHILD", "GRANDCHILD"];

        foreach (var line in File.ReadAllLines(reportPath))
        {
            var parts = line.Split('=');

            if (parts.Length != 2
                || !levels.Contains(parts[0], StringComparer.Ordinal)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            // Identified by pid, never by image name - killing by name could reach a process
            // this test never started.
            var dead = await ProcessLifecycleTests.WaitUntilAsync(
                () => !ProcessTreeTests.IsAlive(pid),
                TimeSpan.FromSeconds(30));

            Assert.True(dead, $"pid {pid} outlived the job");
        }
    }
}
