using System.Diagnostics;
using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Answers whether the process that claimed an execution is still the one running.
///
/// A process id alone proves nothing, because operating systems reuse them: a recycled id
/// belonging to an unrelated program would make a dead execution look alive for ever. The
/// start time is what distinguishes them.
/// </summary>
public sealed class ProcessLivenessProbe : IProcessLivenessProbe
{
    /// <summary>Clock resolution differs between what was recorded and what is read back.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public int CurrentProcessId { get; } = Environment.ProcessId;

    public DateTimeOffset CurrentHostStartedAtUtc { get; } =
        new(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

    public bool IsAlive(int processId, DateTimeOffset hostStartedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                return false;
            }

            var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

            // Same id, different start: a different program wearing the same number.
            return (started - hostStartedAtUtc).Duration() <= StartTimeTolerance;
        }
        catch (ArgumentException)
        {
            // No such process.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
