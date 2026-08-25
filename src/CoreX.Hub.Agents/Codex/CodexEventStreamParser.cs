using System.Text;
using System.Text.Json;
using CoreX.Hub.Agents.Abstractions;

namespace CoreX.Hub.Agents.Codex;

/// <summary>What one command the reviewer ran did, as the provider reported it.</summary>
public sealed record CodexCommandEvent(string Command, string Status, int? ExitCode);

/// <summary>Everything the reader made of one run's event stream.</summary>
public sealed record CodexStreamReport
{
    /// <summary>The provider's own thread identity. Never invented.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Set when the stream carried a terminal completion event.</summary>
    public bool TurnCompleted { get; init; }

    /// <summary>Set when the stream said the turn failed, with the reason it gave.</summary>
    public string? TurnFailure { get; init; }

    /// <summary>Errors the stream reported, in order.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The last assistant message, which is where a structured answer arrives.</summary>
    public string? FinalMessage { get; init; }

    public IReadOnlyList<CodexCommandEvent> Commands { get; init; } = [];

    public AgentUsageMetadata Usage { get; init; } = AgentUsageMetadata.Empty;

    /// <summary>Event types this reader does not model. Kept so a new one is visible, not fatal.</summary>
    public IReadOnlyList<string> UnknownEvents { get; init; } = [];

    /// <summary>Lines that were not readable json at all.</summary>
    public IReadOnlyList<string> ParseDiagnostics { get; init; } = [];
}

/// <summary>
/// Reads the provider's JSONL event stream as it arrives.
///
/// Two rules shape it. An event this reader does not recognise is recorded and stepped over
/// rather than treated as a failure: the provider will add events, and a reviewer that
/// refused to finish because of a new informational line would be brittle in the one place
/// brittleness costs a real run. And a line that is not json at all never destroys the
/// evidence - the raw stream is captured separately and survives whatever this makes of it.
///
/// It decides nothing. Whether a run succeeded is settled by the process and by the presence
/// of a terminal event, in the runtime, not here.
/// </summary>
public sealed class CodexEventStreamParser
{
    private readonly IProgress<AgentEvent>? _sink;
    private readonly StringBuilder _pending = new();

    private readonly List<string> _errors = [];
    private readonly List<CodexCommandEvent> _commands = [];
    private readonly List<string> _unknown = [];
    private readonly List<string> _diagnostics = [];

    private string? _threadId;
    private bool _completed;
    private string? _failure;
    private string? _finalMessage;
    private AgentUsageMetadata _usage = AgentUsageMetadata.Empty;

    public CodexEventStreamParser(IProgress<AgentEvent>? sink = null) => _sink = sink;

    /// <summary>
    /// Takes whatever arrived, which is not necessarily whole lines.
    ///
    /// The pump reads fixed size blocks, so an event routinely arrives in two pieces and
    /// sometimes with three events in one piece. Reassembly is this reader's problem.
    /// </summary>
    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _pending.Append(chunk);

        while (true)
        {
            var text = _pending.ToString();
            var newline = text.IndexOf('\n', StringComparison.Ordinal);

            if (newline < 0)
            {
                return;
            }

            var line = text[..newline];
            _pending.Clear();
            _pending.Append(text[(newline + 1)..]);

            Read(line);
        }
    }

    /// <summary>Reads whatever is left when the stream ends without a final newline.</summary>
    public void Complete()
    {
        var rest = _pending.ToString();
        _pending.Clear();

        if (rest.Trim().Length > 0)
        {
            Read(rest);
        }
    }

    public CodexStreamReport Snapshot() => new()
    {
        ThreadId = _threadId,
        TurnCompleted = _completed,
        TurnFailure = _failure,
        Errors = [.. _errors],
        FinalMessage = _finalMessage,
        Commands = [.. _commands],
        Usage = _usage,
        UnknownEvents = [.. _unknown],
        ParseDiagnostics = [.. _diagnostics],
    };

    private void Read(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length == 0)
        {
            return;
        }

        if (trimmed[0] != '{')
        {
            // The provider writes diagnostics to stderr, but a merged stream can put one
            // here. It is not an event and it is not a failure of the stream.
            return;
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(trimmed).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _diagnostics.Add("unreadable event line: " + ex.Message);
            return;
        }

        var type = Text(root, "type");

        switch (type)
        {
            case "thread.started":
                _threadId = Text(root, "thread_id");
                _sink?.Report(AgentEvent.Started("provider thread " + (_threadId ?? "(unnamed)")));
                break;

            case "turn.started":
                break;

            case "turn.completed":
                _completed = true;
                _usage = ReadUsage(root);
                break;

            case "turn.failed":
                _failure = root.TryGetProperty("error", out var failure)
                    ? Text(failure, "message") ?? failure.GetRawText()
                    : "the provider reported a failed turn without saying why";
                break;

            case "error":
                _errors.Add(Text(root, "message") ?? root.GetRawText());
                break;

            case "item.started":
            case "item.completed":
                ReadItem(root, type);
                break;

            default:
                if (type is { Length: > 0 } && !_unknown.Contains(type, StringComparer.Ordinal))
                {
                    _unknown.Add(type);
                }

                break;
        }
    }

    private void ReadItem(JsonElement root, string eventType)
    {
        if (!root.TryGetProperty("item", out var item))
        {
            return;
        }

        var itemType = Text(item, "type");

        switch (itemType)
        {
            case "agent_message":
                // The last one wins: a structured answer is the final message, and anything
                // before it is the reviewer thinking out loud.
                if (Text(item, "text") is { Length: > 0 } text)
                {
                    _finalMessage = text;
                }

                break;

            case "command_execution":
                if (eventType == "item.completed")
                {
                    _commands.Add(new CodexCommandEvent(
                        Text(item, "command") ?? "(unnamed)",
                        Text(item, "status") ?? "(unknown)",
                        Number(item, "exit_code") is { } exit ? (int)exit : null));

                    _sink?.Report(AgentEvent.Diagnostic(
                        $"command {Text(item, "status")}: {Shorten(Text(item, "command"))}"));
                }

                break;

            case "error":
                _errors.Add(Text(item, "message") ?? item.GetRawText());
                break;

            default:
                if (itemType is { Length: > 0 } && !_unknown.Contains("item:" + itemType, StringComparer.Ordinal))
                {
                    _unknown.Add("item:" + itemType);
                }

                break;
        }
    }

    /// <summary>
    /// The provider's own account of what the turn consumed.
    ///
    /// Reasoning tokens are reported by this provider and have no home on the neutral
    /// record. They are not dropped: the raw stream is stored as evidence and still carries
    /// them, which is the honest place for a counter one provider has and another does not.
    /// </summary>
    private static AgentUsageMetadata ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return AgentUsageMetadata.Empty;
        }

        return new AgentUsageMetadata
        {
            InputTokens = Number(usage, "input_tokens"),
            OutputTokens = Number(usage, "output_tokens"),
            CacheReadTokens = Number(usage, "cached_input_tokens"),
            CacheCreationTokens = Number(usage, "cache_write_input_tokens"),
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string Shorten(string? command) =>
        command is null ? "(unnamed)" : command.Length <= 120 ? command : command[..120];
}
