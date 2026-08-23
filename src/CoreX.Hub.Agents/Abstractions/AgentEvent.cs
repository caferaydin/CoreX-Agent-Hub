namespace CoreX.Hub.Agents.Abstractions;

/// <summary>
/// What kind of thing happened during a run. Deliberately small and provider neutral: no
/// vendor event names, no tool taxonomy and no workflow semantics.
/// </summary>
public enum AgentEventKind
{
    Started,
    Output,
    StructuredOutputAvailable,
    Completed,
    Diagnostic,
}

/// <summary>
/// One point on the progress stream. This is a progress channel, not an event bus: there
/// is no subscription, no routing and no ordering guarantee beyond the order the runtime
/// reports.
/// </summary>
public sealed record AgentEvent
{
    public required AgentEventKind Kind { get; init; }

    public string Message { get; init; } = string.Empty;

    public static AgentEvent Started(string message) => new() { Kind = AgentEventKind.Started, Message = message };

    public static AgentEvent Output(string message) => new() { Kind = AgentEventKind.Output, Message = message };

    public static AgentEvent Structured(string path) =>
        new() { Kind = AgentEventKind.StructuredOutputAvailable, Message = path };

    public static AgentEvent Completed(string message) =>
        new() { Kind = AgentEventKind.Completed, Message = message };

    public static AgentEvent Diagnostic(string message) =>
        new() { Kind = AgentEventKind.Diagnostic, Message = message };
}
