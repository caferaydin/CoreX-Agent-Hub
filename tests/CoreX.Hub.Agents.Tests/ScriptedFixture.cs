using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Composition.Agents;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// A disposable workspace plus a scripted runtime wired to the real ProcessRunner through
/// the composition adapter. No network, no AI, no provider account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScriptedFixture : IDisposable
{
    public const string FixtureExecutableName = "corex-fixture.exe";

    private ScriptedFixture(string root)
    {
        Root = root;
        WorkspaceRoot = Path.Combine(root, "workspace");
        ContextRoot = Path.Combine(root, "context");
        GitControlRoot = Path.Combine(root, "git-control");

        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(ContextRoot);

        File.WriteAllText(Path.Combine(ContextRoot, "system.md"), "# contract\n");

        // The same Hub owned control files production materialises, so the adapter under
        // test is wired the way it is wired for real.
        var hooks = HubHooks.Materialise(GitControlRoot);

        Processes = new ProcessAgentRunner(WorkspaceRoot, hooks.EmptyGlobalConfigPath);
    }

    public string Root { get; }

    public string WorkspaceRoot { get; }

    public string ContextRoot { get; }

    public string GitControlRoot { get; }

    public ProcessAgentRunner Processes { get; }

    public static string FixtureExecutable { get; } = Resolve();

    public static ScriptedFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-agent-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return new ScriptedFixture(root);
    }

    public ScriptedAgentRuntime Runtime(ScriptedAgentMode mode) =>
        new(
            new ScriptedAgentOptions { FixtureExecutablePath = FixtureExecutable, Mode = mode },
            Processes);

    public AgentRunRequest Request(
        TimeSpan? hardTimeout = null,
        string? structuredOutputPath = null,
        IReadOnlyList<string>? environmentAllowlist = null) => new()
    {
        WorkingDirectory = WorkspaceRoot,
        SystemPromptFile = Path.Combine(ContextRoot, "system.md"),
        UserPrompt = "do the deterministic thing",
        PermissionProfile = AgentPermissionProfile.WorkspaceWrite,
        EnvironmentAllowlist = environmentAllowlist ?? ["SystemRoot", "TEMP", "TMP"],
        HardTimeout = hardTimeout ?? TimeSpan.FromSeconds(30),
        CorrelationId = "HUB-001#1",
        StructuredOutputPath = structuredOutputPath,
    };

    public bool WorkspaceHasFile(string relativePath) =>
        File.Exists(Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public string ReadWorkspaceFile(string relativePath) =>
        File.ReadAllText(Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public IReadOnlyList<string> WorkspaceFiles() =>
        [.. Directory.EnumerateFiles(WorkspaceRoot, "*", SearchOption.AllDirectories)
            .Select(f => f[(WorkspaceRoot.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

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

/// <summary>Collects the progress stream so a test can assert on it.</summary>
internal sealed class EventCollector : IProgress<AgentEvent>
{
    private readonly List<AgentEvent> _events = [];

    public IReadOnlyList<AgentEvent> Events => _events;

    public void Report(AgentEvent value) => _events.Add(value);
}
