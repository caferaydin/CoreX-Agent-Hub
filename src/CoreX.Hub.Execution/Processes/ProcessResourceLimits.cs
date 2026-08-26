namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// How much of the machine one run may consume.
///
/// Three optional ceilings and nothing else. There is deliberately no notion here of an
/// organization, a provider, an agent or a task: this is the low level process boundary, and
/// what it needs to know is a number of processes and a number of bytes. Who decided those
/// numbers, and why, belongs to whoever built the request.
///
/// Every value is optional and none has a default, because "unset" and "unlimited" are the
/// same thing to Windows but very different things to a reader. A missing value means that
/// specific quota is not enabled at all - the corresponding Win32 flag is never set, and the
/// native field is left at zero where it is ignored. It does not mean zero, and there is no
/// sentinel to misread: the type cannot express "limit of nothing".
///
/// What this is not: a timeout. Soft, idle and hard deadlines remain the authority on how
/// long a run may take, and nothing here replaces or interacts with them. A process can sit
/// comfortably inside every ceiling and still be ended by the hard deadline, or hit a ceiling
/// long before any deadline is near.
/// </summary>
public sealed record ProcessResourceLimits
{
    /// <summary>No optional quota requested. The job is still killed when it closes.</summary>
    public static ProcessResourceLimits None { get; } = new();

    /// <summary>
    /// How many processes may be alive in the job at once, the root included.
    ///
    /// Job Object semantics count every member, and the process the Hub launched is a member
    /// like any other - so a limit of one is a run that may not spawn anything, not a run
    /// with one helper. That is measured rather than assumed; see the resource limit tests.
    /// </summary>
    public int? ActiveProcessLimit { get; init; }

    /// <summary>
    /// Total committed memory for everything in the job, added together.
    ///
    /// The aggregate is the point: a run that stays under a per process ceiling by spawning
    /// twenty processes has not been contained by a per process ceiling.
    /// </summary>
    public long? JobMemoryLimitBytes { get; init; }

    /// <summary>Committed memory ceiling applied to each process in the job separately.</summary>
    public long? ProcessMemoryLimitBytes { get; init; }

    /// <summary>True when at least one optional ceiling was asked for.</summary>
    public bool Any =>
        ActiveProcessLimit is not null
        || JobMemoryLimitBytes is not null
        || ProcessMemoryLimitBytes is not null;

    /// <summary>
    /// Refuses a ceiling that could never be honoured.
    ///
    /// Nothing is clamped. A caller that asked for a limit of zero has said something the
    /// system cannot act on, and quietly turning it into "unlimited" would answer a security
    /// question with a guess - so the request is refused and no child is started.
    /// </summary>
    public void Validate()
    {
        Require(ActiveProcessLimit, nameof(ActiveProcessLimit));
        Require(JobMemoryLimitBytes, nameof(JobMemoryLimitBytes));
        Require(ProcessMemoryLimitBytes, nameof(ProcessMemoryLimitBytes));
    }

    private static void Require(long? value, string name)
    {
        if (value is { } limit && limit <= 0)
        {
            throw new ProcessRequestException(
                $"{name} must be greater than zero when set; {limit} is not a limit the operating system can enforce.");
        }
    }
}
