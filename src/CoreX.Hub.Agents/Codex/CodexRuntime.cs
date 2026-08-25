using System.Text;
using CoreX.Hub.Agents.Abstractions;

namespace CoreX.Hub.Agents.Codex;

/// <summary>
/// The third <see cref="IAgentRuntime"/>: a second real provider, driven as a child process,
/// and deliberately able to do less than the first.
///
/// It reviews. It does not implement. A write capable request is refused by name before
/// anything is launched, because a provider that can edit the tree it is measuring is not a
/// reviewer, and quietly downgrading such a request would give the caller a run it did not
/// ask for.
///
/// It runs one exact build. The path is configuration and is never resolved on PATH, and the
/// version is checked rather than assumed - the release installed globally on a developer
/// machine is usually a different one, and on this provider the behaviour a review depends on
/// has moved between releases in both directions. "Whichever codex was first on PATH" is not
/// a runtime; it is a coin toss.
///
/// It claims nothing about containment. The provider's own restrictions are worth having and
/// are applied on every invocation, but what proves a review changed nothing is the Hub's
/// own comparison of the snapshot before and after, and what bounds it is the read-only
/// Windows sandbox the provider runs its commands under.
///
/// Authentication is out of band, exactly as it is for the other real runtime. There is no
/// key, no token and no account concept in this file.
/// </summary>
public sealed class CodexRuntime : IAgentRuntime
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly CodexOptions _options;
    private readonly IAgentProcessRunner _processes;

    public CodexRuntime(CodexOptions options, IAgentProcessRunner processes)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new ArgumentException("a runtime needs an absolute executable path.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.AcceptedVersion))
        {
            throw new ArgumentException("a pinned runtime needs the version it accepts.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.HealthProbeDirectory))
        {
            throw new ArgumentException("a runtime needs a directory to run its health probes in.", nameof(options));
        }
    }

    /// <summary>
    /// Declared against what this adapter implements, not against what the tool could do.
    ///
    /// StructuredSchema: yes. A supplied schema is materialised and handed to the provider,
    ///   and whatever structured document comes back is returned verbatim. Whether it
    ///   conforms is the caller's question, and no document is ever invented.
    /// ReadOnlyMode: yes, and it is the only mode. Anything else is refused before launch.
    /// Streaming: yes. Events are read while the run is still going.
    /// SessionResume: no. Every run is a new provider thread and no resume path exists, so a
    ///   resume identifier is refused rather than dropped.
    /// ToolAllowlist: no, and this is the interesting one. The run really does execute with
    ///   most of the provider's surface switched off - but the caller cannot state a list and
    ///   have it honoured, which is what this capability promises. Claiming it because the
    ///   adapter hardcodes its own set would be a lie told in the caller's direction.
    /// CostCap: no. The Hub bounds a run with concurrency and three deadlines; the provider's
    ///   reported usage is metadata and no ceiling could be enforced from here.
    /// </summary>
    public AgentCapabilities Describe() => new()
    {
        SupportsStructuredSchema = true,
        SupportsSessionResume = false,
        SupportsToolAllowlist = false,
        SupportsCostCap = false,
        SupportsStreaming = true,
        SupportsReadOnlyMode = true,
    };

    /// <summary>
    /// Asks the configured installation what it is. Local, read only and never a model call,
    /// so a health check costs nothing and works offline.
    ///
    /// It fails closed and it fails specifically, because "no executable there", "that is a
    /// different build" and "it would not answer" are three different problems.
    ///
    /// What it deliberately does not do is claim the session is usable. A local sign-in
    /// record is a file on disk, and this provider will happily report one whose token the
    /// server has already revoked - measured, not imagined. Only a real turn settles that.
    /// </summary>
    public async Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
        {
            return HealthResult.Unhealthy(
                $"the pinned executable '{_options.ExecutablePath}' does not exist; this runtime never falls "
                + "back to whichever build happens to be on PATH.");
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
                $"the pinned executable could not report its version ({Explain(version)})");
        }

        var reported = ReadVersion(version.Stdout);

        if (reported is null)
        {
            return HealthResult.Unhealthy("the pinned executable reported no recognisable version");
        }

        if (!string.Equals(reported, _options.AcceptedVersion, StringComparison.Ordinal))
        {
            return HealthResult.Unhealthy(
                $"the executable at '{_options.ExecutablePath}' reports version {reported}, and this runtime "
                + $"accepts {_options.AcceptedVersion} exactly. A different build is refused rather than "
                + "tried: the behaviour a review depends on has changed between releases.");
        }

        return HealthResult.Healthy($"pinned codex {reported} is installed and answers");
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        var startedAt = DateTimeOffset.UtcNow;

        if (PreflightRefusal(request) is { } preflight)
        {
            return Refused(preflight, sink, startedAt);
        }

        string prompt;

        try
        {
            prompt = CodexInvocationBuilder.ComposePrompt(
                ReadPromptFile(request.SystemPromptFile),
                ReadPromptFile(request.AppendSystemPromptFile),
                request.UserPrompt);
        }
        catch (IOException ex)
        {
            return Refused("the Hub owned instructions could not be read: " + ex.Message, sink, startedAt);
        }

        string? schemaFile = null;

        try
        {
            if (request.StructuredOutputSchema is { Length: > 0 } schema)
            {
                schemaFile = MaterialiseSchema(schema);
            }

            var plan = CodexInvocationBuilder.Plan(request, schemaFile, prompt);

            if (plan.IsRefused)
            {
                return Refused(plan.Refusal!, sink, startedAt);
            }

            return await RunPlannedAsync(request, plan.Invocation!, sink, cancellationToken);
        }
        catch (IOException ex)
        {
            return Refused("the output schema could not be written: " + ex.Message, sink, startedAt);
        }
        finally
        {
            // On every path: success, refusal, failure, timeout, cancellation. A schema left
            // behind is a file nobody owns, and this runtime wrote it.
            Discard(schemaFile, sink);
        }
    }

    private async Task<AgentRunResult> RunPlannedAsync(
        AgentRunRequest request,
        CodexInvocation invocation,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        var parser = new CodexEventStreamParser(sink);

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
                StandardOutputObserver = parser.Append,
            },
            cancellationToken);

        parser.Complete();
        var stream = parser.Snapshot();

        var outcome = Decide(process, stream, out var diagnostic);

        sink.Report(AgentEvent.Completed(outcome.ToString()));

        return new AgentRunResult
        {
            Outcome = outcome,
            ExitCode = process.ExitCode,

            // The provider's own thread, and only if it actually said one. Deriving it from
            // the Hub's correlation id would produce an identifier that looks like proof of
            // something nobody observed.
            ProviderSessionId = stream.ThreadId,

            Stdout = process.Stdout,
            Stderr = process.Stderr,

            // The final message is the structured document when a schema was supplied. It is
            // returned as it arrived; nothing is extracted from prose and nothing is invented.
            StructuredOutput = outcome == AgentRunOutcome.Completed
                               && !string.IsNullOrWhiteSpace(request.StructuredOutputSchema)
                ? stream.FinalMessage
                : null,

            Usage = stream.Usage,
            Duration = process.Duration,
            Diagnostic = diagnostic,
        };
    }

    /// <summary>
    /// What actually happened, decided in one place and in one order.
    ///
    /// The process comes first and always wins: a run that was cancelled, timed out or died
    /// is that, whatever the events before it looked like.
    ///
    /// Then the stream has to earn the result rather than merely fail to contradict it. An
    /// exit code of zero with no terminal event is not a completed turn - it is a run whose
    /// ending nobody can point to - and a thread the provider never named leaves the result
    /// unattributable. Both fail closed.
    /// </summary>
    private static AgentRunOutcome Decide(
        AgentProcessResult process,
        CodexStreamReport stream,
        out string? diagnostic)
    {
        var processOutcome = MapProcess(process);

        if (processOutcome != AgentRunOutcome.Completed)
        {
            diagnostic = Join(
                process.StartFailureMessage ?? $"the provider process ended as {processOutcome} ({Explain(process)})",
                Notes(stream));

            return processOutcome;
        }

        if (stream.TurnFailure is { } failure)
        {
            diagnostic = Join("the provider reported a failed turn: " + failure, Notes(stream));
            return AgentRunOutcome.Failed;
        }

        if (!stream.TurnCompleted)
        {
            diagnostic = Join(
                "the provider process exited cleanly but the stream carried no terminal event, so there is "
                + "no point at which this run can be said to have finished",
                Notes(stream));

            return AgentRunOutcome.Failed;
        }

        if (string.IsNullOrWhiteSpace(stream.ThreadId))
        {
            diagnostic = Join(
                "the provider never named the thread it ran, so the result cannot be attributed to one",
                Notes(stream));

            return AgentRunOutcome.Failed;
        }

        diagnostic = Notes(stream);
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

    /// <summary>
    /// Things worth saying about a stream that do not by themselves decide the outcome.
    ///
    /// Declined commands are the usual case rather than an alarm: the reviewer is meant to be
    /// unable to write, so a refused write is the boundary working. They are reported because
    /// a diagnosis without them reads as if nothing was attempted.
    /// </summary>
    private static string? Notes(CodexStreamReport stream)
    {
        var notes = new List<string>();

        if (stream.Errors.Count > 0)
        {
            notes.Add($"{stream.Errors.Count} provider error(s): {string.Join("; ", stream.Errors.Take(3))}");
        }

        var declined = stream.Commands.Count(c => !string.Equals(c.Status, "completed", StringComparison.Ordinal));

        if (stream.Commands.Count > 0)
        {
            notes.Add($"{stream.Commands.Count} command(s) attempted, {declined} not completed");
        }

        if (stream.UnknownEvents.Count > 0)
        {
            notes.Add("unmodelled event(s): " + string.Join(", ", stream.UnknownEvents));
        }

        if (stream.ParseDiagnostics.Count > 0)
        {
            notes.Add($"{stream.ParseDiagnostics.Count} unreadable line(s)");
        }

        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    /// <summary>
    /// The parts of a request that can only be checked against the filesystem.
    ///
    /// Kept apart from the invocation plan so the plan stays pure and the whole command
    /// surface remains assertable without touching a disk.
    /// </summary>
    private static string? PreflightRefusal(AgentRunRequest request)
    {
        if (CodexInvocationBuilder.Refuse(request) is { } refusal)
        {
            return refusal;
        }

        if (!string.IsNullOrWhiteSpace(request.SystemPromptFile) && !File.Exists(request.SystemPromptFile))
        {
            return $"the system prompt file '{request.SystemPromptFile}' does not exist; the Hub owned "
                   + "contract is the only source of the reviewer's instructions.";
        }

        if (!string.IsNullOrWhiteSpace(request.AppendSystemPromptFile)
            && !File.Exists(request.AppendSystemPromptFile))
        {
            return $"the append system prompt file '{request.AppendSystemPromptFile}' does not exist.";
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            return $"the working directory '{request.WorkingDirectory}' does not exist.";
        }

        return null;
    }

    /// <summary>
    /// Writes the caller's schema where the provider can read it.
    ///
    /// Outside the reviewed tree on purpose: a file inside the snapshot would be a change to
    /// the thing being reviewed, and the Hub compares that tree before and after.
    ///
    /// Without a byte order mark, measured rather than assumed: the pinned release refuses a
    /// schema file that begins with one, and says only that the json is invalid at line 1.
    /// </summary>
    private static string MaterialiseSchema(string schema)
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-hub-codex");
        Directory.CreateDirectory(root);

        var path = Path.Combine(root, "schema-" + Guid.CreateVersion7().ToString("N") + ".json");

        File.WriteAllText(path, schema, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    /// <summary>
    /// Removes what this runtime wrote. A tidy-up that fails is worth saying and is never
    /// allowed to change what the run was.
    /// </summary>
    private static void Discard(string? path, IProgress<AgentEvent> sink)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sink.Report(AgentEvent.Diagnostic($"the temporary schema '{path}' could not be removed: {ex.Message}"));
        }
    }

    private static string? ReadPromptFile(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : File.ReadAllText(path);

    private static AgentRunResult Refused(string reason, IProgress<AgentEvent> sink, DateTimeOffset startedAt)
    {
        sink.Report(AgentEvent.Diagnostic(reason));

        return new AgentRunResult
        {
            Outcome = AgentRunOutcome.Failed,
            Duration = DateTimeOffset.UtcNow - startedAt,
            Diagnostic = reason,
        };
    }

    private async Task<AgentProcessResult> ProbeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await _processes.RunAsync(
            new AgentProcessRequest
            {
                Executable = _options.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = _options.HealthProbeDirectory,
                EnvironmentAllowlist = CodexInvocationBuilder.PermittedEnvironmentNames,
                HardTimeout = HealthProbeTimeout,
            },
            cancellationToken);

    /// <summary>Reads "codex-cli 0.130.0" without caring what surrounds it.</summary>
    private static string? ReadVersion(string stdout)
    {
        foreach (var token in (stdout ?? string.Empty).Split(
                     [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 0 && char.IsDigit(token[0]) && token.Contains('.', StringComparison.Ordinal))
            {
                return token.Trim();
            }
        }

        return null;
    }

    private static string Explain(AgentProcessResult process) =>
        $"{process.Termination}, exit {process.ExitCode?.ToString() ?? "none"}";

    private static string? Join(string? first, string? second) => (first, second) switch
    {
        (null, null) => null,
        (null, { } only) => only,
        ({ } only, null) => only,
        var (a, b) => a + "; " + b,
    };
}
