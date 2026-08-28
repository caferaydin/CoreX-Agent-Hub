namespace CoreX.Hub.Agents.Abstractions;

/// <summary>
/// One agent invocation, described without reference to where it will run.
///
/// A runtime says what it would execute; somebody else decides where. Nothing in this record can
/// express a boundary - there is no namespace, no sandbox, no host - which is exactly the point:
/// a runtime cannot ask to be isolated less, because it has no vocabulary for the question.
/// </summary>
public sealed record HardAgentCommand
{
    /// <summary>An absolute path to the executable, inside whatever environment runs it.</summary>
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// The whole environment, stated explicitly rather than as an allowlist.
    ///
    /// An allowlist selects from the parent's environment, and an isolated workload has no parent
    /// worth inheriting from. What the process gets is what a service of that account gets, plus
    /// these - so there is no host value for a name to accidentally let through.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? StdinPayload { get; init; }

    /// <summary>
    /// A file the runtime wants read back after the process ends, when it wants one.
    ///
    /// Named rather than read, because the file may not be on the same machine as the Hub. The
    /// caller fetches it across whatever boundary it imposed and hands the content back through
    /// <see cref="HardAgentProcessResult.StructuredOutput"/>, so a runtime's structured output
    /// contract survives being run somewhere it cannot reach.
    /// </summary>
    public string? StructuredOutputPath { get; init; }
}

/// <summary>
/// What happened to an agent process, in the vocabulary a runtime already uses.
///
/// Deliberately the same shape a runtime gets from its own process runner. Interpretation - which
/// exit code means crashed rather than failed, what the diagnostic should say, how the structured
/// document is validated - stays with the runtime that owns those rules, so running somewhere else
/// changes where the process ran and nothing about what its result means.
/// </summary>
public sealed record HardAgentProcessResult
{
    public required AgentProcessTermination Termination { get; init; }

    public int? ExitCode { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public required TimeSpan Duration { get; init; }

    /// <summary>Populated only when the process never started.</summary>
    public string? StartFailureMessage { get; init; }

    /// <summary>
    /// The content of <see cref="HardAgentCommand.StructuredOutputPath"/>, already fetched.
    ///
    /// Null both when none was asked for and when the file did not appear, which are the same
    /// thing to every runtime that reads one: no structured result was produced.
    /// </summary>
    public string? StructuredOutput { get; init; }
}

/// <summary>
/// A runtime that can be run somewhere it did not choose.
///
/// The seam that makes provider isolation possible without teaching providers about isolation. A
/// runtime implementing this splits what it already does into two halves - decide the command,
/// interpret the result - and lets a trusted caller put a boundary between them.
///
/// Implementing it is what makes a runtime eligible for hard execution. A runtime that does not is
/// not run on the host instead: the execution is refused, because the alternative is a hard
/// organization silently getting a logical run, which is the failure this whole phase exists to
/// prevent. Compatibility is therefore a type test the caller can make before anything starts,
/// rather than a capability flag a runtime could get wrong about itself.
///
/// It is deliberately separate from <see cref="IAgentRuntime"/> rather than a fourth member of it.
/// The run contract is frozen, and most runtimes cannot honestly implement this one: a runtime
/// that owns its own process launch, session handling and streaming cannot describe a command and
/// walk away from it without being rewritten.
/// </summary>
public interface IHardCapableAgentRuntime
{
    /// <summary>What this runtime would execute for the request.</summary>
    HardAgentCommand DescribeCommand(AgentRunRequest request);

    /// <summary>
    /// The runtime's own result, built from a process it did not launch.
    ///
    /// Every field the ordinary path would have produced is produced here by the same code, which
    /// is what makes an isolated run and a host run comparable rather than merely similar.
    /// </summary>
    AgentRunResult Interpret(AgentRunRequest request, HardAgentProcessResult process);
}

/// <summary>
/// Whether a runtime can run inside a hard boundary.
///
/// A type test rather than a declared capability, and deliberately: a flag is something a runtime
/// can be wrong about, while implementing the seam is something it either did or did not do. Asked
/// during admission, before an execution row exists, so an incompatible combination is refused
/// rather than discovered once an agent was supposed to start.
/// </summary>
public static class HardAgentRuntimeCompatibility
{
    public static bool IsHardCapable(IAgentRuntime runtime) => runtime is IHardCapableAgentRuntime;

    /// <summary>The seam, or null when this runtime does not implement it.</summary>
    public static IHardCapableAgentRuntime? AsHardCapable(IAgentRuntime runtime) =>
        runtime as IHardCapableAgentRuntime;
}
