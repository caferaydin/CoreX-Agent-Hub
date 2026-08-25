using CoreX.Hub.Agents.Codex;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The event stream reader on its own: no process, no adapter, no provider.
///
/// It is tested against the shapes that decide something - a stream that finished, one that
/// failed, one from a release this reader has never seen, and one that looks successful and
/// is not - plus the two ways a real stream is awkward: it arrives in pieces, and sometimes a
/// line is not json at all.
/// </summary>
public sealed class CodexEventStreamTests
{
    [Fact]
    public void A_normal_run_is_read_in_full()
    {
        var report = Read(CodexStreams.Success("the answer"));

        Assert.Equal(CodexStreams.Thread, report.ThreadId);
        Assert.True(report.TurnCompleted);
        Assert.Null(report.TurnFailure);
        Assert.Equal("the answer", report.FinalMessage);
        Assert.Empty(report.Errors);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void The_usage_the_provider_reported_is_carried_across()
    {
        var usage = Read(CodexStreams.Success()).Usage;

        Assert.Equal(13047, usage.InputTokens);
        Assert.Equal(13, usage.OutputTokens);
        Assert.Equal(6528, usage.CacheReadTokens);

        // Reasoning tokens have no home on the neutral record and are not forced into one.
        // They survive in the raw stream, which is stored as evidence.
        Assert.Null(usage.CostAmount);
    }

    [Fact]
    public void Chunk_boundaries_are_not_line_boundaries()
    {
        var whole = CodexStreams.Success("split across reads");
        var parser = new CodexEventStreamParser();

        for (var i = 0; i < whole.Length; i += 7)
        {
            parser.Append(whole.Substring(i, Math.Min(7, whole.Length - i)));
        }

        parser.Complete();
        var report = parser.Snapshot();

        Assert.Equal(CodexStreams.Thread, report.ThreadId);
        Assert.True(report.TurnCompleted);
        Assert.Equal("split across reads", report.FinalMessage);
    }

    [Fact]
    public void A_stream_that_ends_without_a_newline_is_still_read()
    {
        var parser = new CodexEventStreamParser();

        parser.Append(CodexStreams.ThreadStarted() + CodexStreams.TurnCompleted().TrimEnd('\n'));
        parser.Complete();

        Assert.True(parser.Snapshot().TurnCompleted);
    }

    // --- Failure shapes -------------------------------------------------------------------

    [Fact]
    public void A_failed_turn_is_read_with_the_reason_the_provider_gave()
    {
        var report = Read(
            CodexStreams.ThreadStarted()
            + CodexStreams.Error("Your access token could not be refreshed")
            + CodexStreams.TurnFailed("Your access token could not be refreshed"));

        Assert.False(report.TurnCompleted);
        Assert.Contains("access token", report.TurnFailure!, StringComparison.Ordinal);
        Assert.Single(report.Errors);

        // The thread it did open is still known: an attribution that exists is not thrown
        // away because the turn went wrong.
        Assert.Equal(CodexStreams.Thread, report.ThreadId);
    }

    [Fact]
    public void A_stream_with_no_thread_leaves_the_run_unattributable()
    {
        var report = Read(CodexStreams.TurnStarted() + CodexStreams.Message("hi") + CodexStreams.TurnCompleted());

        Assert.Null(report.ThreadId);
        Assert.True(report.TurnCompleted);
    }

    [Fact]
    public void A_stream_that_stops_early_says_it_did_not_finish()
    {
        var report = Read(CodexStreams.ThreadStarted() + CodexStreams.TurnStarted() + CodexStreams.Message("half"));

        Assert.False(report.TurnCompleted);
        Assert.Null(report.TurnFailure);
        Assert.Equal("half", report.FinalMessage);
    }

    // --- Resilience -----------------------------------------------------------------------

    [Fact]
    public void A_line_that_is_not_json_is_recorded_and_stepped_over()
    {
        var report = Read(
            CodexStreams.ThreadStarted() + "{ this is not json\n" + CodexStreams.TurnCompleted());

        Assert.True(report.TurnCompleted);
        Assert.Single(report.ParseDiagnostics);
    }

    [Fact]
    public void Plain_text_on_the_stream_is_not_an_event_and_not_a_failure()
    {
        var report = Read(
            "2026-08-25T11:44:28Z WARN something the tool wanted to say\n"
            + CodexStreams.Success());

        Assert.True(report.TurnCompleted);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void An_event_this_reader_has_never_seen_is_noted_rather_than_fatal()
    {
        var report = Read(
            CodexStreams.ThreadStarted()
            + "{\"type\":\"turn.compacted\",\"detail\":\"future\"}\n"
            + "{\"type\":\"item.completed\",\"item\":{\"type\":\"web_search\",\"query\":\"x\"}}\n"
            + CodexStreams.TurnCompleted());

        // A provider that adds an informational event must not be able to stop a review from
        // finishing, and a reader that pretended it saw nothing would hide the change.
        Assert.True(report.TurnCompleted);
        Assert.Contains("turn.compacted", report.UnknownEvents);
        Assert.Contains("item:web_search", report.UnknownEvents);
    }

    // --- Command telemetry ------------------------------------------------------------------

    [Fact]
    public void What_the_reviewer_ran_is_read_from_the_stream()
    {
        var report = Read(
            CodexStreams.ThreadStarted()
            + CodexStreams.Command("powershell.exe -Command Get-Content input.txt", "completed", 0)
            + CodexStreams.Command("powershell.exe -Command Set-Content out.txt", "declined", -1)
            + CodexStreams.Message("done")
            + CodexStreams.TurnCompleted());

        Assert.Equal(2, report.Commands.Count);
        Assert.Equal("completed", report.Commands[0].Status);
        Assert.Equal(0, report.Commands[0].ExitCode);
        Assert.Equal("declined", report.Commands[1].Status);
        Assert.Equal(-1, report.Commands[1].ExitCode);
    }

    [Fact]
    public void The_last_message_is_the_answer_and_earlier_ones_are_working_out_loud()
    {
        var report = Read(
            CodexStreams.ThreadStarted()
            + CodexStreams.Message("I will read the file first.")
            + CodexStreams.Message("{\"marker\":\"OK\"}")
            + CodexStreams.TurnCompleted());

        Assert.Equal("{\"marker\":\"OK\"}", report.FinalMessage);
    }

    private static CodexStreamReport Read(string stdout)
    {
        var parser = new CodexEventStreamParser();

        parser.Append(stdout);
        parser.Complete();

        return parser.Snapshot();
    }
}
