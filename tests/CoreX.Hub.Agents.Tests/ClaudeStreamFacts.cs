using System.Text.Json;

namespace CoreX.Hub.Agents.Tests;

/// <summary>One tool the provider actually invoked, as the raw stream recorded it.</summary>
internal sealed record ToolAttempt(string Id, string Name, string Target, string Raw);

/// <summary>
/// Structural facts read straight out of a run's raw stdout.
///
/// Deliberately not the production parser. The adapter aggregates what it needs to decide
/// an outcome; an acceptance proof about what the provider *tried to do* has to read the
/// stream itself, so that the thing being proved and the thing doing the proving are not
/// the same code.
/// </summary>
internal static class ClaudeStreamFacts
{
    /// <summary>Every event object in the stream, in order. Unparseable lines are skipped.</summary>
    public static IReadOnlyList<JsonElement> Events(string stdout)
    {
        var events = new List<JsonElement>();

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            try
            {
                events.Add(JsonDocument.Parse(trimmed).RootElement.Clone());
            }
            catch (JsonException)
            {
                // A line the adapter would also have refused. Not this helper's business.
            }
        }

        return events;
    }

    public static JsonElement Init(string stdout) =>
        Events(stdout).First(e =>
            Text(e, "type") == "system" && Text(e, "subtype") == "init");

    public static JsonElement? Result(string stdout) =>
        Events(stdout).LastOrDefault(e => Text(e, "type") == "result") is { ValueKind: JsonValueKind.Object } r
            ? r
            : null;

    public static IReadOnlyList<string> Tools(string stdout) =>
        [.. Init(stdout).GetProperty("tools").EnumerateArray().Select(t => t.GetString()!)];

    /// <summary>
    /// Every tool the provider actually invoked, with whatever path it aimed at.
    ///
    /// This is the difference between "the file did not change" and "the tool was used and
    /// refused". Only the second is evidence about the boundary.
    /// </summary>
    public static IReadOnlyList<ToolAttempt> ToolAttempts(string stdout)
    {
        var attempts = new List<ToolAttempt>();

        foreach (var element in Events(stdout))
        {
            if (Text(element, "type") is not ("assistant" or "user")
                || !element.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || Text(block, "type") != "tool_use")
                {
                    continue;
                }

                var target = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                    ? Text(input, "file_path") ?? Text(input, "path") ?? Text(input, "command") ?? string.Empty
                    : string.Empty;

                attempts.Add(new ToolAttempt(
                    Text(block, "id") ?? string.Empty,
                    Text(block, "name") ?? string.Empty,
                    target,
                    block.GetRawText()));
            }
        }

        return attempts;
    }

    /// <summary>
    /// How a tool call ended, and - when it failed - why.
    ///
    /// The distinction the Phase 6B containment gate turns on: a tool that refused to act
    /// because of where it was pointed is a boundary; a tool that refused because of its own
    /// usage rules is not, and treating the second as the first would report a containment
    /// proof that had measured nothing.
    /// </summary>
    public enum ToolOutcome
    {
        /// <summary>No result came back for this invocation.</summary>
        Unanswered,

        Succeeded,

        /// <summary>Refused by the tool's own precondition, not by any boundary.</summary>
        PreconditionError,

        /// <summary>Refused for some other reason - permission, path, policy.</summary>
        Refused,
    }

    /// <summary>
    /// Tool preconditions, which are usage rules rather than boundaries. Matched narrowly and
    /// on the provider's own wording so that a real refusal can never be misread as one.
    /// </summary>
    private static readonly string[] PreconditionMarkers =
        ["has not been read yet", "read it first", "String to replace not found", "has been modified since"];

    /// <summary>Classifies every tool invocation by the result that came back for it.</summary>
    public static IReadOnlyDictionary<string, (ToolOutcome Outcome, string Raw)> ToolOutcomes(string stdout)
    {
        var outcomes = new Dictionary<string, (ToolOutcome, string)>(StringComparer.Ordinal);

        foreach (var element in Events(stdout))
        {
            if (!element.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || Text(block, "type") != "tool_result"
                    || Text(block, "tool_use_id") is not { Length: > 0 } id)
                {
                    continue;
                }

                var raw = block.GetRawText();

                var isError = block.TryGetProperty("is_error", out var flag)
                              && flag.ValueKind == JsonValueKind.True;

                var outcome = !isError
                    ? ToolOutcome.Succeeded
                    : PreconditionMarkers.Any(m => raw.Contains(m, StringComparison.OrdinalIgnoreCase))
                        ? ToolOutcome.PreconditionError
                        : ToolOutcome.Refused;

                outcomes[id] = (outcome, raw);
            }
        }

        return outcomes;
    }

    /// <summary>Tool results the provider marked as errors, keyed by the invocation they answer.</summary>
    public static IReadOnlyDictionary<string, string> FailedToolResults(string stdout)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in Events(stdout))
        {
            if (!element.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || Text(block, "type") != "tool_result"
                    || Text(block, "tool_use_id") is not { Length: > 0 } id)
                {
                    continue;
                }

                var isError = block.TryGetProperty("is_error", out var flag)
                              && flag.ValueKind == JsonValueKind.True;

                if (isError)
                {
                    failures[id] = block.GetRawText();
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// The provider's own structured record of what it refused, from the final result event.
    /// </summary>
    public static IReadOnlyList<JsonElement> PermissionDenials(string stdout)
    {
        if (Result(stdout) is not { } result
            || !result.TryGetProperty("permission_denials", out var denials)
            || denials.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. denials.EnumerateArray()];
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
