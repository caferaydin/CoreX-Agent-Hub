using System.Diagnostics;
using System.Globalization;
using System.Text;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Cli;
using CoreX.Hub.Cli.Workspace;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// One complete, disposable Hub installation: its own data directory, its own configuration,
/// its own git repository, and nothing shared with the machine it runs on.
///
/// Everything here is real. The repository is a real git repository, the agent is the
/// deterministic fixture executable driven through the real scripted runtime, verification
/// runs real processes, and the commands under test are the shipped ones - either through the
/// same dispatch entry point the executable uses, or by launching the executable itself when
/// the point of the test is that two processes are involved.
///
/// Nothing reaches the network, no AI is called, and no developer repository or global git
/// configuration is touched.
/// </summary>
internal sealed class LoopZeroHub : IDisposable
{
    private readonly List<HubDbContext> _contexts = [];
    private readonly string _git;

    private string _agentMode = "apply-patch";
    private string _reportKind = "trx-fixed";
    private int _verificationExitCode;
    private AgentTimeouts _timeouts = AgentTimeouts.Default;
    private bool _hardIsolation;
    private string[]? _testStepArgs;

    private LoopZeroHub(string root, string git)
    {
        Root = root;
        _git = git;

        DataDirectory = Path.Combine(root, "data");
        WorkspaceRoot = Path.Combine(root, "cx");
        OrganizationRoot = Path.Combine(WorkspaceRoot, "_fixtures", "hub-dev");
        RepositoryPath = Path.Combine(OrganizationRoot, "hub-sample");
        ConfigPath = Path.Combine(root, HubPaths.ConfigFileName);

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RepositoryPath);
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public string WorkspaceRoot { get; }

    public string OrganizationRoot { get; }

    public string RepositoryPath { get; }

    public string ConfigPath { get; }

    public string DatabasePath => Path.Combine(DataDirectory, HubDatabase.DatabaseFileName);

    /// <summary>The deterministic agent and verification harness, built alongside these tests.</summary>
    public static string FixtureExecutable { get; } =
        Path.Combine(AppContext.BaseDirectory, "corex-fixture.exe");

    /// <summary>The shipped CLI, for the tests whose whole point is a separate process.</summary>
    public static string HubExecutable { get; } =
        Path.Combine(AppContext.BaseDirectory, "hub.exe");

    /// <summary>
    /// Builds an installation whose scripted agent plays <paramref name="agentMode"/> and whose
    /// verification writes <paramref name="reportKind"/> and exits with <paramref name="verificationExitCode"/>.
    ///
    /// Both are configuration rather than test-only switches: the runtime and the verification
    /// profile are read from hub.config.yaml exactly as they would be on a real installation,
    /// which is what makes each scenario below a real run of the shipped pipeline.
    /// </summary>
    public static LoopZeroHub Create(
        string agentMode = "apply-patch",
        string reportKind = "trx-fixed",
        int verificationExitCode = 0,
        bool requiresHardIsolation = false,
        AgentTimeouts? timeouts = null)
    {
        Assert.True(GitToolchain.TryResolve(out var git, out var diagnostic), diagnostic);
        Assert.True(File.Exists(FixtureExecutable), "the process fixture was not built: " + FixtureExecutable);
        Assert.True(File.Exists(HubExecutable), "the hub executable was not built: " + HubExecutable);

        var root = Path.Combine(Path.GetTempPath(), "corex-loop0-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);

        var hub = new LoopZeroHub(root, git!);

        hub.InitialiseRepository();
        hub.WriteConfig(agentMode, reportKind, verificationExitCode, requiresHardIsolation, timeouts ?? AgentTimeouts.Default);

        return hub;
    }

    /// <summary>
    /// Drives the exact dispatch path the executable uses, in this process.
    ///
    /// Used where the test is about what a command does rather than about process boundaries;
    /// where the boundary is the point, <see cref="ExecuteAsync"/> launches the executable.
    /// </summary>
    public async Task<CliResult> CliAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var full = new List<string> { "--data-dir", DataDirectory, "--config", ConfigPath };
        full.AddRange(args);

        var exitCode = await HubCli.RunAsync(full, stdout, stderr, CancellationToken.None);

        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Launches the shipped executable as a separate operating system process.</summary>
    public Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(HubExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Root,
        };

        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(DataDirectory);
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(ConfigPath);

        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("hub.exe did not start");

        // Nothing here types at a prompt. Closing the pipe means a command that asks a
        // question reads end of input and decides what to do about it, rather than a test
        // hanging on a console nobody is sitting at.
        process.StandardInput.Close();

        return process;
    }

    /// <summary>Runs the shipped executable to completion, with a deadline rather than a wait.</summary>
    public async Task<CliResult> ExecuteAsync(TimeSpan deadline, params string[] args)
    {
        using var process = Start(args);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(deadline);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            throw new TimeoutException(
                $"hub {string.Join(' ', args)} did not finish within {deadline.TotalSeconds:0} seconds");
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    public Task<CliResult> ExecuteAsync(params string[] args) =>
        ExecuteAsync(TimeSpan.FromMinutes(3), args);

    /// <summary>A store on its own connection, for reading what actually got persisted.</summary>
    public IHubStore OpenStore()
    {
        var context = HubDatabase.CreateContext(DatabasePath, new OrganizationScope());
        _contexts.Add(context);
        return new EfHubStore(context);
    }

    /// <summary>
    /// The Loop Zero task: two machine criteria answered from recorded verification, and one
    /// a person has to answer. Created and made Ready through the shipped commands, so the
    /// ready gate is the one that approved it.
    /// </summary>
    public async Task<CliResult> SeedTaskAsync(
        string key = "HUB-001",
        IReadOnlyList<string>? allow = null,
        IReadOnlyList<string>? machineRefs = null)
    {
        var args = new List<string>
        {
            "task", "create",
            "--org", "hub-dev",
            "--project", "hub-sample",
            "--repo", "hub-sample-repo",
            "--key", key,
            "--objective", "Add the feature the fixture applies, and keep the suite green.",
        };

        foreach (var glob in allow ?? ["src/**"])
        {
            args.Add("--allow");
            args.Add(glob);
        }

        foreach (var reference in machineRefs ?? ["build", "test"])
        {
            args.Add("--machine-criterion");
            args.Add(reference + "=the " + reference + " step passes");
        }

        args.Add("--human-criterion");
        args.Add("a person has read the diff and is happy with it");

        var created = await CliAsync([.. args]);
        Assert.True(created.ExitCode == 0, created.Describe());

        return created;
    }

    public async Task SeedReadyTaskAsync(
        string key = "HUB-001",
        IReadOnlyList<string>? allow = null,
        IReadOnlyList<string>? machineRefs = null)
    {
        await SeedTaskAsync(key, allow, machineRefs);

        var ready = await CliAsync("task", "ready", key);
        Assert.True(ready.ExitCode == 0, ready.Describe());
    }

    /// <summary>
    /// Declares hard isolation the installation does not have, after the fact.
    ///
    /// Written this way because the ready gate refuses a task in a blocked organization at
    /// all: to prove the D5 gate stops a run, the task has to have become Ready while the
    /// organization was still usable, exactly as it would if somebody edited configuration
    /// between the two.
    /// </summary>
    public void DeclareHardIsolation()
    {
        _hardIsolation = true;
        Rewrite();
    }

    /// <summary>
    /// Replaces the arguments of the verification step, keeping everything else as it is.
    ///
    /// Used to make verification block for a while - so a kill can land inside it - and then
    /// to make it finish normally again, which is what a machine that was wedged and is now
    /// fine looks like to the Hub.
    /// </summary>
    public void SetTestStepArguments(params string[] args)
    {
        _testStepArgs = args;
        Rewrite();
    }

    public void RestoreTestStep()
    {
        _testStepArgs = null;
        Rewrite();
    }

    private void Rewrite() =>
        WriteConfig(_agentMode, _reportKind, _verificationExitCode, _hardIsolation, _timeouts);

    private string TestStepArgs() =>
        string.Join(", ", (_testStepArgs ?? ["report", "results/results.trx", _reportKind,
            _verificationExitCode.ToString(CultureInfo.InvariantCulture)]).Select(a => "'" + a + "'"));

    /// <summary>The audit trail for one task, read straight from the table.</summary>
    public async Task<IReadOnlyList<Domain.Audit.AuditEvent>> ReadAuditAsync(Guid taskId)
    {
        var context = HubDatabase.CreateContext(DatabasePath, new OrganizationScope());
        _contexts.Add(context);

        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            System.Linq.Queryable.Where(context.AuditEvents, a => a.TaskId == taskId));
    }

    public string RunGit(params string[] args) => RunGitIn(RepositoryPath, args);

    public string RunGitIn(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(_git)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");

        return stdout.Trim();
    }

    public int GitExitCode(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(_git)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode;
    }

    /// <summary>
    /// Waits for something to become true, with a deadline. Never a bare sleep: a test that
    /// slept long enough to be probably right would be a test that fails on a slow machine and
    /// passes on a fast one for reasons unrelated to the code.
    /// </summary>
    public static async Task<T> WaitForAsync<T>(
        Func<Task<T?>> read,
        TimeSpan deadline,
        string what)
        where T : class
    {
        var until = DateTimeOffset.UtcNow + deadline;

        while (DateTimeOffset.UtcNow < until)
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{what} did not happen within {deadline.TotalSeconds:0} seconds");
    }

    /// <summary>
    /// The agent processes belonging to this suite, and to no other.
    ///
    /// Other test assemblies drive their own copy of the same fixture executable, so a name
    /// is not an identity. The image path is: each suite runs the copy in its own output
    /// directory.
    /// </summary>
    public static IReadOnlyList<Process> OwnFixtureProcesses()
    {
        var mine = new List<Process>();

        foreach (var process in Process.GetProcessesByName("corex-fixture"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, FixtureExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    mine.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited between listing and inspection, or not ours to look at.
            }

            process.Dispose();
        }

        return mine;
    }

    public static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private void InitialiseRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "Fixture");
        RunGit("config", "user.email", "fixture@corex.local");
        RunGit("config", "commit.gpgsign", "false");

        Directory.CreateDirectory(Path.Combine(RepositoryPath, "src"));
        File.WriteAllText(Path.Combine(RepositoryPath, "src", "existing.cs"), "// base\n");
        File.WriteAllText(Path.Combine(RepositoryPath, "README.md"), "# fixture\n");

        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "base");
    }

    private void WriteConfig(
        string agentMode,
        string reportKind,
        int verificationExitCode,
        bool requiresHardIsolation,
        AgentTimeouts timeouts)
    {
        _agentMode = agentMode;
        _reportKind = reportKind;
        _verificationExitCode = verificationExitCode;
        _timeouts = timeouts;
        _hardIsolation = requiresHardIsolation;

        var yaml = string.Create(
            CultureInfo.InvariantCulture,
            $"""
            schemaVersion: 1
            workspace:
              root: '{WorkspaceRoot}'
              fixtureRoot: '{Path.Combine(WorkspaceRoot, "_fixtures")}'
            verification:
              executables:
                fixture: '{FixtureExecutable}'
            agents:
              scriptedRuntimes:
                - key: scripted
                  fixturePath: '{FixtureExecutable}'
                  mode: {agentMode}
            organizations:
              - slug: hub-dev
                name: Loop Zero Fixture
                filesystemRoot: '{OrganizationRoot}'
                isolationLevel: Logical
                requiresHardIsolation: {(requiresHardIsolation ? "true" : "false")}
                environmentAllowlist: [PATH, SystemRoot, TEMP, TMP]
                agentProfiles:
                  - name: scripted-implementer
                    role: Implementer
                    provider: Scripted
                    runtimeKey: scripted
                    permissionProfile: WorkspaceWrite
                    softTimeoutSeconds: {timeouts.SoftSeconds}
                    idleTimeoutSeconds: {timeouts.IdleSeconds}
                    hardTimeoutSeconds: {timeouts.HardSeconds}
                projects:
                  - slug: hub-sample
                    name: Loop Zero Sample
                    taskKeyPrefix: HUB
                    repositories:
                      - slug: hub-sample-repo
                        name: hub-sample
                        localPath: '{RepositoryPath}'
                        defaultBranch: main
                        protectedBranches: [main]
                        verificationProfiles:
                          - name: fixture-verification
                            isDefault: true
                            workingDirectory: '.'
                            steps:
                              - name: build
                                executable: fixture
                                args: ['exit', '0']
                                timeoutSeconds: 120
                                required: true
                                resultParser: none
                              - name: test
                                executable: fixture
                                args: [{TestStepArgs()}]
                                timeoutSeconds: 120
                                required: true
                                resultParser: trx
                                resultPath: 'results/results.trx'

            """);

        File.WriteAllText(ConfigPath, yaml, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _contexts.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(Root))
            {
                // Worktrees are read-only in places; git leaves them that way on purpose.
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);

                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that will not go away is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The three deadlines an agent run is held to. Short ones make a hang test finish in
/// seconds; the ordinary ones are long enough that nothing else trips over them.
/// </summary>
internal sealed record AgentTimeouts(int SoftSeconds, int IdleSeconds, int HardSeconds)
{
    public static AgentTimeouts Default { get; } = new(30, 60, 120);

    /// <summary>Short enough that an agent which never says anything is noticed quickly.</summary>
    public static AgentTimeouts Impatient { get; } = new(2, 4, 30);
}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    public string All => Stdout + Stderr;

    public string Describe() => $"exit {ExitCode}{Environment.NewLine}{Stdout}{Stderr}";
}
