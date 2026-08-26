using System.Diagnostics;
using System.Globalization;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// The reason the Job Object exists (architecture F.5 #1).
///
/// Process.Kill(entireProcessTree: true) walks a parent chain a descendant can escape, so
/// it is not accepted as the guarantee. Job membership is inherited by every process a
/// member creates, which is what this test proves: ending the run ends parent, child and
/// grandchild.
/// </summary>
public sealed class ProcessTreeTests
{
    private static readonly TimeSpan ObservationDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Cancellation_kills_parent_child_and_grandchild()
    {
        using var workspace = TempWorkspace.Create();
        var reportPath = workspace.Combine("tree.txt");
        File.WriteAllText(reportPath, string.Empty);

        using var cancellation = new CancellationTokenSource();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(120), "spawn-tree", reportPath) with
        {
            GracefulShutdownPeriod = TimeSpan.FromSeconds(1),
        };

        var run = workspace.Runner.RunAsync(request, cancellation.Token);

        // Wait for evidence that all three levels exist before ending the run: polling a
        // file is deterministic, a fixed sleep would be flaky.
        var appeared = await ProcessLifecycleTests.WaitUntilAsync(
            () => ReadPids(reportPath).Count >= 3,
            ObservationDeadline);

        var pids = ReadPids(reportPath);
        Assert.True(appeared, $"only observed: {Describe(pids)}");
        Assert.Equal(3, pids.Count);
        Assert.True(pids.ContainsKey("PARENT"));
        Assert.True(pids.ContainsKey("CHILD"));
        Assert.True(pids.ContainsKey("GRANDCHILD"));
        Assert.All(pids.Values, pid => Assert.True(IsAlive(pid), $"pid {pid} should be alive before cancellation"));

        EvidenceLog.Write("ProcessTree observed alive: " + Describe(pids));

        await cancellation.CancelAsync();
        var result = await run;

        Assert.Equal(ProcessTerminationReason.Cancelled, result.TerminationReason);
        Assert.True(result.JobTerminated);
        Assert.Equal(pids["PARENT"], result.ProcessId);

        foreach (var entry in pids)
        {
            var dead = await ProcessLifecycleTests.WaitUntilAsync(() => !IsAlive(entry.Value), ObservationDeadline);
            Assert.True(dead, $"{entry.Key} pid {entry.Value} survived the run");
        }

        EvidenceLog.Write(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ProcessTree all dead after {result.Duration.TotalMilliseconds:F0}ms: {Describe(pids)}"));
    }

    [Fact]
    public async Task Hard_timeout_also_kills_the_whole_tree()
    {
        using var workspace = TempWorkspace.Create();
        var reportPath = workspace.Combine("tree-hard.txt");
        File.WriteAllText(reportPath, string.Empty);

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(6), "spawn-tree", reportPath);

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.HardTimeout, result.TerminationReason);

        var pids = ReadPids(reportPath);
        Assert.Equal(3, pids.Count);

        foreach (var entry in pids)
        {
            var dead = await ProcessLifecycleTests.WaitUntilAsync(() => !IsAlive(entry.Value), ObservationDeadline);
            Assert.True(dead, $"{entry.Key} pid {entry.Value} survived the hard timeout");
        }

        EvidenceLog.Write("ProcessTree hard timeout all dead: " + Describe(pids));
    }

    private static Dictionary<string, int> ReadPids(string path)
    {
        var pids = new Dictionary<string, int>(StringComparer.Ordinal);

        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return pids;
        }

        foreach (var line in lines)
        {
            var parts = line.Trim().Split('=');
            if (parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var pid))
            {
                pids[parts[0]] = pid;
            }
        }

        return pids;
    }

    internal static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Describe(Dictionary<string, int> pids) =>
        string.Join(", ", pids.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}
