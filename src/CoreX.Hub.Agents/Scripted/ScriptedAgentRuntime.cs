using System.Globalization;
using CoreX.Hub.Agents.Abstractions;

namespace CoreX.Hub.Agents.Scripted;

/// <summary>
/// The deterministic, offline agent runtime (architecture F.2).
///
/// It exists so the orchestration pipeline can be exercised without a network, an AI, a
/// cloud account, provider authentication, billing or nondeterministic model output. It
/// implements exactly the same IAgentRuntime contract a real provider will, gets no
/// privileged access to application services, and knows nothing about tasks, executions
/// or acceptance.
///
/// All behaviour lives in the fixture executable it drives, exactly as a real adapter
/// drives a vendor CLI. That is deliberate: it keeps git behaviour, file writes and
/// crashes in the harness instead of turning this runtime into a workspace manager
/// (E.1 #5).
/// </summary>
public sealed class ScriptedAgentRuntime : IAgentRuntime
{
    private const string AgentVerb = "agent";

    private readonly ScriptedAgentOptions _options;
    private readonly IAgentProcessRunner _processes;

    public ScriptedAgentRuntime(ScriptedAgentOptions options, IAgentProcessRunner processes)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));

        if (string.IsNullOrWhiteSpace(options.FixtureExecutablePath))
        {
            throw new ArgumentException("A scripted runtime needs a fixture executable.", nameof(options));
        }
    }

    public ScriptedAgentMode Mode => _options.Mode;

    /// <summary>
    /// Declared honestly, capability by capability, because a runtime that claimed
    /// everything would hide exactly the gaps the Unsupported result exists to surface.
    ///
    /// StructuredSchema: no. It writes a fixed result document when a path is given, but it
    ///   never reads AgentRunRequest.StructuredOutputSchema and never checks its output
    ///   against one. Writing to a path is not schema support, and declaring it as such
    ///   would let a caller believe a schema had been honoured when nothing looked at it.
    /// SessionResume: no, there is no session and nothing to resume.
    /// ToolAllowlist: no, it uses no tools, so restricting them would mean nothing.
    /// CostCap: no, nothing here costs anything, so a cap could not be honoured or refused.
    /// Streaming: no, output is captured and reported at the end, not incrementally.
    /// ReadOnlyMode: no, it cannot be constrained to change nothing; that is what NoOp is.
    ///
    /// A caller needing a schema honoured therefore gets Unsupported rather than a
    /// plausible looking document nothing validated.
    /// </summary>
    public AgentCapabilities Describe() => AgentCapabilities.None;

    /// <summary>
    /// Deterministic and offline. It checks only what this runtime genuinely needs - that
    /// the fixture it drives exists - and never probes for a vendor CLI or a network.
    /// </summary>
    public Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(File.Exists(_options.FixtureExecutablePath)
            ? HealthResult.Healthy($"scripted runtime ready in {_options.Mode} mode")
            : HealthResult.Unhealthy($"the fixture executable '{_options.FixtureExecutablePath}' does not exist"));
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        sink.Report(AgentEvent.Started($"scripted runtime, mode {_options.Mode}"));

        var arguments = new List<string> { AgentVerb, ModeArgument(_options.Mode) };

        if (!string.IsNullOrWhiteSpace(request.StructuredOutputPath))
        {
            arguments.Add("--structured-output");
            arguments.Add(request.StructuredOutputPath!);
        }

        var process = await _processes.RunAsync(
            new AgentProcessRequest
            {
                Executable = _options.FixtureExecutablePath,
                Arguments = arguments,
                WorkingDirectory = request.WorkingDirectory,
                EnvironmentAllowlist = request.EnvironmentAllowlist,
                SoftTimeout = request.SoftTimeout,
                IdleTimeout = request.IdleTimeout,
                HardTimeout = request.HardTimeout,
            },
            cancellationToken);

        foreach (var line in process.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            sink.Report(AgentEvent.Output(line.TrimEnd()));
        }

        var structured = await ReadStructuredOutputAsync(request, sink, cancellationToken);
        var outcome = MapOutcome(process);

        sink.Report(AgentEvent.Completed(outcome.ToString()));

        return new AgentRunResult
        {
            Outcome = outcome,
            ExitCode = process.ExitCode,
            StructuredOutput = structured,
            Stdout = process.Stdout,
            Stderr = process.Stderr,
            Duration = process.Duration,
            Diagnostic = process.StartFailureMessage ?? Explain(outcome, process),
        };
    }

    private string? Explain(AgentRunOutcome outcome, AgentProcessResult process) =>
        outcome == AgentRunOutcome.Completed
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{_options.Mode} ended as {outcome} with exit code {process.ExitCode}");

    /// <summary>
    /// A process that exited tells us its code; anything else is the runner's machine
    /// truth. A negative exit code means the child died rather than exiting, which is a
    /// crash and not merely a failure.
    /// </summary>
    private static AgentRunOutcome MapOutcome(AgentProcessResult process) => process.Termination switch
    {
        AgentProcessTermination.StartFailed => AgentRunOutcome.Crashed,
        AgentProcessTermination.Cancelled => AgentRunOutcome.Cancelled,
        AgentProcessTermination.TimedOut => AgentRunOutcome.TimedOut,
        _ => process.ExitCode switch
        {
            0 => AgentRunOutcome.Completed,
            < 0 => AgentRunOutcome.Crashed,
            _ => AgentRunOutcome.Failed,
        },
    };

    private static async Task<string?> ReadStructuredOutputAsync(
        AgentRunRequest request,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StructuredOutputPath)
            || !File.Exists(request.StructuredOutputPath))
        {
            return null;
        }

        sink.Report(AgentEvent.Structured(request.StructuredOutputPath!));
        return await File.ReadAllTextAsync(request.StructuredOutputPath!, cancellationToken);
    }

    private static string ModeArgument(ScriptedAgentMode mode) => mode switch
    {
        ScriptedAgentMode.NoOp => "no-op",
        ScriptedAgentMode.ApplyPatch => "apply-patch",
        ScriptedAgentMode.OutOfScopeWrite => "out-of-scope-write",
        ScriptedAgentMode.SecretWrite => "secret-write",
        ScriptedAgentMode.PartialWrite => "partial-write",
        ScriptedAgentMode.Hang => "hang",
        ScriptedAgentMode.Crash => "crash",
        ScriptedAgentMode.UnexpectedCommit => "unexpected-commit",
        ScriptedAgentMode.UnexpectedCommitThenCrash => "unexpected-commit-then-crash",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown scripted mode"),
    };
}
