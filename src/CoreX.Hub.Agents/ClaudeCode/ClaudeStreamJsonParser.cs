using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreX.Hub.Agents.Abstractions;

namespace CoreX.Hub.Agents.ClaudeCode;

/// <summary>What the opening system event stated about the session that was actually started.</summary>
public sealed record ClaudeInitEvent
{
    public string? SessionId { get; init; }

    public string? PermissionMode { get; init; }

    public string? Model { get; init; }

    /// <summary>The tools the provider says are available. Compared against what was asked for.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>How many model context servers became active. Anything but none is a refusal.</summary>
    public int McpServerCount { get; init; }
}

/// <summary>The final provider event. Its absence is as meaningful as its contents.</summary>
public sealed record ClaudeResultEvent
{
    public string? Subtype { get; init; }

    /// <summary>Null when the provider did not state it, which is itself not a success.</summary>
    public bool? IsError { get; init; }

    public string? SessionId { get; init; }

    /// <summary>The provider's own final text. Never confused with tool content.</summary>
    public string? Text { get; init; }

    public string? StopReason { get; init; }

    public string? TerminalReason { get; init; }

    public AgentUsageMetadata Usage { get; init; } = AgentUsageMetadata.Empty;
}

/// <summary>Everything one run's stream said, as machine truth about the stream itself.</summary>
public sealed record ClaudeStreamReport
{
    public ClaudeInitEvent? Init { get; init; }

    public ClaudeResultEvent? Result { get; init; }

    /// <summary>Assistant text blocks in order. Tool content is deliberately not here.</summary>
    public string AssistantText { get; init; } = string.Empty;

    /// <summary>Event types this parser does not model. Informational, never fatal on its own.</summary>
    public IReadOnlyList<string> UnknownEventTypes { get; init; } = [];

    public int RateLimitEventCount { get; init; }

    /// <summary>
    /// Lines that could not be understood as an event. Not empty means the stream is not
    /// trustworthy, whatever else it appeared to say.
    /// </summary>
    public IReadOnlyList<string> ParseDiagnostics { get; init; } = [];

    public int LinesRead { get; init; }
}

/// <summary>
/// Reads the provider's newline delimited event stream as it arrives.
///
/// It is incremental by construction. The output of a long run is not a document that
/// exists at the end - it is a sequence of events that has to be understood while the run
/// is still going, both so progress can be reported and so a run that produced megabytes
/// is never held in memory as one value and handed to a whole-document deserialiser.
///
/// Three rules decide everything here.
///
///   An unknown event type is informational. New event types appear in tool releases, and
///   a Hub that fell over when one did would be broken by an upgrade it was not part of.
///
///   An unknown property is ignored. Same reason, one level down.
///
///   A line that is not an event at all is a diagnostic and is never smoothed over. The
///   stream is the only structured account of what the provider did; if part of it cannot
///   be read, what is left is not evidence that the run succeeded. Nothing is invented to
///   fill the gap and the raw output is preserved regardless (ADR-0009).
///
/// Feeding is single threaded: chunks arrive on one pump, in order. The report is read
/// after the run has completed.
/// </summary>
public sealed class ClaudeStreamJsonParser
{
    private readonly IProgress<AgentEvent>? _sink;
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _assistantText = new();
    private readonly List<string> _diagnostics = [];
    private readonly List<string> _unknownTypes = [];

    private ClaudeInitEvent? _init;
    private ClaudeResultEvent? _result;
    private int _rateLimitEvents;
    private int _lines;
    private bool _completed;

    /// <summary>
    /// <paramref name="sink"/> receives progress as the stream is read. It is optional so
    /// the parser can be exercised on its own without a progress channel.
    /// </summary>
    public ClaudeStreamJsonParser(IProgress<AgentEvent>? sink = null) => _sink = sink;

    /// <summary>Accepts one chunk of stdout. Chunk boundaries need not be line boundaries.</summary>
    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        foreach (var character in chunk)
        {
            if (character == '\n')
            {
                ReadLine(_pending.ToString());
                _pending.Clear();
                continue;
            }

            _pending.Append(character);
        }
    }

    /// <summary>Reads whatever is left when the stream ends without a final newline.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        if (_pending.Length > 0)
        {
            ReadLine(_pending.ToString());
            _pending.Clear();
        }
    }

    /// <summary>What the stream said. Call <see cref="Complete"/> first.</summary>
    public ClaudeStreamReport Snapshot() => new()
    {
        Init = _init,
        Result = _result,
        AssistantText = _assistantText.ToString(),
        UnknownEventTypes = [.. _unknownTypes],
        RateLimitEventCount = _rateLimitEvents,
        ParseDiagnostics = [.. _diagnostics],
        LinesRead = _lines,
    };

    private void ReadLine(string raw)
    {
        var line = raw.Trim('\r', ' ', '\t');

        if (line.Length == 0)
        {
            return;
        }

        _lines++;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            // The line itself is not repeated: it is already in the raw output that is
            // stored as evidence, and repeating it here would put unredacted provider text
            // into a diagnostic that travels separately.
            Diagnose($"line {_lines.ToString(CultureInfo.InvariantCulture)} is not valid json ({ex.Message})");
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                Diagnose($"line {_lines.ToString(CultureInfo.InvariantCulture)} is json but not an event object");
                return;
            }

            if (Text(root, "type") is not { Length: > 0 } type)
            {
                Diagnose($"line {_lines.ToString(CultureInfo.InvariantCulture)} declares no event type");
                return;
            }

            Dispatch(type, root);
        }
    }

    private void Dispatch(string type, JsonElement root)
    {
        switch (type)
        {
            case "system":
                ReadSystem(root);
                break;

            case "assistant":
                ReadAssistant(root);
                break;

            case "result":
                ReadResult(root);
                break;

            case "rate_limit_event":
                // Informational. A status event before a successful result says something
                // about the account, not about this run.
                _rateLimitEvents++;
                _sink?.Report(AgentEvent.Diagnostic("provider reported a rate limit status event"));
                break;

            default:
                if (!_unknownTypes.Contains(type, StringComparer.Ordinal))
                {
                    _unknownTypes.Add(type);
                    _sink?.Report(AgentEvent.Diagnostic($"provider emitted an unmodelled event type '{type}'"));
                }

                break;
        }
    }

    private void ReadSystem(JsonElement root)
    {
        if (!string.Equals(Text(root, "subtype"), "init", StringComparison.Ordinal))
        {
            return;
        }

        var tools = new List<string>();
        if (root.TryGetProperty("tools", out var toolArray) && toolArray.ValueKind == JsonValueKind.Array)
        {
            tools.AddRange(toolArray.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()!));
        }

        var servers = root.TryGetProperty("mcp_servers", out var mcp) && mcp.ValueKind == JsonValueKind.Array
            ? mcp.GetArrayLength()
            : 0;

        _init = new ClaudeInitEvent
        {
            SessionId = Text(root, "session_id"),
            PermissionMode = Text(root, "permissionMode") ?? Text(root, "permission_mode"),
            Model = Text(root, "model"),
            Tools = tools,
            McpServerCount = servers,
        };

        _sink?.Report(AgentEvent.Diagnostic(
            $"provider session {_init.SessionId}, permission mode {_init.PermissionMode}, "
            + $"{tools.Count.ToString(CultureInfo.InvariantCulture)} tools, "
            + $"{servers.ToString(CultureInfo.InvariantCulture)} context servers"));
    }

    /// <summary>
    /// Collects assistant text only.
    ///
    /// A content array is not a string and its first element is not always text: a turn can
    /// carry reasoning, a tool call and that tool's result alongside what was said. Treating
    /// any of those as the reply would put a tool argument where a sentence belongs.
    /// </summary>
    private void ReadAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !string.Equals(Text(block, "type"), "text", StringComparison.Ordinal)
                || Text(block, "text") is not { Length: > 0 } text)
            {
                continue;
            }

            _assistantText.Append(text);
            _sink?.Report(AgentEvent.Output(text));
        }
    }

    private void ReadResult(JsonElement root)
    {
        _result = new ClaudeResultEvent
        {
            Subtype = Text(root, "subtype"),
            IsError = root.TryGetProperty("is_error", out var isError)
                      && isError.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isError.GetBoolean()
                : null,
            SessionId = Text(root, "session_id"),
            Text = Text(root, "result"),
            StopReason = Text(root, "stop_reason"),
            TerminalReason = Text(root, "terminal_reason"),
            Usage = ReadUsage(root),
        };
    }

    /// <summary>
    /// Reads the provider's own account of what the run consumed.
    ///
    /// The per model map is kept whole rather than summed into the pinned model's totals: a
    /// run that pins one model can still report work by another, and a single pair of
    /// numbers would attribute all of it to the wrong place.
    /// </summary>
    private static AgentUsageMetadata ReadUsage(JsonElement root)
    {
        var models = new Dictionary<string, AgentModelUsage>(StringComparer.Ordinal);

        if (root.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in modelUsage.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                models[entry.Name] = new AgentModelUsage
                {
                    InputTokens = Number(entry.Value, "inputTokens"),
                    OutputTokens = Number(entry.Value, "outputTokens"),
                    CacheReadTokens = Number(entry.Value, "cacheReadInputTokens"),
                    CacheCreationTokens = Number(entry.Value, "cacheCreationInputTokens"),
                    CostAmount = Money(entry.Value, "costUSD"),
                };
            }
        }

        var usage = root.TryGetProperty("usage", out var totals) && totals.ValueKind == JsonValueKind.Object
            ? totals
            : default;

        var cost = Money(root, "total_cost_usd");

        return new AgentUsageMetadata
        {
            InputTokens = Number(usage, "input_tokens"),
            OutputTokens = Number(usage, "output_tokens"),
            CacheReadTokens = Number(usage, "cache_read_input_tokens"),
            CacheCreationTokens = Number(usage, "cache_creation_input_tokens"),

            // Provider reported, and metadata only: no Hub decision is made from it and no
            // ceiling is enforced with it (ADR-0009).
            CostAmount = cost,
            CostCurrency = cost is null ? null : "USD",
            ModelUsage = models,
        };
    }

    private void Diagnose(string detail)
    {
        _diagnostics.Add(detail);
        _sink?.Report(AgentEvent.Diagnostic(detail));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static decimal? Money(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number)
            ? number
            : null;
}
