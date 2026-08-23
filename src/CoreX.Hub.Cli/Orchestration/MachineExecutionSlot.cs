using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// One execution at a time for one Hub installation, across processes.
///
/// The orchestrator's own limit is a semaphore, which is the right answer inside a process
/// and says nothing at all about a second command line invocation. Two of those would drive
/// git against the same repository at the same time, so the rule has to hold where the
/// processes can actually see each other.
///
/// An exclusive file handle is what does that here. It is deliberately not a mutex: a
/// mutex is owned by a thread, and an async command does not stay on one. It is deliberately
/// not a named semaphore either, because a semaphore's count is not restored when its holder
/// is killed - and a Hub that could never run again after one crash would be worse than no
/// limit at all. A file handle is released by the operating system when the process ends,
/// however it ends.
///
/// Scoped to the installation rather than to the machine: two separate Hub data directories
/// are two separate installations, and neither has any business blocking the other.
/// </summary>
public sealed class MachineExecutionSlot : IHubExecutionSlot
{
    /// <summary>Named so an operator who finds it knows what it is.</summary>
    public const string FileName = "execution.lock";

    private readonly string _path;

    public MachineExecutionSlot(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public IDisposable? TryClaim()
    {
        try
        {
            return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Held by another Hub process. Not an error condition to report as a failure:
            // the caller refuses the run and says who has it.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
