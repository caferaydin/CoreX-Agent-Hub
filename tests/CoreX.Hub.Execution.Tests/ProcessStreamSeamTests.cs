using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// The two narrow seams a real provider adapter needs from the single launch point: a
/// payload that goes down stdin instead of onto the command line, and stdout observed
/// while the run is still going.
///
/// Both are additive and both are optional. A request that sets neither behaves exactly as
/// it did before, which is asserted here rather than assumed.
/// </summary>
public sealed class ProcessStreamSeamTests
{
    [Fact]
    public async Task A_stdin_payload_reaches_the_child_and_never_appears_in_the_argument_vector()
    {
        using var workspace = TempWorkspace.Create();

        const string payload = "a prompt that has no business being readable by every process here";

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), "stdin-echo") with { StdinPayload = payload },
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"STDIN[{payload}]", result.NormalizedStdout);
    }

    [Fact]
    public async Task A_stdin_payload_is_followed_by_end_of_stream_so_a_reading_child_finishes()
    {
        // Without the close, a child that reads to end waits for ever and the run only ends
        // on a deadline. That would turn every prompted run into a timeout.
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), "stdin-echo") with { StdinPayload = string.Empty },
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal("STDIN[]", result.NormalizedStdout);
    }

    [Fact]
    public async Task Not_supplying_a_payload_gives_the_child_end_of_stream_at_once()
    {
        // The default that a real verification run had to teach us. Stdin is always
        // redirected, so a child handed a pipe nobody closes can wait on it for ever: the
        // whole solution's tests finished in six minutes and the process then sat there
        // until a twenty minute deadline killed it, because end of stream never came.
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), "stdin-echo"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        // It read to end and got nothing, rather than blocking until a deadline.
        Assert.Equal("STDIN[]", result.NormalizedStdout);
        Assert.False(result.JobTerminated);
    }

    [Fact]
    public async Task A_child_that_waits_on_stdin_can_still_be_given_one_that_stays_open()
    {
        // The old behaviour is still available, but it is now something a request asks for.
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(60), "stdin-wait") with
        {
            SoftTimeout = TimeSpan.FromSeconds(1),
            GracefulShutdownPeriod = TimeSpan.FromSeconds(10),
            KeepStandardInputOpen = true,
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        // It waited, and it was still waiting when the soft deadline closed stdin for it.
        Assert.Equal(ProcessTerminationReason.SoftTimeout, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("waiting-stdin", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.Contains("STDIN-CLOSED", result.NormalizedStdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_tool_run_ends_when_the_tool_ends_rather_than_at_its_deadline()
    {
        // The shape of the defect, in miniature: a child that never reads stdin, given far
        // more time than it needs. Before the default changed this exited on the deadline
        // with no exit code; now it exits on its own, promptly.
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromMinutes(5), "stdout", "work done"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.JobTerminated);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stdout_is_observed_while_the_run_is_still_going()
    {
        using var workspace = TempWorkspace.Create();

        var firstSeenAt = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(10), "chatty", "100") with
        {
            SoftTimeout = TimeSpan.FromSeconds(3),
            StandardOutputObserver = _ => firstSeenAt.TrySetResult(DateTimeOffset.UtcNow),
        };

        var run = workspace.Runner.RunAsync(request, CancellationToken.None);

        var observedAt = await firstSeenAt.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var result = await run;
        var finishedAt = DateTimeOffset.UtcNow;

        Assert.Equal(ProcessTerminationReason.SoftTimeout, result.TerminationReason);
        Assert.True(
            finishedAt - observedAt > TimeSpan.FromSeconds(1),
            "output should have been observed long before the run ended");
    }

    [Fact]
    public async Task The_observer_sees_everything_the_capture_ceiling_would_have_dropped()
    {
        using var workspace = TempWorkspace.Create();

        var observed = 0L;

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(60), "flood", "400") with
        {
            MaxCapturedCharactersPerStream = 4096,
            StandardOutputObserver = chunk => Interlocked.Add(ref observed, chunk.Length),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.True(result.StdoutTruncated);
        Assert.Equal(result.StdoutCharactersObserved, Interlocked.Read(ref observed));
        Assert.True(Interlocked.Read(ref observed) > 4096);
    }

    [Fact]
    public async Task An_observer_that_throws_cannot_end_a_run()
    {
        // Observation is a courtesy to the caller. A broken observer that abandoned the pump
        // would leave the pipe unread and eventually stall the child.
        using var workspace = TempWorkspace.Create();

        var calls = 0;

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "stdout", "still captured") with
        {
            StandardOutputObserver = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("a deliberately broken observer");
            },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("still captured", result.NormalizedStdout);
        Assert.True(Volatile.Read(ref calls) > 0);
    }

    [Fact]
    public async Task Observed_chunks_reassemble_into_exactly_what_was_captured()
    {
        using var workspace = TempWorkspace.Create();

        var chunks = new List<string>();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(60), "flood", "40") with
        {
            StandardOutputObserver = chunk =>
            {
                lock (chunks)
                {
                    chunks.Add(chunk);
                }
            },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        string joined;
        lock (chunks)
        {
            joined = string.Concat(chunks);
        }

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.False(result.StdoutTruncated);
        Assert.Equal(result.RawStdout, joined);
        Assert.Equal(result.StdoutCharactersObserved, joined.Length);
    }

    [Fact]
    public async Task Stderr_is_never_handed_to_the_stdout_observer()
    {
        using var workspace = TempWorkspace.Create();

        var seen = new List<string>();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "both", "OUTLINE", "ERRLINE") with
        {
            StandardOutputObserver = chunk =>
            {
                lock (seen)
                {
                    seen.Add(chunk);
                }
            },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        string joined;
        lock (seen)
        {
            joined = string.Concat(seen);
        }

        Assert.Contains("ERRLINE", result.NormalizedStderr, StringComparison.Ordinal);
        Assert.Contains("OUTLINE", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("ERRLINE", joined, StringComparison.Ordinal);
    }
}
