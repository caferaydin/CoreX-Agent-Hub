using System.Globalization;
using System.Runtime.InteropServices;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// The launch boundary itself: what happens between "there is no child" and "the child is
/// running inside the job", including every way that can go wrong.
///
/// The headline property is the one Phase 10 exists for. Previously a child was started and
/// then put in the job, so a fast child could spawn a descendant that escaped it - a small,
/// real, documented window. The fix is to create the child suspended, assign it, and only
/// then resume it, and the only honest way to prove that is to break the assignment and show
/// that nothing ran. Source-code ordering is not evidence; a fixture that never wrote its
/// marker is.
///
/// The failures are injected through an internal seam because they cannot be provoked
/// otherwise: <c>AssignProcessToJobObject</c> does not fail on a healthy machine, and a test
/// that waited for it to would never run.
/// </summary>
public sealed class NativeLaunchTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // --- The primary acceptance proof ---------------------------------------------------

    [Fact]
    public async Task A_created_child_is_provably_not_running_until_it_is_resumed()
    {
        using var workspace = TempWorkspace.Create();

        // The fixture writes this file as its first action and then exits.
        var report = workspace.Combine("suspended.txt");
        File.WriteAllText(report, string.Empty);

        var duringPause = "not observed";

        // Hold the launch open at the one moment that matters: the child exists, it is inside
        // the job, and it has not been resumed. Two seconds is far longer than the fixture
        // needs to start and write - so if it were running, this window would show it.
        //
        // This is what makes the test discriminating rather than merely green. A child created
        // without CREATE_SUSPENDED, or resumed before being assigned, writes during this pause
        // and fails the assertion below; the old Process.Start ordering would fail it too.
        var faults = new ProcessLaunchFaults
        {
            BeforeResume = () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                duringPause = File.ReadAllText(report);
            },
        };

        var runner = new ProcessRunner(workspace.Guard, faults);

        var result = await runner.RunAsync(
            Fixture.Request(workspace, Deadline, "marker", report),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);

        // Existing, assigned, and demonstrably inert.
        Assert.Equal(string.Empty, duringPause.Trim());

        // And it really was the same child, which ran the moment it was allowed to.
        Assert.Contains("marker invoked", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_that_could_not_be_put_in_the_job_never_executes()
    {
        using var workspace = TempWorkspace.Create();

        // The fixture writes a marker as its first action and immediately spawns a second
        // process that writes another. If a single instruction runs, this file is not empty.
        var report = workspace.Combine("assign-failure.txt");
        File.WriteAllText(report, string.Empty);

        var runner = new ProcessRunner(workspace.Guard, new ProcessLaunchFaults { FailAssign = true });

        var result = await runner.RunAsync(
            Fixture.Request(workspace, Deadline, "spawn-tree", report),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("AssignProcessToJobObject", result.StartFailureMessage!, StringComparison.Ordinal);

        // The child existed - it was created suspended - and was terminated without ever
        // being resumed. Nothing it would have done has happened.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(string.Empty, File.ReadAllText(report).Trim());
    }

    [Fact]
    public async Task A_child_that_could_not_be_resumed_is_terminated_rather_than_left_suspended()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("resume-failure.txt");
        File.WriteAllText(report, string.Empty);

        var runner = new ProcessRunner(workspace.Guard, new ProcessLaunchFaults { FailResume = true });

        var result = await runner.RunAsync(
            Fixture.Request(workspace, Deadline, "spawn-tree", report),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("ResumeThread", result.StartFailureMessage!, StringComparison.Ordinal);

        // A process that cannot be resumed must not be left frozen for ever holding a job
        // slot and a pipe; it is killed on the way out, and it never ran.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(string.Empty, File.ReadAllText(report).Trim());
    }

    [Fact]
    public async Task A_run_cancelled_before_the_child_was_resumed_never_runs_it()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("cancelled-before-resume.txt");
        File.WriteAllText(report, string.Empty);

        using var cancellation = new CancellationTokenSource();

        // Cancel in the window the launcher opens for exactly this question: the child exists,
        // is assigned, and has not been resumed.
        var faults = new ProcessLaunchFaults { BeforeResume = cancellation.Cancel };
        var runner = new ProcessRunner(workspace.Guard, faults);

        var result = await runner.RunAsync(
            Fixture.Request(workspace, Deadline, "spawn-tree", report),
            cancellation.Token);

        // Cancelled, not StartFailed: the caller withdrew a run that was otherwise fine.
        Assert.Equal(ProcessTerminationReason.Cancelled, result.TerminationReason);

        // Resuming it so the ordinary cancellation path could kill it would have executed the
        // very code the caller withdrew.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(string.Empty, File.ReadAllText(report).Trim());
    }

    // --- The other failure paths ---------------------------------------------------------

    [Fact]
    public async Task A_child_that_could_not_be_created_reports_a_start_failure()
    {
        using var workspace = TempWorkspace.Create();

        var runner = new ProcessRunner(workspace.Guard, new ProcessLaunchFaults { FailCreateProcess = true });

        var result = await runner.RunAsync(
            Fixture.Request(workspace, Deadline, "exit", "0"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("CreateProcess", result.StartFailureMessage!, StringComparison.Ordinal);
        Assert.Null(result.ExitCode);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public async Task A_job_that_could_not_be_created_starts_nothing_at_all()
    {
        using var workspace = TempWorkspace.Create();

        var report = workspace.Combine("no-job.txt");
        File.WriteAllText(report, string.Empty);

        var runner = new ProcessRunner(workspace.Guard, new ProcessLaunchFaults { FailJobCreation = true });

        var result = await runner.RunAsync(
            Fixture.Request(workspace, Deadline, "spawn-tree", report),
            CancellationToken.None);

        // No job means no bounded tree. There is deliberately no fallback that launches the
        // child anyway and hopes.
        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("job could not be created", result.StartFailureMessage!, StringComparison.Ordinal);
        Assert.Equal(string.Empty, File.ReadAllText(report).Trim());
    }

    // --- Argument round trip through a real process --------------------------------------

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("tab\there")]
    [InlineData("quote\"inside")]
    [InlineData("a\\\"b")]
    [InlineData("a\\\\\"b")]
    [InlineData("trailing\\")]
    [InlineData("trailing\\\\")]
    [InlineData("C:\\Program Files\\app\\")]
    [InlineData("görev-日本語-🚀")]
    [InlineData("mixed \\ back \\\\ slashes \"and\" quotes")]
    public async Task An_argument_arrives_exactly_as_it_was_given(string argument)
    {
        using var workspace = TempWorkspace.Create();

        // Stronger than testing the encoder: this is what the operating system's own parser
        // gave back, so an encoder that was self-consistently wrong would still fail here.
        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, Deadline, "echo-args", argument, "sentinel"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        var observed = Arguments(result.NormalizedStdout);

        Assert.Equal(2, observed.Count);
        Assert.Equal(argument, observed[0]);
        Assert.Equal("sentinel", observed[1]);
    }

    [Fact]
    public async Task Many_adversarial_arguments_keep_their_order_and_their_content()
    {
        using var workspace = TempWorkspace.Create();

        string[] arguments =
        [
            "first",
            "",
            "has space",
            "ends\\",
            "q\"uote",
            "back\\\\slash",
            "日本語",
            "last",
        ];

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, Deadline, ["echo-args", .. arguments]),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments, Arguments(result.NormalizedStdout));
    }

    // --- Environment ----------------------------------------------------------------------

    [Fact]
    public async Task The_child_sees_what_was_allowed_and_nothing_else()
    {
        using var workspace = TempWorkspace.Create();

        var allowed = "COREX_ALLOWED_" + Guid.CreateVersion7().ToString("N")[..8];
        var denied = "COREX_DENIED_" + Guid.CreateVersion7().ToString("N")[..8];

        Environment.SetEnvironmentVariable(allowed, "allowed-görev-🚀");
        Environment.SetEnvironmentVariable(denied, "must-not-cross");

        try
        {
            var request = Fixture.Request(workspace, Deadline, "print-env") with
            {
                EnvironmentAllowlist = [.. Fixture.DotnetHostAllowlist, allowed],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["COREX_EXPLICIT"] = "explicit-value",
                },
            };

            var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);

            var environment = EnvironmentOf(result.NormalizedStdout);

            // Named, so present - Unicode intact through the UTF-16 block.
            Assert.Equal("allowed-görev-🚀", environment[allowed]);

            // Explicitly supplied values arrive too.
            Assert.Equal("explicit-value", environment["COREX_EXPLICIT"]);

            // The Hub's own forced values are always there and cannot be displaced.
            Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("1", environment["CI"]);

            // Present in this process, never named, therefore invisible to the child. This is
            // the deny-by-default rule (ADR-0007), unchanged by the new launch path.
            Assert.False(environment.ContainsKey(denied));
        }
        finally
        {
            Environment.SetEnvironmentVariable(allowed, null);
            Environment.SetEnvironmentVariable(denied, null);
        }
    }

    [Fact]
    public async Task A_forced_value_cannot_be_overridden_by_the_caller()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, Deadline, "print-env") with
        {
            AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Same variable the Hub forces off, spelled differently on purpose: Windows
                // environment names are case insensitive, so this must not become a second entry.
                ["git_terminal_prompt"] = "1",
            },
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);
        var environment = EnvironmentOf(result.NormalizedStdout);

        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Single(environment, e => e.Key.Equals("GIT_TERMINAL_PROMPT", StringComparison.OrdinalIgnoreCase));
    }

    // --- Handle inheritance ----------------------------------------------------------------

    [Fact]
    public async Task An_unrelated_inheritable_handle_does_not_reach_the_child()
    {
        using var workspace = TempWorkspace.Create();

        var sentinelPath = workspace.Combine("sentinel.txt");
        File.WriteAllText(sentinelPath, string.Empty);

        using var sentinel = new FileStream(
            sentinelPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        // Deliberately made inheritable: exactly the kind of handle that would ride along if
        // the launcher inherited everything instead of naming the three stdio handles.
        Assert.True(SetHandleInformation(sentinel.SafeFileHandle.DangerousGetHandle(), 1, 1));

        var value = sentinel.SafeFileHandle.DangerousGetHandle().ToInt64()
            .ToString(CultureInfo.InvariantCulture);

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, Deadline, "probe-handle", value),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);

        sentinel.Flush();

        // The parent's own file is the evidence, not the child's opinion: if the child had
        // inherited the handle its write would have landed here. A coincidental handle number
        // in the child writes somewhere else and leaves this empty.
        //
        // Read through a sharing handle because this test still holds the file open for
        // write - which is the whole point of the sentinel.
        using var reader = new FileStream(
            sentinelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(0, reader.Length);
    }

    // --- Ordinary behaviour, through the new path ------------------------------------------

    [Fact]
    public async Task Output_written_at_the_first_instruction_is_still_captured()
    {
        using var workspace = TempWorkspace.Create();

        // The child writes and exits immediately. If the pumps were attached after the child
        // was resumed rather than before, this is the run that would lose its output.
        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, Deadline, "stdout", "immediate-output"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("immediate-output", result.NormalizedStdout);
    }

    [Fact]
    public async Task A_non_zero_exit_is_reported_as_the_process_reported_it()
    {
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, Deadline, "exit", "42"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(42, result.ExitCode);
        Assert.NotNull(result.ProcessId);
    }

    [Fact]
    public async Task The_working_directory_is_the_one_that_was_validated()
    {
        using var workspace = TempWorkspace.Create();
        var nested = workspace.CreateDirectory("nested");

        var request = Fixture.Request(workspace, Deadline, "marker", workspace.Combine("cwd.txt")) with
        {
            WorkingDirectory = nested,
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
    }

    private static List<string> Arguments(string stdout) =>
    [
        .. stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("ARG[", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..]),
    ];

    private static Dictionary<string, string> EnvironmentOf(string stdout)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');

            if (line == "ENV-END")
            {
                break;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                environment[line[..separator]] = line[(separator + 1)..];
            }
        }

        return environment;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
