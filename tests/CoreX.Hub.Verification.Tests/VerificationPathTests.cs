using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Working directory and report path containment. Every path a run touches is validated
/// against the same managed boundary the rest of the Hub uses.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationPathTests
{
    [Fact]
    public async Task The_declared_working_directory_is_where_the_step_runs()
    {
        using var fixture = VerificationFixture.Create();
        fixture.CreateWorkspaceDirectory("src");

        var profile = ProfileBuilder.Profile(
            "src",
            ProfileBuilder.Step(
                "report",
                ["report", "out/report.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit,
                resultPath: "out/report.xml"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.Passed, run.Steps[0].Status);

        // The report landed under the declared working directory, not the workspace root.
        Assert.True(File.Exists(Path.Combine(fixture.WorkspaceRoot, "src", "out", "report.xml")));
        Assert.False(File.Exists(Path.Combine(fixture.WorkspaceRoot, "out", "report.xml")));

        Assert.Equal(
            Path.Combine(fixture.WorkspaceRoot, "src"),
            run.Steps[0].WorkingDirectory);
    }

    [Fact]
    public async Task A_working_directory_that_leaves_the_workspace_is_rejected()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            Path.Combine("..", "outside"),
            ProfileBuilder.Step("build", ["exit", "0"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.StartFailed, run.Steps[0].Status);
        Assert.Contains("working directory", run.Steps[0].Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TraversalEscape", run.Steps[0].Diagnostic!, StringComparison.Ordinal);
        Assert.Null(run.Steps[0].StartedAtUtc);
    }

    [Fact]
    public async Task A_result_path_that_leaves_the_working_directory_is_rejected()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["exit", "0"],
                parser: VerificationResultParser.Trx,
                resultPath: Path.Combine("..", "outside.trx")));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.StartFailed, run.Steps[0].Status);
        Assert.Contains("result path", run.Steps[0].Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TraversalEscape", run.Steps[0].Diagnostic!, StringComparison.Ordinal);

        // Rejected before the process started, so no evidence and no exit code.
        Assert.Null(run.Steps[0].ExitCode);
        Assert.Empty(fixture.BlobFiles());
    }

    [Fact]
    public async Task A_report_reached_through_a_junction_leaving_the_workspace_is_rejected()
    {
        using var fixture = VerificationFixture.Create();

        // A real NTFS junction, not a simulated attribute: the report path resolves inside
        // the workspace but the directory it lands in points outside it.
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(outside, "results.trx"),
            "<TestRun><ResultSummary><Counters total=\"1\" passed=\"1\" /></ResultSummary></TestRun>");

        var junction = Path.Combine(fixture.WorkspaceRoot, "results");
        CreateJunction(junction, outside);
        Assert.True(Directory.Exists(junction), "the junction was not created");

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["exit", "0"],
                parser: VerificationResultParser.Trx,
                resultPath: "results/results.trx"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.StartFailed, run.Steps[0].Status);
        Assert.Contains("result path", run.Steps[0].Diagnostic!, StringComparison.OrdinalIgnoreCase);

        // Specifically the reparse rule, not merely "the file is missing".
        Assert.Contains("ReparsePointEscape", run.Steps[0].Diagnostic!, StringComparison.Ordinal);
    }

    private static void CreateJunction(string link, string target)
    {
        // mklink is a cmd builtin, so the test harness needs cmd to create the junction.
        // This is test setup observing the filesystem, never a Hub code path: production
        // still launches processes only through ProcessRunner, which refuses shells.
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(TimeSpan.FromSeconds(15));
    }
}
