using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Codex;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// A process runner that answers from a script instead of starting anything.
///
/// The real runner is proved against real processes by the execution tests. What the Codex
/// adapter needs proving about is what it makes of a stream and a termination, and those are
/// states that are awkward to produce for real and trivial to state here.
/// </summary>
public sealed class ScriptedCodexProcesses : IAgentProcessRunner
{
    private readonly Queue<AgentProcessResult> _answers = new();

    public List<AgentProcessRequest> Requests { get; } = [];

    /// <summary>What the last request was told to write on the child's standard input.</summary>
    public string? LastStandardInput => Requests.Count == 0 ? null : Requests[^1].StandardInputText;

    public ScriptedCodexProcesses Returning(AgentProcessResult result)
    {
        _answers.Enqueue(result);
        return this;
    }

    /// <summary>A run that exited cleanly, having written the given event stream.</summary>
    public ScriptedCodexProcesses Streaming(string stdout, int exitCode = 0) =>
        Returning(Result(AgentProcessTermination.Exited, exitCode, stdout));

    public ScriptedCodexProcesses Ending(AgentProcessTermination termination, string stdout = "") =>
        Returning(Result(termination, termination == AgentProcessTermination.Exited ? 0 : null, stdout));

    public Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        // The observer is how the production runner feeds the parser, so the fake feeds it
        // the same way rather than letting the adapter read a captured string instead.
        var result = _answers.Count > 0
            ? _answers.Dequeue()
            : Result(AgentProcessTermination.Exited, 0, string.Empty);

        request.StandardOutputObserver?.Invoke(result.Stdout);

        return Task.FromResult(result);
    }

    private static AgentProcessResult Result(AgentProcessTermination termination, int? exitCode, string stdout) =>
        new()
        {
            Termination = termination,
            ExitCode = exitCode,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Stdout = stdout,
            Stderr = string.Empty,
            StartFailureMessage = termination == AgentProcessTermination.StartFailed
                ? "the executable could not be started"
                : null,
        };
}

/// <summary>Builds the provider event streams the adapter has to read.</summary>
public static class CodexStreams
{
    public const string Thread = "01a038c5-9427-72e0-a63b-a0c81310bdbe";

    public static string ThreadStarted(string id = Thread) =>
        "{\"type\":\"thread.started\",\"thread_id\":\"" + id + "\"}\n";

    public static string TurnStarted() => "{\"type\":\"turn.started\"}\n";

    public static string Message(string text) =>
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"agent_message\",\"text\":\""
        + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\"}}\n";

    public static string Command(string command, string status, int exitCode) =>
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_1\",\"type\":\"command_execution\",\"command\":\""
        + command + "\",\"status\":\"" + status + "\",\"exit_code\":" + exitCode + "}}\n";

    public static string TurnCompleted() =>
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":13047,\"cached_input_tokens\":6528,"
        + "\"output_tokens\":13,\"reasoning_output_tokens\":4}}\n";

    public static string TurnFailed(string message) =>
        "{\"type\":\"turn.failed\",\"error\":{\"message\":\"" + message + "\"}}\n";

    public static string Error(string message) =>
        "{\"type\":\"error\",\"message\":\"" + message + "\"}\n";

    /// <summary>The ordinary shape: a thread, a turn, an answer and a terminal event.</summary>
    public static string Success(string answer = "done") =>
        ThreadStarted() + TurnStarted() + Message(answer) + TurnCompleted();
}

/// <summary>Requests shaped the way the Phase 7 reviewer shapes them.</summary>
public static class CodexRequests
{
    public static AgentRunRequest Review(
        string workingDirectory,
        Func<AgentRunRequest, AgentRunRequest>? mutate = null,
        string systemPromptFile = @"C:\hub\review\reviewer.md")
    {
        var request = new AgentRunRequest
        {
            WorkingDirectory = workingDirectory,
            SystemPromptFile = systemPromptFile,
            UserPrompt = "Review the result state in your working directory.",
            PermissionProfile = AgentPermissionProfile.ReadOnly,
            ModelHint = "gpt-5.5",
            EffortHint = "high",
            EnvironmentAllowlist = ["PATH", "USERPROFILE", "SystemRoot", "OPENAI_API_KEY", "GITHUB_TOKEN"],
            HardTimeout = TimeSpan.FromMinutes(30),
            SoftTimeout = TimeSpan.FromMinutes(15),
            IdleTimeout = TimeSpan.FromMinutes(20),
            CorrelationId = "0199aa00-0000-7000-8000-000000000001",
            SessionId = "0199aa00-0000-7000-8000-000000000001",
        };

        return mutate is null ? request : mutate(request);
    }

    public static CodexOptions Options(string executablePath, string probeDirectory) => new()
    {
        ExecutablePath = executablePath,
        AcceptedVersion = "0.130.0",
        HealthProbeDirectory = probeDirectory,
    };
}
