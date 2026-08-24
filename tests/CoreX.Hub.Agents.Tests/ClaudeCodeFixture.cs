using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.ClaudeCode;
using CoreX.Hub.Cli.Agents;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// A disposable layout plus the real runtime wired to the real process runner through the
/// real composition adapter.
///
/// The only thing swapped out is the executable at the end of it, and that is the point:
/// argument vector, child environment, stdin, job object, timeouts and output capture are
/// all the production ones, so what these tests observe is what the operating system would
/// hand the real tool.
///
///   root/
///     work/      the agent's working directory
///     outside/   a sibling it has no business writing to
///     context/   Hub owned prompt files
///     control/   Hub owned git control files
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeFixture : IDisposable
{
    public const string FixtureExecutableName = "corex-claude-fixture.exe";

    /// <summary>Written into the working directory to choose which stream shape is emitted.</summary>
    public const string ScenarioFileName = ".claude-scenario";

    public const string ArgumentsFileName = ".claude-argv.txt";

    public const string StandardInputFileName = ".claude-stdin.txt";

    private ClaudeCodeFixture(string root)
    {
        Root = root;
        WorkPath = Path.Combine(root, "work");
        OutsidePath = Path.Combine(root, "outside");
        ContextPath = Path.Combine(root, "context");
        ControlPath = Path.Combine(root, "control");

        Directory.CreateDirectory(WorkPath);
        Directory.CreateDirectory(OutsidePath);
        Directory.CreateDirectory(ContextPath);

        SystemPromptPath = Path.Combine(ContextPath, "system.md");
        AppendPromptPath = Path.Combine(ContextPath, "rules.md");

        File.WriteAllText(SystemPromptPath, "# hub execution contract\n");
        File.WriteAllText(AppendPromptPath, "# project rules\n");

        var hooks = HubHooks.Materialise(ControlPath);

        // The trust root is the whole layout, exactly as production uses the workspace root
        // rather than one worktree: the sibling directory has to be reachable by the runner
        // for a containment claim about it to mean anything.
        Processes = new ProcessAgentRunner(Root, hooks.EmptyGlobalConfigPath);
    }

    public string Root { get; }

    public string WorkPath { get; }

    public string OutsidePath { get; }

    public string ContextPath { get; }

    public string ControlPath { get; }

    public string SystemPromptPath { get; }

    public string AppendPromptPath { get; }

    public ProcessAgentRunner Processes { get; }

    public static string FixtureExecutable { get; } = Resolve();

    public static ClaudeCodeFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-claude-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return new ClaudeCodeFixture(root);
    }

    /// <summary>The runtime under test, driving the deterministic stand-in executable.</summary>
    public ClaudeCodeRuntime Runtime(string? executablePath = null) =>
        new(
            new ClaudeCodeOptions
            {
                ExecutablePath = executablePath ?? FixtureExecutable,
                HealthProbeDirectory = WorkPath,
            },
            Processes);

    public void UseScenario(string scenario) =>
        File.WriteAllText(Path.Combine(WorkPath, ScenarioFileName), scenario);

    /// <summary>The argument vector the operating system actually delivered.</summary>
    public IReadOnlyList<string> DeliveredArguments() =>
        File.Exists(Path.Combine(WorkPath, ArgumentsFileName))
            ? File.ReadAllLines(Path.Combine(WorkPath, ArgumentsFileName))
            : [];

    public string DeliveredStandardInput() =>
        File.Exists(Path.Combine(WorkPath, StandardInputFileName))
            ? File.ReadAllText(Path.Combine(WorkPath, StandardInputFileName))
            : string.Empty;

    public AgentRunRequest Request(
        string? sessionId = null,
        string? model = "claude-opus-5",
        string? effort = "high",
        string? appendPromptFile = null,
        string? resumeSessionId = null,
        string? structuredOutputSchema = null,
        string? structuredOutputPath = null,
        AgentPermissionProfile permissionProfile = AgentPermissionProfile.WorkspaceWrite,
        IReadOnlyList<string>? environmentAllowlist = null,
        TimeSpan? hardTimeout = null,
        string? userPrompt = null,
        string? systemPromptFile = null) => new()
    {
        WorkingDirectory = WorkPath,
        SystemPromptFile = systemPromptFile ?? SystemPromptPath,
        AppendSystemPromptFile = appendPromptFile ?? AppendPromptPath,
        UserPrompt = userPrompt ?? "write a sentinel file",
        PermissionProfile = permissionProfile,
        ModelHint = model,
        EffortHint = effort,
        StructuredOutputSchema = structuredOutputSchema,
        StructuredOutputPath = structuredOutputPath,
        EnvironmentAllowlist = environmentAllowlist ?? DefaultEnvironmentAllowlist,
        HardTimeout = hardTimeout ?? TimeSpan.FromSeconds(60),
        CorrelationId = "HUB-001#1",
        SessionId = sessionId ?? Guid.NewGuid().ToString("D"),
        ResumeSessionId = resumeSessionId,
    };

    /// <summary>
    /// What a real organization grants its managed children (ADR-0007).
    ///
    /// Deliberately the production shape, which is wider than this runtime needs: the same
    /// list has to serve a .NET verification toolchain, whose NuGet restore wants APPDATA
    /// and a ProgramFiles. The adapter narrows to its own ceiling, and tests that assert on
    /// the child environment are asserting on that narrowing rather than on a list invented
    /// to match it.
    /// </summary>
    public static IReadOnlyList<string> DefaultEnvironmentAllowlist { get; } =
    [
        "SystemRoot", "windir", "SystemDrive", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE",
        "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "NUMBER_OF_PROCESSORS",
    ];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Resolve()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, FixtureExecutableName);

        return File.Exists(candidate)
            ? candidate
            : throw new InvalidOperationException($"the fixture executable was not found ({candidate})");
    }
}

/// <summary>Records the progress stream with arrival times, so streaming can be proved.</summary>
internal sealed class TimedEventCollector : IProgress<AgentEvent>
{
    private readonly List<(AgentEvent Event, DateTimeOffset At)> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(AgentEvent Event, DateTimeOffset At)> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public void Report(AgentEvent value)
    {
        lock (_gate)
        {
            _events.Add((value, DateTimeOffset.UtcNow));
        }
    }
}
