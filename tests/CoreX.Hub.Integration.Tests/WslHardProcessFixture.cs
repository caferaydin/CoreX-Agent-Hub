using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A disposable Linux workspace inside whatever WSL environment this machine already has.
///
/// What it creates, and nothing else: one uniquely named directory under <c>/var/tmp</c>, and one
/// Python file inside it. Both are removed when the tests finish. It does not touch
/// <c>/etc/wsl.conf</c>, <c>/etc/sudoers</c>, any user, any group, any provider home or any real
/// project path, and it does not register, import or configure a distribution.
///
/// <c>/var/tmp</c> rather than <c>/tmp</c>, and that was measured rather than preferred: the
/// distribution's <c>/tmp</c> is cleaned out from under a fixture within minutes, which showed up
/// first as a systemd exec failure with status 200 on a directory that had existed a moment
/// earlier.
///
/// It needs root to create the directory and to make it readable by the workload's account. That
/// is the trusted control plane doing setup for a test, which is exactly the allowance the phase
/// brief describes, and it is the only privileged thing the tests do.
///
/// Using this environment proves nothing about isolation. The distribution stays exactly as
/// unready as Phase 10C-1 found it - the agent it runs as is an ordinary system account, the host
/// drives are still mounted, interop is still registered. What these tests establish is that the
/// containment mechanics work, which is a different claim and a smaller one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WslHardProcessFixture : IAsyncLifetime
{
    /// <summary>An account every Debian derived distribution has, owning nothing interesting.</summary>
    public const string AgentUser = "daemon";

    /// <summary>The trusted identity of the profile. Distinct from the agent, as a profile requires.</summary>
    public const string ServiceUser = "root";

    private string _hostWorkingDirectory = string.Empty;

    /// <summary>The environment these tests run in, or empty when this machine has none.</summary>
    public string EnvironmentId { get; private set; } = string.Empty;

    /// <summary>The unique Linux directory this fixture owns.</summary>
    public string Root { get; } =
        "/var/tmp/corex-hard-process-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    public string FixturePath => Root + "/workload.py";

    /// <summary>Whether there is an environment to run in at all.</summary>
    public bool Available => EnvironmentId.Length > 0;

    public async Task InitializeAsync()
    {
        _hostWorkingDirectory = Path.Combine(
            Path.GetTempPath(), "corex-hard-process", Guid.CreateVersion7().ToString("N"));

        Directory.CreateDirectory(_hostWorkingDirectory);

        EnvironmentId = await FindEnvironmentAsync();

        if (!Available)
        {
            return;
        }

        await ControlAsync(["/usr/bin/mkdir", "-p", Root]);

        // The workload account must be able to enter the directory and read the file; nothing
        // here needs to be writable by it.
        await ControlAsync(["/usr/bin/chmod", "0755", Root]);
        await WriteWorkloadAsync();
        await ControlAsync(["/usr/bin/chmod", "0644", FixturePath]);
    }

    public async Task DisposeAsync()
    {
        if (Available)
        {
            await ControlAsync(["/usr/bin/rm", "-rf", Root]);
        }

        try
        {
            if (Directory.Exists(_hostWorkingDirectory))
            {
                Directory.Delete(_hostWorkingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp directory must never fail a test run.
        }
    }

    /// <summary>A profile whose execution root is this fixture's own directory.</summary>
    public HardIsolationProfile Profile(int processLimit = 64, long memoryLimitBytes = 512L * 1024 * 1024)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = EnvironmentId,
            AgentUser = AgentUser,
            ServiceUser = ServiceUser,
            ExecutionRoot = Root,
            EvidenceRoot = Root + "-evidence",
            ProcessLimit = processLimit,
            MemoryLimitBytes = memoryLimitBytes,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    public HardProcessRequest Request(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan? hardTimeout = null) =>
        new()
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = Root,
            HardTimeout = hardTimeout ?? TimeSpan.FromSeconds(90),
            CorrelationId = Guid.CreateVersion7(),
        };

    /// <summary>A request that runs the Python workload in one of its modes.</summary>
    public HardProcessRequest Workload(params string[] arguments) =>
        Request("/usr/bin/python3", [FixturePath, .. arguments]);

    public IHardProcessExecutor Executor() => WslHardProcessExecutor.Create(_hostWorkingDirectory);

    /// <summary>
    /// Runs a command in the environment as root, the way the tests inspect it.
    ///
    /// Through <see cref="ProcessRunner"/> like everything else: a test that reached for
    /// <c>Process.Start</c> would be proving something about a path that does not ship.
    /// </summary>
    public async Task<(int? ExitCode, string Stdout)> ControlAsync(IReadOnlyList<string> arguments)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return (null, string.Empty);
        }

        var runner = new ProcessRunner(FileSystemGuard.Create(_hostWorkingDirectory));

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["-d", EnvironmentId, "-u", "root", "--exec", .. arguments],
                WorkingDirectory = _hostWorkingDirectory,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromSeconds(120),
            },
            CancellationToken.None);

        return (result.ExitCode, result.NormalizedStdout);
    }

    /// <summary>The processes in one unit's control group, or none when the group is gone.</summary>
    public async Task<IReadOnlyList<string>> CgroupMembersAsync(string unitName)
    {
        var (exitCode, stdout) = await ControlAsync(
            ["/usr/bin/cat", $"/sys/fs/cgroup/system.slice/{unitName}/cgroup.procs"]);

        return exitCode == 0
            ? [.. stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)]
            : [];
    }

    public async Task<string> UnitLoadStateAsync(string unitName)
    {
        var (_, stdout) = await ControlAsync(
            ["/usr/bin/systemctl", "show", unitName, "--property=LoadState"]);

        return stdout.Trim();
    }

    /// <summary>
    /// Which environment to use, asked the same way production asks.
    ///
    /// A Debian derived one is preferred, because the account the profile names is a Debian
    /// convention. On a machine with no WSL at all this is empty and the tests say so.
    /// </summary>
    private async Task<string> FindEnvironmentAsync()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return string.Empty;
        }

        var runner = new ProcessRunner(FileSystemGuard.Create(_hostWorkingDirectory));

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["--list", "--quiet"],
                WorkingDirectory = _hostWorkingDirectory,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromSeconds(90),
            },
            CancellationToken.None);

        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        var names = result.NormalizedStdout
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return names.FirstOrDefault(n =>
                   n.Contains("ubuntu", StringComparison.OrdinalIgnoreCase)
                   || n.Contains("debian", StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault()
               ?? string.Empty;
    }

    /// <summary>
    /// Writes the workload into the environment through <c>tee</c>, which needs no shell.
    ///
    /// The alternative - a redirection - would have meant a shell, and a test harness that
    /// reaches for one weakens the guard that says production has none.
    /// </summary>
    private async Task WriteWorkloadAsync()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        var runner = new ProcessRunner(FileSystemGuard.Create(_hostWorkingDirectory));

        await runner.RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["-d", EnvironmentId, "-u", "root", "--exec", "/usr/bin/tee", FixturePath],
                WorkingDirectory = _hostWorkingDirectory,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                StdinPayload = WorkloadSource,
                HardTimeout = TimeSpan.FromSeconds(90),
            },
            CancellationToken.None);
    }

    /// <summary>
    /// The workload the containment tests run.
    ///
    /// Python rather than a shell script, deliberately. The tests need a process that can fork a
    /// tree and allocate memory, and the obvious way to get one is a shell - which would mean
    /// introducing into the test harness the very thing production is guarded against having. It
    /// is written into the environment's own temporary directory and removed with it.
    /// </summary>
    private static string WorkloadSource { get; } =
        """
        import os, sys, time

        mode = sys.argv[1]

        if mode == "identity":
            print("UID=%d" % os.getuid(), flush=True)
            print("CWD=%s" % os.getcwd(), flush=True)

        elif mode == "environment":
            for name in sorted(os.environ):
                print("%s=%s" % (name, os.environ[name]), flush=True)

        elif mode == "streams":
            print("ON-STDOUT", flush=True)
            print("ON-STDERR", file=sys.stderr, flush=True)
            sys.exit(int(sys.argv[2]))

        elif mode == "echo":
            data = sys.stdin.read()
            print("READ=%s" % data.strip(), flush=True)
            print("EOF-SEEN", flush=True)

        elif mode == "spawn":
            made = 0
            for _ in range(int(sys.argv[2])):
                try:
                    pid = os.fork()
                except OSError:
                    break
                if pid == 0:
                    time.sleep(600)
                    os._exit(0)
                made += 1
            print("SPAWNED=%d" % made, flush=True)

        elif mode == "alloc":
            target = int(sys.argv[2])
            held = []
            got = 0
            while got < target:
                held.append(bytearray(4 * 1024 * 1024))
                got += 4
                print("ALLOCATED=%d" % got, flush=True)
            print("REACHED=%d" % got, flush=True)

        elif mode == "tree":
            print("PARENT=%d" % os.getpid(), flush=True)
            if os.fork() == 0:
                if os.fork() == 0:
                    time.sleep(600)
                    os._exit(0)
                time.sleep(600)
                os._exit(0)
            time.sleep(600)

        elif mode == "sleep":
            print("SLEEPING", flush=True)
            time.sleep(float(sys.argv[2]))
            print("WOKE", flush=True)
        """.ReplaceLineEndings("\n");
}
