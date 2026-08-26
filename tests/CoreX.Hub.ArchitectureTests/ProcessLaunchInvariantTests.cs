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
        var job = text.IndexOf("JobObjectHandle.Create(", StringComparison.Ordinal);
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
    public void Kill_on_close_is_not_optional_however_the_limits_are_composed()
    {
        // The worst way a quota feature could fail is by costing the guarantee it was added
        // beside: a job that stops reaping leaves trees behind. So the flag is unconditional
        // in the plan rather than one branch among several, and it is asserted here as well
        // as exhaustively in the plan's own tests.
        var text = CodeOnly(File.ReadAllText(SourceFile("JobLimitPlan.cs")));

        Assert.Contains("var flags = KillOnJobClose;", text, StringComparison.Ordinal);

        foreach (var conditional in new[] { "flags |= ActiveProcess;", "flags |= JobMemory;", "flags |= ProcessMemory;" })
        {
            Assert.Contains(conditional, text, StringComparison.Ordinal);
        }

        // Every optional flag is reached only from inside its own value's test, so a ceiling
        // is never enabled without the number it is supposed to enforce.
        foreach (var (guard, flag) in new[]
                 {
                     ("if (limits.ActiveProcessLimit is { } processes)", "flags |= ActiveProcess;"),
                     ("if (limits.JobMemoryLimitBytes is { } jobBytes)", "flags |= JobMemory;"),
                     ("if (limits.ProcessMemoryLimitBytes is { } processBytes)", "flags |= ProcessMemory;"),
                 })
        {
            var at = text.IndexOf(guard, StringComparison.Ordinal);
            var set = text.IndexOf(flag, StringComparison.Ordinal);

            Assert.True(at > 0, guard);
            Assert.True(set > at, $"{flag} must be set only inside {guard}");
        }
    }

    [Fact]
    public void The_job_is_configured_before_any_child_could_exist_and_never_falls_back()
    {
        var job = CodeOnly(File.ReadAllText(SourceFile("JobObjectHandle.cs")));

        // Limits are applied as part of creating the job, so there is no window in which a
        // job exists more weakly limited than the caller asked for.
        Assert.Contains("var plan = JobLimitPlan.For(limits);", job, StringComparison.Ordinal);
        Assert.Contains("information.BasicLimitInformation.LimitFlags = plan.LimitFlags;", job, StringComparison.Ordinal);

        // And a refusal disposes the job and throws rather than returning a lesser one.
        Assert.Contains("if (!configured)", job, StringComparison.Ordinal);
        Assert.Contains("job.Dispose();", job, StringComparison.Ordinal);
        Assert.DoesNotContain("return job;\r\n        }\r\n\r\n        // fallback", job, StringComparison.Ordinal);

        // In the runner, the job is fully built before the launcher is ever called.
        var runner = CodeOnly(Runner());

        var created = runner.IndexOf("JobObjectHandle.Create(", StringComparison.Ordinal);
        var launched = runner.IndexOf("NativeProcessLauncher.Launch(", StringComparison.Ordinal);

        Assert.True(created > 0 && launched > created);
    }

    [Fact]
    public void The_win32_translation_lives_only_in_the_execution_project()
    {
        // Job Object flags are a containment mechanism, not a domain concept. A limit may
        // travel as a neutral number; the bits that enforce it may not travel at all.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}CoreX.Hub.Execution{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = CodeOnly(File.ReadAllText(file));

            foreach (var forbidden in new[]
                     {
                         "JOB_OBJECT_LIMIT", "JobLimitPlan", "SetInformationJobObject",
                         "ActiveProcessLimit", "JobMemoryLimit", "ProcessMemoryLimit",
                     })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names {forbidden}; Win32 quota translation belongs to the execution project.");
            }
        }
    }

    [Fact]
    public void No_universal_numeric_quota_is_baked_into_the_product()
    {
        // 10B-2 ships the mechanism, not the policy. ProcessRunner also drives verification
        // and git, so a number chosen for an agent would silently become the ceiling for a
        // build and a clone as well - which is exactly why the default asks for nothing.
        var contracts = CodeOnly(File.ReadAllText(SourceFile("ProcessContracts.cs")));

        Assert.Contains(
            "public ProcessResourceLimits ResourceLimits { get; init; } = ProcessResourceLimits.None;",
            contracts,
            StringComparison.Ordinal);

        var limits = CodeOnly(File.ReadAllText(SourceFile("ProcessResourceLimits.cs")));

        // Every ceiling is optional and none has a value. A default here would be a policy.
        Assert.Contains("public int? ActiveProcessLimit { get; init; }", limits, StringComparison.Ordinal);
        Assert.Contains("public long? JobMemoryLimitBytes { get; init; }", limits, StringComparison.Ordinal);
        Assert.Contains("public long? ProcessMemoryLimitBytes { get; init; }", limits, StringComparison.Ordinal);
        Assert.DoesNotContain("= 32", limits, StringComparison.Ordinal);
        Assert.DoesNotContain("1024 * 1024", limits, StringComparison.Ordinal);

        // And no composition root has quietly picked one either.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            Assert.DoesNotContain(
                "new ProcessResourceLimits",
                CodeOnly(File.ReadAllText(file)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_resource_ceiling_is_never_provider_specific()
    {
        var limits = File.ReadAllText(SourceFile("ProcessResourceLimits.cs"));

        foreach (var forbidden in new[] { "Claude", "Codex", "Scripted", "Organization", "AgentProfile" })
        {
            Assert.False(limits.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }
    }

    [Fact]
    public void No_later_phase_work_arrived_with_the_launcher()
    {
        // 10B-1 is the Windows launch primitive and nothing else. WSL is 10C; quotas are
        // 10B-2; budget, garbage collection and the repair loop are later still.
        foreach (var name in new[]
                 {
                     LauncherFileName, RunnerFileName, "WindowsPipe.cs", "NativeChildProcess.cs",
                     "JobLimitPlan.cs", "ProcessResourceLimits.cs", "JobObjectHandle.cs",
                 })
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
