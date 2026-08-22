using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Basic process contract: exit codes, stream capture, encoding and start failures.
/// Every run uses the deterministic fixture executable, never git, dotnet, npm or an
/// agent CLI.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan Hard = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Zero_exit_code_is_captured()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "exit", "0");

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.JobTerminated);
        Assert.NotNull(result.ProcessId);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Non_zero_exit_code_is_captured()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "exit", "42");

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task Stdout_is_captured()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "stdout", "merhaba-stdout");

        Assert.Equal("merhaba-stdout", result.NormalizedStdout);
        Assert.Empty(result.NormalizedStderr);
    }

    [Fact]
    public async Task Stderr_is_captured()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "stderr", "merhaba-stderr");

        Assert.Equal("merhaba-stderr", result.NormalizedStderr);
        Assert.Empty(result.NormalizedStdout);
    }

    [Fact]
    public async Task Both_streams_are_captured_in_one_run()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "both", "cikti", "hata");

        Assert.Equal("cikti", result.NormalizedStdout);
        Assert.Equal("hata", result.NormalizedStderr);
    }

    /// <summary>
    /// The classic redirected pipe deadlock: both streams are flooded far past any pipe
    /// buffer at the same time. If either stream were read to completion before the other
    /// this test would hang instead of failing.
    /// </summary>
    [Fact]
    public async Task High_volume_on_both_streams_does_not_deadlock()
    {
        using var workspace = TempWorkspace.Create();
        const int PerStream = 4 * 1024 * 1024;

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(120), "flood", PerStream.ToString(null as IFormatProvider))
            with
            { MaxCapturedCharactersPerStream = 16 * 1024 * 1024 };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        Assert.EndsWith("STDOUT-END", result.RawStdout, StringComparison.Ordinal);
        Assert.EndsWith("STDERR-END", result.RawStderr, StringComparison.Ordinal);

        Assert.True(result.StdoutCharactersObserved >= PerStream, $"stdout {result.StdoutCharactersObserved}");
        Assert.True(result.StderrCharactersObserved >= PerStream, $"stderr {result.StderrCharactersObserved}");
        Assert.False(result.StdoutTruncated);
        Assert.False(result.StderrTruncated);

        EvidenceLog.Write(
            $"Flood(no ceiling)     stdout observed={result.StdoutCharactersObserved} captured={result.RawStdout.Length} "
            + $"stderr observed={result.StderrCharactersObserved} captured={result.RawStderr.Length} "
            + $"duration={result.Duration.TotalMilliseconds:F0}ms exit={result.ExitCode}");
    }

    /// <summary>
    /// Capture is bounded, consumption is not. Storage stops at the ceiling while the pipe
    /// keeps draining, so the child still runs to completion.
    /// </summary>
    [Fact]
    public async Task Output_ceiling_bounds_memory_without_deadlocking_the_pipe()
    {
        using var workspace = TempWorkspace.Create();
        const int PerStream = 4 * 1024 * 1024;
        const int Ceiling = 64 * 1024;

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(120), "flood", PerStream.ToString(null as IFormatProvider))
            with
            { MaxCapturedCharactersPerStream = Ceiling };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        Assert.True(result.StdoutTruncated);
        Assert.True(result.StderrTruncated);
        Assert.Equal(Ceiling, result.RawStdout.Length);
        Assert.Equal(Ceiling, result.RawStderr.Length);

        // Everything the process produced was still consumed from the pipe.
        Assert.True(result.StdoutCharactersObserved >= PerStream, $"stdout {result.StdoutCharactersObserved}");
        Assert.True(result.StderrCharactersObserved >= PerStream, $"stderr {result.StderrCharactersObserved}");

        EvidenceLog.Write(
            $"Flood(ceiling {Ceiling}) stdout observed={result.StdoutCharactersObserved} captured={result.RawStdout.Length} "
            + $"truncated={result.StdoutTruncated} stderr observed={result.StderrCharactersObserved} "
            + $"captured={result.RawStderr.Length} truncated={result.StderrTruncated} exit={result.ExitCode}");
    }

    [Fact]
    public async Task Utf8_text_survives_both_streams()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "utf8");

        Assert.Contains("İstanbul, çalışma, doğrulama", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.Contains("ĞÜŞİÖÇ ğüşıöç", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.Contains("doğrulama başarısız", result.NormalizedStderr, StringComparison.Ordinal);

        EvidenceLog.Write(
            "Utf8 stdout=" + result.NormalizedStdout.Replace(Environment.NewLine, " | ", StringComparison.Ordinal).Trim()
            + " || stderr=" + result.NormalizedStderr.Trim());
    }

    [Fact]
    public async Task Ansi_is_normalized_while_raw_output_is_preserved()
    {
        using var workspace = TempWorkspace.Create();

        var result = await Run(workspace, "ansi");

        Assert.True(result.AnsiNormalizationApplied);

        Assert.Contains('\u001b', result.RawStdout);
        Assert.DoesNotContain('\u001b', result.NormalizedStdout);
        Assert.DoesNotContain('\u001b', result.NormalizedStderr);

        Assert.Contains("kirmizi", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.Contains("yesil", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.Contains("-son", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.DoesNotContain("[31m", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.DoesNotContain("window title", result.NormalizedStdout, StringComparison.Ordinal);
        Assert.Contains("uyari", result.NormalizedStderr, StringComparison.Ordinal);

        EvidenceLog.Write(
            $"Ansi rawStdoutLen={result.RawStdout.Length} normalizedStdoutLen={result.NormalizedStdout.Length} "
            + $"normalized='{result.NormalizedStdout.Trim()}' escapesInRaw={result.RawStdout.Count(c => c == '')}");
    }

    private static Task<ProcessRunResult> Run(TempWorkspace workspace, params string[] arguments) =>
        workspace.Runner.RunAsync(Fixture.Request(workspace, Hard, arguments), CancellationToken.None);
}
