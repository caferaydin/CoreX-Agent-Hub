using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Architecture invariant E.1 #3, runtime half.
///
/// The source guard proves production code writes no shell launch. This suite proves the
/// request boundary refuses one: a caller cannot obtain a shell by handing ProcessRunner
/// an interpreter path, and argument vector entries never acquire shell semantics.
/// </summary>
public sealed class NoShellPolicyTests
{
    private const string SystemRoot = "C:/Windows/System32";

    [Theory]
    // Real absolute system paths, using the exact casing Windows ships.
    [InlineData(SystemRoot + "/cmd.exe")]
    [InlineData(SystemRoot + "/CMD.EXE")]
    [InlineData(SystemRoot + "/Cmd.Exe")]
    [InlineData(SystemRoot + "/WindowsPowerShell/v1.0/powershell.exe")]
    [InlineData(SystemRoot + "/WindowsPowerShell/v1.0/PowerShell.EXE")]
    // Classified without touching the filesystem, so an uninstalled shell is still caught.
    [InlineData("C:/Program Files/PowerShell/7/pwsh.exe")]
    [InlineData("C:/Program Files/PowerShell/7/PWSH.EXE")]
    [InlineData("C:/Program Files/Git/bin/bash.exe")]
    [InlineData("C:/Program Files/Git/usr/bin/sh.exe")]
    [InlineData("C:/tools/cmd")]
    [InlineData("C:/tools/bash")]
    public void Direct_shell_interpreters_are_classified_as_shells(string executable)
    {
        Assert.True(ShellExecutablePolicy.IsShellOrScript(executable, out var reason));
        Assert.Contains("shell", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C:/build/npm.cmd")]
    [InlineData("C:/build/BUILD.BAT")]
    [InlineData("C:/build/deploy.ps1")]
    [InlineData("C:/build/module.psm1")]
    [InlineData("C:/build/run.sh")]
    [InlineData("C:/build/legacy.vbs")]
    [InlineData("C:/build/task.js")]
    public void Script_and_shim_inputs_are_classified_as_scripts(string executable)
    {
        Assert.True(ShellExecutablePolicy.IsShellOrScript(executable, out var reason));
        Assert.Contains("shell", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C:/tools/corex-fixture.exe")]
    [InlineData("C:/Program Files/dotnet/dotnet.exe")]
    [InlineData("C:/Program Files/Git/cmd/git.exe")]
    [InlineData("C:/tools/mybash.exe")]
    [InlineData("C:/tools/shell-helper.exe")]
    [InlineData("C:/tools/powershell-formatter.exe")]
    [InlineData("C:/Windows/System32/wsl.exe")]
    public void Ordinary_executables_are_not_classified_as_shells(string executable)
    {
        Assert.False(ShellExecutablePolicy.IsShellOrScript(executable, out var reason));
        Assert.Null(reason);
    }

    /// <summary>
    /// A shell request must be refused before any process is created: no child appears,
    /// and the working directory and environment never even come into play.
    /// </summary>
    [Theory]
    [InlineData(SystemRoot + "/cmd.exe", "/c", "echo hi")]
    [InlineData(SystemRoot + "/CMD.EXE", "/c", "echo hi")]
    [InlineData(SystemRoot + "/WindowsPowerShell/v1.0/powershell.exe", "-Command", "Get-Date")]
    [InlineData("C:/Program Files/PowerShell/7/pwsh.exe", "-Command", "Get-Date")]
    [InlineData("C:/Program Files/Git/bin/bash.exe", "-c", "id")]
    [InlineData("C:/Program Files/Git/usr/bin/sh.exe", "-c", "id")]
    public async Task ProcessRunner_refuses_a_shell_before_starting_anything(
        string executable,
        string flag,
        string payload)
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), flag, payload) with
        {
            Executable = executable,
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.False(result.Started);
        Assert.Null(result.ProcessId);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.RawStdout);
        Assert.Empty(result.RawStderr);
        Assert.Contains("shell", result.StartFailureMessage!, StringComparison.OrdinalIgnoreCase);

        EvidenceLog.Write($"NoShell rejected: {executable} -> {result.TerminationReason}: {result.StartFailureMessage}");
    }

    [Fact]
    public async Task A_shell_is_refused_even_when_the_working_directory_is_invalid()
    {
        // Proves the ordering: classification happens before the path is even considered.
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "/c", "echo hi") with
        {
            Executable = SystemRoot + "/cmd.exe",
            WorkingDirectory = workspace.Combine("does-not-exist"),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("shell", result.StartFailureMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MissingRequiredPath", result.StartFailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_normal_executable_is_still_allowed()
    {
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), "exit", "7"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(7, result.ExitCode);
    }
}
