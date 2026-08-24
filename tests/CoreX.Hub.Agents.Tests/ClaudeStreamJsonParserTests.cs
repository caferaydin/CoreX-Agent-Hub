using System.Text.Json;
using CoreX.Hub.Agents.ClaudeCode;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The event stream reader on its own: no process, no adapter, no provider.
///
/// It is tested against the shapes that actually decide something - a stream that can be
/// read, a stream that cannot, a stream from a newer release, and a stream that looks
/// successful but is not.
/// </summary>
public sealed class ClaudeStreamJsonParserTests
{
    private const string Session = "8f14e45f-ceea-467a-9f3a-1b2c3d4e5f60";

    [Fact]
    public void A_normal_run_is_read_in_full()
    {
        var report = Read(
            Init(Session),
            Assistant("hello "),
            Assistant("world"),
            Result(Session));

        Assert.Equal(Session, report.Init!.SessionId);
        Assert.Equal("acceptEdits", report.Init.PermissionMode);
        Assert.Equal(["Read", "Edit", "Write", "Glob", "Grep"], report.Init.Tools);
        Assert.Equal(0, report.Init.McpServerCount);

        Assert.Equal("hello world", report.AssistantText);

        Assert.Equal("success", report.Result!.Subtype);
        Assert.False(report.Result.IsError);
        Assert.Equal(Session, report.Result.SessionId);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void Chunk_boundaries_are_not_line_boundaries()
    {
        // The pump reads fixed size blocks, so an event routinely arrives in two pieces and
        // sometimes with three events in one piece. Reassembly is the parser's problem.
        var whole = string.Concat(Init(Session), Assistant("split"), Result(Session));

        var parser = new ClaudeStreamJsonParser();

        for (var i = 0; i < whole.Length; i += 7)
        {
            parser.Append(whole.Substring(i, Math.Min(7, whole.Length - i)));
        }

        parser.Complete();
        var report = parser.Snapshot();

        Assert.NotNull(report.Init);
        Assert.Equal("split", report.AssistantText);
        Assert.NotNull(report.Result);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void A_final_line_without_a_newline_is_still_read()
    {
        var parser = new ClaudeStreamJsonParser();
        parser.Append(Init(Session));
        parser.Append(Result(Session).TrimEnd('\n'));
        parser.Complete();

        Assert.NotNull(parser.Snapshot().Result);
    }

    [Fact]
    public void An_unmodelled_event_type_is_recorded_and_survived()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"a_future_event\",\"detail\":\"whatever\"}\n",
            Assistant("done"),
            Result(Session));

        Assert.Equal(["a_future_event"], report.UnknownEventTypes);
        Assert.Empty(report.ParseDiagnostics);
        Assert.NotNull(report.Result);
    }

    [Fact]
    public void An_unmodelled_property_is_ignored()
    {
        var report = Read(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + Session + "\","
            + "\"permissionMode\":\"acceptEdits\",\"tools\":[\"Read\"],\"mcp_servers\":[],"
            + "\"something_new\":{\"deeply\":[{\"nested\":true}]}}\n",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + Session
            + "\",\"result\":\"done\",\"another_new_thing\":123}\n");

        Assert.Empty(report.ParseDiagnostics);
        Assert.Equal(Session, report.Init!.SessionId);
        Assert.False(report.Result!.IsError);
    }

    [Fact]
    public void A_line_that_is_not_an_event_is_a_diagnostic_and_never_smoothed_over()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"assistant\", this is not json\n",
            Assistant("done"),
            Result(Session));

        // The result parsed, and that is exactly why this matters: the stream still looks
        // successful, and it is not trustworthy.
        Assert.NotNull(report.Result);
        Assert.Single(report.ParseDiagnostics);
        Assert.Contains("line 2", report.ParseDiagnostics[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Json_that_is_not_an_event_object_is_a_diagnostic()
    {
        Assert.Single(Read("[1,2,3]\n").ParseDiagnostics);
        Assert.Single(Read("{\"no\":\"type\"}\n").ParseDiagnostics);
        Assert.Single(Read("\"a bare string\"\n").ParseDiagnostics);
    }

    [Fact]
    public void Blank_lines_are_not_events_and_are_not_diagnostics()
    {
        var report = Read("\n", "   \n", "\r\n", Init(Session), "\n", Result(Session));

        Assert.Empty(report.ParseDiagnostics);
        Assert.Equal(2, report.LinesRead);
    }

    [Fact]
    public void Assistant_content_is_not_assumed_to_be_text()
    {
        // A turn can carry reasoning, a tool call and that tool's output alongside what was
        // said. Only what was said is the reply; treating the first block as text would put a
        // file path where a sentence belongs.
        var report = Read(
            Init(Session),
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"thinking\",\"thinking\":\"REASONING\"},"
            + "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"TOOLARGUMENT\"}},"
            + "{\"type\":\"text\",\"text\":\"first. \"},"
            + "{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"TOOLOUTPUT\"},"
            + "{\"type\":\"text\",\"text\":\"second.\"}]}}\n",
            Result(Session));

        Assert.Equal("first. second.", report.AssistantText);
        Assert.DoesNotContain("REASONING", report.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOLARGUMENT", report.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOLOUTPUT", report.AssistantText, StringComparison.Ordinal);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void An_assistant_event_with_no_content_array_is_survived()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\"}}\n",
            "{\"type\":\"assistant\"}\n",
            Result(Session));

        Assert.Equal(string.Empty, report.AssistantText);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void More_than_one_model_survives_the_read()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + Session + "\","
            + "\"total_cost_usd\":0.0425,"
            + "\"usage\":{\"input_tokens\":120,\"output_tokens\":45,\"cache_read_input_tokens\":900,"
            + "\"cache_creation_input_tokens\":300},"
            + "\"modelUsage\":{"
            + "\"claude-opus-5\":{\"inputTokens\":100,\"outputTokens\":40,\"cacheReadInputTokens\":900,"
            + "\"cacheCreationInputTokens\":300,\"costUSD\":0.04},"
            + "\"a-second-model\":{\"inputTokens\":20,\"outputTokens\":5,\"costUSD\":0.0025}}}\n");

        var usage = report.Result!.Usage;

        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(45, usage.OutputTokens);
        Assert.Equal(900, usage.CacheReadTokens);
        Assert.Equal(300, usage.CacheCreationTokens);
        Assert.Equal(0.0425m, usage.CostAmount);
        Assert.Equal("USD", usage.CostCurrency);

        Assert.Equal(2, usage.ModelUsage.Count);
        Assert.Equal(100, usage.ModelUsage["claude-opus-5"].InputTokens);
        Assert.Equal(0.0025m, usage.ModelUsage["a-second-model"].CostAmount);

        // Not summed into the pinned model, and not silently dropped either.
        Assert.NotEqual(usage.InputTokens, usage.ModelUsage["claude-opus-5"].InputTokens);
    }

    [Fact]
    public void A_result_with_no_usage_reports_nothing_rather_than_zero()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + Session + "\"}\n");

        var usage = report.Result!.Usage;

        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.CostAmount);
        Assert.Null(usage.CostCurrency);
        Assert.Empty(usage.ModelUsage);
    }

    [Fact]
    public void A_rate_limit_status_event_is_informational()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"rate_limit_event\",\"rate_limit\":{\"status\":\"allowed_warning\"}}\n",
            Assistant("done"),
            Result(Session));

        Assert.Equal(1, report.RateLimitEventCount);
        Assert.Empty(report.ParseDiagnostics);
        Assert.False(report.Result!.IsError);
    }

    [Fact]
    public void A_missing_final_event_is_reported_as_missing()
    {
        var report = Read(Init(Session), Assistant("i stopped talking"));

        Assert.Null(report.Result);
        Assert.Empty(report.ParseDiagnostics);
    }

    [Fact]
    public void A_stated_error_and_an_unstated_one_are_both_visible()
    {
        var stated = Read(
            Init(Session),
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,"
            + "\"session_id\":\"" + Session + "\",\"terminal_reason\":\"refusal\"}\n");

        Assert.True(stated.Result!.IsError);
        Assert.Equal("refusal", stated.Result.TerminalReason);

        // A result that never says whether it failed is not a success either.
        var silent = Read(Init(Session), "{\"type\":\"result\",\"subtype\":\"success\"}\n");

        Assert.Null(silent.Result!.IsError);
    }

    [Fact]
    public void A_context_server_that_became_active_is_counted()
    {
        var report = Read(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + Session + "\","
            + "\"permissionMode\":\"acceptEdits\",\"tools\":[\"Read\"],"
            + "\"mcp_servers\":[{\"name\":\"one\"},{\"name\":\"two\"}]}\n");

        Assert.Equal(2, report.Init!.McpServerCount);
    }

    private static ClaudeStreamReport Read(params string[] chunks)
    {
        var parser = new ClaudeStreamJsonParser();

        foreach (var chunk in chunks)
        {
            parser.Append(chunk);
        }

        parser.Complete();
        return parser.Snapshot();
    }

    private static string Init(string session) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + session + "\","
        + "\"permissionMode\":\"acceptEdits\",\"model\":\"claude-opus-5\","
        + "\"tools\":[\"Read\",\"Edit\",\"Write\",\"Glob\",\"Grep\"],\"mcp_servers\":[]}\n";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":"
        + "[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}\n";

    // --- Structured output ------------------------------------------------------------
    //
    // This field is the only thing that separates a review from a provider that merely
    // finished. Measured in Phase 7A: the run can report success, exit zero, and carry no
    // structured output at all - so what the parser does with its absence decides whether a
    // schema failure looks like a clean review.

    [Fact]
    public void A_structured_result_is_carried_through_exactly_as_it_arrived()
    {
        var report = Read(Init(Session), ResultWith(Session, "{\"findings\":[],\"summary\":\"nothing\"}"));

        Assert.NotNull(report.Result!.StructuredOutput);

        // Raw json, not a parsed shape: this module knows nothing about findings, and only
        // whoever supplied the schema can say whether what came back conforms to it.
        using var document = JsonDocument.Parse(report.Result.StructuredOutput!);

        Assert.Equal("nothing", document.RootElement.GetProperty("summary").GetString());
        Assert.Empty(document.RootElement.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public void A_successful_result_with_no_structured_output_says_so_rather_than_inventing_one()
    {
        var report = Read(Init(Session), Result(Session));

        Assert.Equal("success", report.Result!.Subtype);
        Assert.False(report.Result.IsError);
        Assert.Null(report.Result.StructuredOutput);
    }

    [Fact]
    public void A_null_structured_output_is_the_same_as_none()
    {
        var report = Read(
            Init(Session),
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + Session
            + "\",\"result\":\"done\",\"structured_output\":null}\n");

        Assert.Null(report.Result!.StructuredOutput);
    }

    [Fact]
    public void A_structured_result_that_arrives_in_pieces_is_still_whole()
    {
        var whole = string.Concat(
            Init(Session), ResultWith(Session, "{\"findings\":[],\"summary\":\"split across reads\"}"));

        var parser = new ClaudeStreamJsonParser();

        for (var i = 0; i < whole.Length; i += 5)
        {
            parser.Append(whole.Substring(i, Math.Min(5, whole.Length - i)));
        }

        parser.Complete();

        var structured = parser.Snapshot().Result!.StructuredOutput;

        Assert.NotNull(structured);
        Assert.Contains("split across reads", structured!, StringComparison.Ordinal);
    }

    private static string Result(string session) =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + session + "\","
        + "\"result\":\"done\",\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}\n";

    private static string ResultWith(string session, string structuredOutput) =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"" + session + "\","
        + "\"result\":\"done\",\"structured_output\":" + structuredOutput + "}\n";
}
