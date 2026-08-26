namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// A way for a test to make one native launch step fail on purpose.
///
/// The guarantees this slice exists to provide are all about what happens when a launch goes
/// wrong halfway: the child must never run if it could not be put in the job, and must never
/// be left suspended for ever if it could not be resumed. Those paths cannot be reached by
/// waiting for the machine to misbehave - <c>AssignProcessToJobObject</c> does not fail on a
/// healthy system - so the only honest way to test them is to ask the launcher to fail there.
///
/// Deliberately internal and instance-scoped rather than a static switch: it is carried on
/// one <see cref="ProcessRunner"/>, so a test that injects a fault cannot affect a run
/// happening beside it, and no caller outside this assembly can reach it at all. Production
/// always uses <see cref="None"/>, where every property is false and every hook is null, and
/// the launcher calls Win32 directly.
/// </summary>
internal sealed record ProcessLaunchFaults
{
    /// <summary>What production uses. No injection, no hooks, no behaviour change.</summary>
    public static ProcessLaunchFaults None { get; } = new();

    /// <summary>Simulates a Job Object that could not be created at all.</summary>
    public bool FailJobCreation { get; init; }

    /// <summary>
    /// Simulates <c>SetInformationJobObject</c> refusing the requested resource limits.
    ///
    /// The job exists; the ceilings could not be applied to it. What must follow is that no
    /// child is created - not a warning and a weaker job.
    /// </summary>
    public bool FailJobConfiguration { get; init; }

    /// <summary>Simulates <c>CreateProcessW</c> refusing to start the child.</summary>
    public bool FailCreateProcess { get; init; }

    /// <summary>
    /// Simulates <c>AssignProcessToJobObject</c> failing while the child is still suspended.
    ///
    /// The child exists and has executed nothing. Whether it stays that way is the property
    /// under test.
    /// </summary>
    public bool FailAssign { get; init; }

    /// <summary>Simulates <c>ResumeThread</c> failing after a successful assignment.</summary>
    public bool FailResume { get; init; }

    /// <summary>
    /// Runs after the child exists and has been assigned, but before it is resumed.
    ///
    /// The seam for the cancellation-before-resume case: a test cancels here and then asserts
    /// the child never ran. Nothing in production sets it.
    /// </summary>
    public Action? BeforeResume { get; init; }
}
