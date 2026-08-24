using System.Diagnostics;
using System.Globalization;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// The three deadlines, external cancellation, and the guarantee that ending a run ends
/// the whole process tree.
/// </summary>
public sealed class ProcessLifecycleTests
{
    /// <summary>
    /// Soft deadline: stdin is closed, the child is given a bounded grace period, and a
    /// child that honours end of stream exits by itself. The reason still records that
    /// the soft deadline initiated the shutdown.
    /// </summary>
    [Fact]
    public async Task Soft_timeout_requests_graceful_shutdown()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(60), "stdin-wait") with
        {
            SoftTimeout = TimeSpan.FromSeconds(1),
            GracefulShutdownPeriod = TimeSpan.FromSeconds(10),

            // This child exists to wait on stdin, which is now something a request asks
            // for rather than what every child gets by default.
            KeepStandardInputOpen = true,
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.SoftTimeout, result.TerminationReason);
        Assert.True(result.TimedOut);

        // The child shut down cleanly rather than being killed.
        Assert.False(result.JobTerminated);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("STDIN-CLOSED", result.NormalizedStdout, StringComparison.Ordinal);

        Assert.InRange(result.Duration, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(20));
        EvidenceLog.Write(Format("SoftTimeout", result));
    }

    /// <summary>Idle deadline: alive but producing nothing on either stream.</summary>
    [Fact]
    public async Task Idle_timeout_ends_a_quiet_process()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(60), "silent", "300") with
        {
            IdleTimeout = TimeSpan.FromSeconds(1),
            GracefulShutdownPeriod = TimeSpan.FromSeconds(1),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.IdleTimeout, result.TerminationReason);
        Assert.True(result.JobTerminated);
        Assert.Null(result.ExitCode);
        Assert.InRange(result.Duration, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(20));

        EvidenceLog.Write(Format("IdleTimeout", result));
    }

    /// <summary>
    /// Output activity keeps the idle clock alive, so a chatty process must reach the
    /// hard ceiling instead. This is what makes idle and hard semantically different.
    /// </summary>
    [Fact]
    public async Task Output_activity_prevents_the_idle_timeout_and_the_hard_ceiling_wins()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(4), "chatty", "100") with
        {
            IdleTimeout = TimeSpan.FromSeconds(1),
            GracefulShutdownPeriod = TimeSpan.FromSeconds(1),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.HardTimeout, result.TerminationReason);
        Assert.NotEqual(ProcessTerminationReason.IdleTimeout, result.TerminationReason);
        Assert.True(result.JobTerminated);
        Assert.Contains("tick 0", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.InRange(result.Duration, TimeSpan.FromSeconds(3.9), TimeSpan.FromSeconds(20));

        EvidenceLog.Write(Format("HardTimeout(chatty)", result));
    }

    /// <summary>The hard ceiling is absolute and is never postponed by output.</summary>
    [Fact]
    public async Task Hard_timeout_ends_a_process_that_never_stops()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(2), "silent", "300");

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.HardTimeout, result.TerminationReason);
        Assert.True(result.JobTerminated);
        Assert.Null(result.ExitCode);
        Assert.InRange(result.Duration, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(20));

        EvidenceLog.Write(Format("HardTimeout(silent)", result));
    }

    [Fact]
    public async Task External_cancellation_produces_a_structured_result()
    {
        using var workspace = TempWorkspace.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(60), "silent", "300") with
        {
            GracefulShutdownPeriod = TimeSpan.FromSeconds(1),
        };

        var result = await workspace.Runner.RunAsync(request, cancellation.Token);

        Assert.Equal(ProcessTerminationReason.Cancelled, result.TerminationReason);
        Assert.True(result.Cancelled);
        Assert.True(result.JobTerminated);
        Assert.InRange(result.Duration, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(20));

        EvidenceLog.Write(Format("Cancelled", result));
    }

    [Theory]
    [InlineData(5, 10, 5)]
    [InlineData(11, null, 10)]
    [InlineData(null, 10, 10)]
    [InlineData(0, null, 10)]
    public void Nonsensical_timeout_combinations_are_rejected(int? softSeconds, int? idleSeconds, int hardSeconds)
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(hardSeconds), "exit", "0") with
        {
            SoftTimeout = softSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            IdleTimeout = idleSeconds is { } i ? TimeSpan.FromSeconds(i) : null,
        };

        Assert.Throws<ProcessRequestException>(request.Validate);
    }

    private static string Format(string label, ProcessRunResult result) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{label,-22} reason={result.TerminationReason,-14} exit={result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "null",-5} jobTerminated={result.JobTerminated,-5} duration={result.Duration.TotalMilliseconds:F0}ms pid={result.ProcessId}");

    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan deadline)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}
