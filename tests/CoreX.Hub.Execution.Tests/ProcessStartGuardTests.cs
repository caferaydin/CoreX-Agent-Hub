using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// What ProcessRunner refuses before a child ever exists: bad working directories,
/// shell shims and unresolvable executables. All of them produce a structured result,
/// never an unhandled exception and never a half started process.
/// </summary>
public sealed class ProcessStartGuardTests
{
    [Fact]
    public async Task A_nonexistent_working_directory_fails_before_the_child_starts()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "exit", "0") with
        {
            WorkingDirectory = workspace.Combine("does-not-exist"),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.False(result.Started);
        Assert.Null(result.ProcessId);
        Assert.Null(result.ExitCode);
        Assert.Contains("MissingRequiredPath", result.StartFailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_working_directory_outside_the_trust_root_is_refused()
    {
        using var workspace = TempWorkspace.Create();
        using var other = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "exit", "0") with
        {
            WorkingDirectory = other.Root,
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("OutsideRoot", result.StartFailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_executable_produces_a_structured_failure()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30)) with
        {
            Executable = workspace.Combine("no-such-tool.exe"),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("does not exist", result.StartFailureMessage!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("npm.cmd")]
    [InlineData("build.bat")]
    [InlineData("deploy.ps1")]
    [InlineData("run.sh")]
    public async Task Shell_shims_are_refused_rather_than_wrapped_in_a_shell(string shim)
    {
        using var workspace = TempWorkspace.Create();
        var path = workspace.CreateFile(shim, "echo hi");

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30)) with { Executable = path };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("without a shell", result.StartFailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relative_executable_is_refused_because_PATH_lookup_is_not_performed()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30)) with { Executable = "corex-fixture.exe" };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("fully qualified", result.StartFailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_executable_is_a_caller_error()
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30)) with { Executable = "  " };

        Assert.Throws<ProcessRequestException>(request.Validate);
    }

    [Fact]
    public async Task The_working_directory_the_child_observes_is_the_canonical_path()
    {
        using var workspace = TempWorkspace.Create();
        var nested = workspace.CreateDirectory("nested", "deep");

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "print-env") with
        {
            WorkingDirectory = Path.Combine(workspace.Root, "nested", "..", "nested", "deep"),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(nested));
    }
}
