using System.Reflection;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// How a Windows child is created, and what must never come back.
///
/// The behavioural proof that a child cannot run before it is inside the job lives in
/// CoreX.Hub.Execution.Tests, where a failed assignment is injected and the fixture is shown
/// never to have written its marker. That is the real evidence and these guards do not try to
/// restate it. What they protect is the shape around it: that the native path stays the only
/// path, that it stays confined to one project, and that nobody quietly reintroduces the
/// managed launch that made the race possible in the first place.
/// </summary>
public sealed class ProcessLaunchInvariantTests
{
    private const string LauncherFileName = "NativeProcessLauncher.cs";
    private const string RunnerFileName = "ProcessRunner.cs";

    [Fact]
    public void The_native_launcher_is_the_only_place_that_creates_a_windows_process()
    {
        // CreateProcessW is an implementation detail of one file. A second caller would be a
        // second launch policy - its own environment handling, its own job discipline, its own
        // ordering - which is exactly what having a single boundary exists to prevent.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            if (string.Equals(Path.GetFileName(file), LauncherFileName, StringComparison.Ordinal))
            {
                continue;
            }

            // Commentary stripped: SafeThreadHandle's own doc explains what CreateProcessW
            // hands back, and a guard that read prose would be refusing the explanation.
            var text = CodeOnly(File.ReadAllText(file));

            foreach (var forbidden in new[] { "CreateProcessW", "ResumeThread", "PROC_THREAD_ATTRIBUTE" })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names {forbidden}; native process creation belongs to {LauncherFileName}.");
            }
        }
    }

    [Fact]
    public void The_launcher_creates_the_child_suspended_and_assigns_it_before_resuming_it()
    {
        var text = Launcher();

        // The flag is the mechanism. Without it the child is running the moment it exists and
        // no ordering below can put it in the job first.
        Assert.Contains("CreateSuspended = 0x00000004", text, StringComparison.Ordinal);
        Assert.Contains("CreateUnicodeEnvironment = 0x00000400", text, StringComparison.Ordinal);
        Assert.Contains("CreateSuspended | CreateUnicodeEnvironment", text, StringComparison.Ordinal);

        var create = text.IndexOf("created = CreateProcessW(", StringComparison.Ordinal);
        var assign = text.IndexOf("job.TryAssign(processHandle", StringComparison.Ordinal);
        var resume = text.IndexOf("ResumeThread(threadHandle)", StringComparison.Ordinal);

        Assert.True(create > 0, "the launcher should create the child");
        Assert.True(assign > create, "assignment must follow creation");
        Assert.True(resume > assign, "the child must be assigned to the job before it is resumed");
    }

    [Fact]
    public void A_child_that_could_not_be_assigned_is_never_resumed()
    {
        var text = Launcher();

        // Structural companion to the behavioural test: the assignment failure returns, so
        // there is no path from a failed assignment to ResumeThread at all.
        var assignFailure = text.IndexOf("NativeLaunchFailure.Assign", StringComparison.Ordinal);
        var resume = text.IndexOf("ResumeThread(threadHandle)", StringComparison.Ordinal);

        Assert.True(assignFailure > 0 && resume > assignFailure);
        Assert.Contains("if (!assigned)", text, StringComparison.Ordinal);

        // And every exit that did not reach a running child terminates what it created.
        Assert.Contains("if (!started && processHandle is not null)", text, StringComparison.Ordinal);
        Assert.Contains("TerminateQuietly(processHandle)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runner_has_no_fallback_that_starts_a_child_without_a_job()
    {
        // Code only: the class doc now explains the race it used to have, and naming the
        // old mechanism in that explanation is the point of writing it down.
        var text = CodeOnly(Runner());

        // The managed launch is gone from the one file that was allowed to use it. Its
        // absence here is what makes the repository-wide ban on Process.Start total.
        Assert.DoesNotContain("Process.Start", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProcessStartInfo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UseShellExecute", text, StringComparison.Ordinal);

        // The job is created before the launcher is called, and a job that could not be
        // created ends the attempt rather than lowering the standard.
        var job = text.IndexOf("JobObjectHandle.CreateKillOnClose()", StringComparison.Ordinal);
        var launch = text.IndexOf("NativeProcessLauncher.Launch(", StringComparison.Ordinal);

        Assert.True(job > 0 && launch > job, "the job must exist before the child is created");
        Assert.Contains("The process job could not be created", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_launch_seam_is_not_reachable_from_outside_the_execution_project()
    {
        // The fault injector exists so the failure paths can be tested. It must never become
        // something a composition root or a provider could set.
        var faults = typeof(ProcessRunner).Assembly.GetType("CoreX.Hub.Execution.Processes.ProcessLaunchFaults");

        Assert.NotNull(faults);
        Assert.False(faults!.IsPublic, "the fault seam must not be public");

        var constructors = typeof(ProcessRunner).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // Exactly one way in from outside: a guard, and nothing else.
        var only = Assert.Single(constructors);
        var parameter = Assert.Single(only.GetParameters());

        Assert.Equal(typeof(FileSystemGuard), parameter.ParameterType);
    }

    [Fact]
    public void No_provider_or_composition_code_reaches_the_launch_primitives()
    {
        // Providers submit an executable, an argument vector, a working directory and an
        // environment. How that becomes a process is not their concern and must not become one.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var name = Path.GetFileName(file);

            if (!file.Contains($"{Path.DirectorySeparatorChar}CoreX.Hub.Agents{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}CoreX.Hub.Composition{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var forbidden in new[]
                     {
                         "NativeProcessLauncher", "ProcessLaunchFaults", "JobObjectHandle",
                         "WindowsCommandLine", "WindowsPipe", "SafeThreadHandle",
                     })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{name} names {forbidden}; the launch primitives are private to the execution project.");
            }
        }
    }

    [Fact]
    public void The_job_wrapper_can_still_grow_the_quotas_a_later_slice_will_add()
    {
        // Phase 10B-1 deliberately sets no numeric policy. What it must not do is make the
        // next slice rewrite this type to add one: the extended limit structure is already
        // the one Windows wants, so enabling a limit later is a flag and a field, not a
        // redesign.
        var text = File.ReadAllText(SourceFile("JobObjectHandle.cs"));

        Assert.Contains("JobObjectExtendedLimitInformation", text, StringComparison.Ordinal);
        Assert.Contains("ActiveProcessLimit", text, StringComparison.Ordinal);
        Assert.Contains("ProcessMemoryLimit", text, StringComparison.Ordinal);
        Assert.Contains("JobMemoryLimit", text, StringComparison.Ordinal);

        // And the one limit this slice does rely on is still the one that reaps a tree.
        Assert.Contains("JobObjectLimitKillOnJobClose", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_later_phase_work_arrived_with_the_launcher()
    {
        // 10B-1 is the Windows launch primitive and nothing else. WSL is 10C; quotas are
        // 10B-2; budget, garbage collection and the repair loop are later still.
        foreach (var name in new[] { LauncherFileName, RunnerFileName, "WindowsPipe.cs", "NativeChildProcess.cs" })
        {
            var text = File.ReadAllText(SourceFile(name));

            foreach (var forbidden in new[]
                     {
                         "wsl", "distro", "cgroup", "systemd", "HardIsolation",
                         "RepairLoop", "budget", "GarbageCollect", "EvidenceGc",
                     })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{name}: {forbidden}");
            }
        }
    }

    /// <summary>Source with its commentary removed, so a guard measures code.</summary>
    private static string CodeOnly(string source) =>
        string.Join(
            Environment.NewLine,
            source
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("/*", StringComparison.Ordinal)));

    private static string Launcher() => File.ReadAllText(SourceFile(LauncherFileName));

    private static string Runner() => File.ReadAllText(SourceFile(RunnerFileName));

    private static string SourceFile(string fileName)
    {
        var path = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));

        Assert.True(path is not null, $"{fileName} was not found in the production sources");

        return path!;
    }
}
