using System.Globalization;
using System.Text.Json;
using CoreX.Hub.Agents.Abstractions;

namespace CoreX.Hub.Agents.ClaudeCode;

/// <summary>
/// The second <see cref="IAgentRuntime"/>: a real provider driven as a child process
/// (ADR-0009).
///
/// It is an adapter and nothing more. It does not manage a workspace, does not touch git,
/// does not decide whether work is acceptable and never learns what the execution is for.
/// Everything it reports is machine truth about one child process and the event stream
/// that process produced; every judgement about that run is made afterwards, elsewhere,
/// from Hub observed facts.
///
/// Three things are worth reading before changing anything here.
///
/// The tool surface is frozen (ADR-0020) and has no configuration path. There is no shell,
/// and there is no string in any file that could add one.
///
/// The provider's tool restrictions are not the security boundary and are not claimed as
/// one. Reducing what the agent can do is worth having, but what actually contains this
/// run is the Hub's own machinery: an empty based child environment, the managed worktree,
/// the scope and secret gates, the policy gate, deterministic verification and the
/// evidence trail. None of those were relaxed because the shell went away.
///
/// Authentication is out of band. There is no key, no token, no endpoint and no account
/// concept anywhere in this file; the installed tool already holds a workstation session
/// and the Hub does not read, copy, store or replace it.
/// </summary>
public sealed class ClaudeCodeRuntime : IAgentRuntime
{
    /// <summary>The lowest release whose command surface this adapter was written against.</summary>
    public static Version MinimumVersion { get; } = new(2, 1);

    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly ClaudeCodeOptions _options;
    private readonly IAgentProcessRunner _processes;

    public ClaudeCodeRuntime(ClaudeCodeOptions options, IAgentProcessRunner processes)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new ArgumentException("a runtime needs an absolute executable path.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.HealthProbeDirectory))
        {
            throw new ArgumentException("a runtime needs a directory to run its health probes in.", nameof(options));
        }
    }

    /// <summary>
    /// Declared against what this adapter implements today, not against what the tool it
    /// drives might be able to do.
    ///
    /// Streaming: yes. The event stream is read as it arrives and reported to the progress
    ///   channel while the run is still going, rather than being reconstructed at the end.
    /// ToolAllowlist: yes. The caller cannot choose the list - it is frozen - but the run
    ///   genuinely executes with a restricted tool surface, which is what the capability
    ///   says.
    /// CostCap: no. The Hub bounds a run with concurrency, counters and three deadlines,
    ///   which are provider neutral. The provider's own spend flag is not used and the
    ///   cost it reports afterwards is metadata, so no ceiling could be enforced from here.
    /// SessionResume: no. Session persistence is deliberately off and no resume path is
    ///   implemented, so a resume identifier is refused before launch instead of being
    ///   dropped.
    /// StructuredSchema: yes. A supplied schema is passed to the provider and the structured
    ///   result it returns is handed back verbatim. What this runtime does not do is decide
    ///   whether that result conforms - the caller supplied the schema and owns that - and it
    ///   never invents a document when the provider returned none. Writing structured output
    ///   to a caller chosen path is a different feature and is still refused by name.
    /// ReadOnlyMode: yes. A read only request runs with reading tools and plan permission,
    ///   and the provider's own report of both is checked before the run is believed. It is
    ///   defence in depth and not the boundary: what proves a reviewer changed nothing is the
    ///   snapshot's tree, compared before and after.
    /// </summary>
    public AgentCapabilities Describe() => new()
    {
        SupportsStructuredSchema = true,
        SupportsSessionResume = false,
        SupportsToolAllowlist = true,
        SupportsCostCap = false,
        SupportsStreaming = true,
        SupportsReadOnlyMode = true,
    };

    /// <summary>
    /// Asks the local installation about itself. Local and read only: it runs no model, so
    /// a health check never costs anything and never depends on the network being useful.
    ///
    /// It fails closed and it fails specifically. "Not installed", "too old to have the
    /// command surface this adapter uses", "nobody is signed in" and "signed in through a
    /// route this runtime does not use" are four different problems with four different
    /// answers, and reporting them as one unhealthy runtime would waste the diagnosis.
    ///
    /// Nothing identifying is reported. Whether a session exists and how it authenticates
    /// is what health means; who it belongs to is not, so the address and the organisation
    /// the probe can see are read past and never written down.
    /// </summary>
    public async Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
        {
            return HealthResult.Unhealthy($"the executable '{_options.ExecutablePath}' does not exist");
        }

        if (!Directory.Exists(_options.HealthProbeDirectory))
        {
            return HealthResult.Unhealthy(
                $"the health probe directory '{_options.HealthProbeDirectory}' does not exist");
        }

        var version = await ProbeAsync(["--version"], cancellationToken);

        if (version.Termination != AgentProcessTermination.Exited || version.ExitCode != 0)
        {
            return HealthResult.Unhealthy(
                "the installed executable could not report its version "
                + $"({Explain(version)})");
        }

        if (ReadVersion(version.Stdout) is not { } installed)
        {
            return HealthResult.Unhealthy("the installed executable reported no recognisable version");
        }

        if (installed < MinimumVersion)
        {
            return HealthResult.Unhealthy(
                $"version {installed} is older than {MinimumVersion}, which is the release this adapter's "
                + "command surface was written against");
        }

        var auth = await ProbeAsync(["auth", "status", "--json"], cancellationToken);

        if (auth.Termination != AgentProcessTermination.Exited || auth.ExitCode != 0)
        {
            return HealthResult.Unhealthy($"authentication status could not be read ({Explain(auth)})");
        }

        return ReadAuthentication(auth.Stdout, installed);
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        var startedAt = DateTimeOffset.UtcNow;

        if (PreflightRefusal(request) is { } refusal)
        {
            // Nothing was launched, so there is no process truth to report and nothing was
            // half done. Refusing here is the whole point of refusing before launch.
            sink.Report(AgentEvent.Diagnostic(refusal));

            return new AgentRunResult
            {
                Outcome = AgentRunOutcome.Failed,
                Duration = DateTimeOffset.UtcNow - startedAt,
                Diagnostic = refusal,
            };
        }

        var plan = ClaudeCodeInvocationBuilder.Plan(request);

        if (plan.IsRefused)
        {
            sink.Report(AgentEvent.Diagnostic(plan.Refusal!));

            return new AgentRunResult
            {
                Outcome = AgentRunOutcome.Failed,
                Duration = DateTimeOffset.UtcNow - startedAt,
                Diagnostic = plan.Refusal,
            };
        }

        var invocation = plan.Invocation!;
        var parser = new ClaudeStreamJsonParser(sink);

        sink.Report(AgentEvent.Started($"provider run for session {invocation.SessionId}"));

        var process = await _processes.RunAsync(
            new AgentProcessRequest
            {
                Executable = _options.ExecutablePath,
                Arguments = invocation.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                EnvironmentAllowlist = invocation.EnvironmentAllowlist,
                SoftTimeout = request.SoftTimeout,
                IdleTimeout = request.IdleTimeout,
                HardTimeout = request.HardTimeout,

                // The prompt never appears in the argument vector.
                StandardInputText = invocation.StandardInputText,

                // Read while the run is still going. It also sees output the capture
                // ceiling would have dropped, so a long run is still fully understood.
                StandardOutputObserver = parser.Append,
            },
            cancellationToken);

        parser.Complete();
        var stream = parser.Snapshot();

        var expectedTools = ClaudeCodeInvocationBuilder.ExpectedEffectiveTools(
            request.PermissionProfile, !string.IsNullOrWhiteSpace(request.StructuredOutputSchema));

        var expectedPermissionMode = ClaudeCodeInvocationBuilder.PermissionModeFor(request.PermissionProfile);

        var outcome = Decide(
            process, stream, invocation, expectedTools, expectedPermissionMode, out var diagnostic);

        sink.Report(AgentEvent.Completed(outcome.ToString()));

        return new AgentRunResult
        {
            Outcome = outcome,
            ExitCode = process.ExitCode,

            // Stated only when the provider's own session matched the one that was asked
            // for. An unverified identifier is worse than none: it would look like proof.
            ProviderSessionId = SessionVerified(stream, invocation) ? invocation.SessionId : null,

            // The raw streams as captured, whatever the parser made of them. Evidence
            // survives a stream this adapter could not read (ADR-0009).
            Stdout = process.Stdout,
            Stderr = process.Stderr,

            // Verbatim, and null when the provider produced none. Deciding what a missing
            // structured result means belongs to whoever supplied the schema.
            StructuredOutput = stream.Result?.StructuredOutput,
            Usage = stream.Result?.Usage ?? AgentUsageMetadata.Empty,
            Duration = process.Duration,
            Diagnostic = diagnostic,
        };
    }

    /// <summary>
    /// The parts of a request that can only be checked against the filesystem.
    ///
    /// Kept apart from the invocation plan so that the plan stays pure and the entire
    /// command surface remains assertable without touching a disk.
    /// </summary>
    private static string? PreflightRefusal(AgentRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SystemPromptFile) && !File.Exists(request.SystemPromptFile))
        {
            return $"the system prompt file '{request.SystemPromptFile}' does not exist; the Hub context pack "
                   + "is the only source of the execution contract.";
        }

        if (!string.IsNullOrWhiteSpace(request.AppendSystemPromptFile)
            && !File.Exists(request.AppendSystemPromptFile))
        {
            return $"the append system prompt file '{request.AppendSystemPromptFile}' does not exist.";
        }

        return null;
    }

    /// <summary>
    /// What actually happened, decided in one place and in one order.
    ///
    /// The process comes first and always wins. A run that was cancelled, ran out of time
    /// or died is that, however encouraging the events it emitted beforehand were: a
    /// success shaped event in the middle of a stream is not a finished run, and letting
    /// late text overwrite process truth is how a killed agent gets recorded as a passing
    /// one.
    ///
    /// Only once the process is known to have exited cleanly does the stream get a say, and
    /// then it has to earn the result rather than merely fail to contradict it. A missing
    /// final event, an unreadable line, a session that is not the one that was asked for, a
    /// permission mode or tool surface other than the one stated, or a context server that
    /// became active are each enough on their own. None of them is repairable here and none
    /// of them is worth guessing about.
    /// </summary>
    private static AgentRunOutcome Decide(
        AgentProcessResult process,
        ClaudeStreamReport stream,
        ClaudeCodeInvocation invocation,
        IReadOnlyList<string> expectedTools,
        string expectedPermissionMode,
        out string? diagnostic)
    {
        var processOutcome = MapProcess(process);

        if (processOutcome != AgentRunOutcome.Completed)
        {
            diagnostic = Join(
                process.StartFailureMessage ?? $"the provider process ended as {processOutcome} ({Explain(process)})",
                StreamNotes(stream));

            return processOutcome;
        }

        if (Reject(stream, invocation, expectedTools, expectedPermissionMode) is { } rejection)
        {
            diagnostic = Join(rejection, StreamNotes(stream));
            return AgentRunOutcome.Failed;
        }

        diagnostic = StreamNotes(stream);
        return AgentRunOutcome.Completed;
    }

    private static AgentRunOutcome MapProcess(AgentProcessResult process) => process.Termination switch
    {
        AgentProcessTermination.StartFailed => AgentRunOutcome.Crashed,
        AgentProcessTermination.Cancelled => AgentRunOutcome.Cancelled,
        AgentProcessTermination.TimedOut => AgentRunOutcome.TimedOut,
        _ => process.ExitCode switch
        {
            0 => AgentRunOutcome.Completed,
            < 0 => AgentRunOutcome.Crashed,
            null => AgentRunOutcome.Crashed,
            _ => AgentRunOutcome.Failed,
        },
    };

    /// <summary>Why an otherwise clean exit is still not a completed run, or null.</summary>
    private static string? Reject(
        ClaudeStreamReport stream,
        ClaudeCodeInvocation invocation,
        IReadOnlyList<string> expectedTools,
        string expectedPermissionMode)
    {
        if (stream.ParseDiagnostics.Count > 0)
        {
            return "the provider event stream could not be read in full, so it is not evidence of anything: "
                   + string.Join("; ", stream.ParseDiagnostics);
        }

        if (stream.Init is not { } init)
        {
            return "the provider emitted no session init event.";
        }

        if (!string.Equals(init.SessionId, invocation.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            return $"the provider started session '{init.SessionId}' rather than the requested "
                   + $"'{invocation.SessionId}'.";
        }

        if (!string.Equals(init.PermissionMode, expectedPermissionMode, StringComparison.Ordinal))
        {
            return $"the provider ran in permission mode '{init.PermissionMode}' rather than the stated "
                   + $"'{expectedPermissionMode}'.";
        }

        if (init.McpServerCount != 0)
        {
            return $"{init.McpServerCount.ToString(CultureInfo.InvariantCulture)} model context servers became "
                   + "active; this runtime declares none.";
        }

        if (!SameTools(init.Tools, expectedTools))
        {
            return $"the provider reported the tool surface [{string.Join(", ", init.Tools)}] rather than the "
                   + $"expected [{string.Join(", ", expectedTools)}].";
        }

        if (stream.Result is not { } result)
        {
            return "the provider exited cleanly but emitted no final result event, so there is no structurally "
                   + "valid account of how the run ended.";
        }

        if (result.SessionId is { Length: > 0 }
            && !string.Equals(result.SessionId, invocation.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            return $"the final result names session '{result.SessionId}' rather than the requested "
                   + $"'{invocation.SessionId}'.";
        }

        if (result.IsError != false)
        {
            return $"the provider reported a failed result (subtype '{result.Subtype}', "
                   + $"terminal reason '{result.TerminalReason}', stop reason '{result.StopReason}').";
        }

        if (!string.Equals(result.Subtype, "success", StringComparison.Ordinal))
        {
            return $"the final result is of subtype '{result.Subtype}', which is not a successful terminal "
                   + "result.";
        }

        return null;
    }

    private static bool SameTools(IReadOnlyList<string> reported, IReadOnlyList<string> expected) =>
        reported.Count == expected.Count
        && expected.All(t => reported.Contains(t, StringComparer.Ordinal));

    private static bool SessionVerified(ClaudeStreamReport stream, ClaudeCodeInvocation invocation) =>
        stream.Init is { SessionId: { Length: > 0 } observed }
        && string.Equals(observed, invocation.SessionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Things worth recording that are not by themselves a verdict: a rate limit status
    /// event, or an event type this parser does not model yet.
    /// </summary>
    private static string? StreamNotes(ClaudeStreamReport stream)
    {
        var notes = new List<string>();

        if (stream.RateLimitEventCount > 0)
        {
            notes.Add(
                $"{stream.RateLimitEventCount.ToString(CultureInfo.InvariantCulture)} rate limit status events");
        }

        if (stream.UnknownEventTypes.Count > 0)
        {
            notes.Add("unmodelled event types: " + string.Join(", ", stream.UnknownEventTypes));
        }

        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (null, { } only) => only,
            ({ } only, null) => only,
            var (a, b) => $"{a} ({b})",
        };

    private static string Explain(AgentProcessResult process) =>
        $"termination {process.Termination}, exit code "
        + (process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none");

    private async Task<AgentProcessResult> ProbeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await _processes.RunAsync(
            new AgentProcessRequest
            {
                Executable = _options.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = _options.HealthProbeDirectory,
                EnvironmentAllowlist = ClaudeCodeInvocationBuilder.RequiredEnvironmentNames,
                HardTimeout = HealthProbeTimeout,
            },
            cancellationToken);

    /// <summary>Reads the leading major.minor of a version banner, or null.</summary>
    private static Version? ReadVersion(string output)
    {
        var token = output.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.Length > 0 && char.IsAsciiDigit(t[0]));

        if (token is null)
        {
            return null;
        }

        var parts = token.Split('.');

        return parts.Length >= 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? new Version(major, minor)
            : null;
    }

    /// <summary>
    /// Turns the authentication probe into a verdict without recording who is signed in.
    ///
    /// The route matters because this runtime has exactly one: the workstation session the
    /// user already established. A machine signed in some other way would run, and would be
    /// billed some other way, which is precisely the surprise fail closed exists to prevent.
    /// </summary>
    private static HealthResult ReadAuthentication(string output, Version installed)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return HealthResult.Unhealthy("the authentication status could not be read as json");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return HealthResult.Unhealthy("the authentication status was not an object");
            }

            var loggedIn = root.TryGetProperty("loggedIn", out var flag)
                           && flag.ValueKind == JsonValueKind.True;

            if (!loggedIn)
            {
                return HealthResult.Unhealthy(
                    "no workstation session is signed in; this runtime uses the existing session and "
                    + "configures no alternative");
            }

            var method = root.TryGetProperty("authMethod", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            var provider = root.TryGetProperty("apiProvider", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

            if (!string.Equals(method, "claude.ai", StringComparison.Ordinal)
                || !string.Equals(provider, "firstParty", StringComparison.Ordinal))
            {
                return HealthResult.Unhealthy(
                    $"the signed in session authenticates as '{method}' through '{provider}', which is not the "
                    + "workstation session route this runtime is accepted for");
            }

            return HealthResult.Healthy(
                $"version {installed}, signed in as '{method}' through '{provider}'");
        }
    }
}
