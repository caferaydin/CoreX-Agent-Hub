using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Interop;

/// <summary>
/// The exact Win32 limit fields one <see cref="ProcessResourceLimits"/> turns into.
///
/// Separated from the P/Invoke so the translation can be read and tested as a value rather
/// than inferred from whether a child process misbehaved. Two things go wrong in this kind of
/// mapping and both are silent: a flag set without its value, and a value written into the
/// neighbouring field - <c>JobMemoryLimit</c> and <c>ProcessMemoryLimit</c> sit next to each
/// other in the same structure and mean very different things. Neither mistake produces an
/// error; both produce a job that is not limited the way anyone asked. So the mapping is a
/// pure function with its own table of tests.
/// </summary>
internal readonly record struct JobLimitPlan
{
    /// <summary>Always set. Closing the job ends everything still inside it.</summary>
    internal const uint KillOnJobClose = 0x00002000;

    internal const uint ActiveProcess = 0x00000008;
    internal const uint JobMemory = 0x00000200;
    internal const uint ProcessMemory = 0x00000100;

    private JobLimitPlan(uint flags, uint activeProcessLimit, ulong processMemory, ulong jobMemory)
    {
        LimitFlags = flags;
        ActiveProcessLimit = activeProcessLimit;
        ProcessMemoryLimitBytes = processMemory;
        JobMemoryLimitBytes = jobMemory;
    }

    public uint LimitFlags { get; }

    public uint ActiveProcessLimit { get; }

    public ulong ProcessMemoryLimitBytes { get; }

    public ulong JobMemoryLimitBytes { get; }

    /// <summary>
    /// Builds the plan for a set of requested ceilings.
    ///
    /// Kill-on-close is unconditional: it is what makes the job a containment boundary at all,
    /// and adding a quota must never be the reason a tree stops being reaped. Every other flag
    /// appears only when its own value was supplied, and an absent ceiling leaves its field at
    /// zero - where Windows ignores it, because the flag that would read it is not set.
    /// </summary>
    public static JobLimitPlan For(ProcessResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var flags = KillOnJobClose;
        uint activeProcess = 0;
        ulong processMemory = 0;
        ulong jobMemory = 0;

        if (limits.ActiveProcessLimit is { } processes)
        {
            flags |= ActiveProcess;
            activeProcess = (uint)processes;
        }

        if (limits.JobMemoryLimitBytes is { } jobBytes)
        {
            flags |= JobMemory;
            jobMemory = (ulong)jobBytes;
        }

        if (limits.ProcessMemoryLimitBytes is { } processBytes)
        {
            flags |= ProcessMemory;
            processMemory = (ulong)processBytes;
        }

        return new JobLimitPlan(flags, activeProcess, processMemory, jobMemory);
    }
}
