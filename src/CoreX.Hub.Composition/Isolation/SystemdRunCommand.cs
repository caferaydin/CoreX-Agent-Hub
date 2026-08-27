using System.Globalization;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// The argument vector that runs one workload inside a transient systemd service.
///
/// A pure function, and separated out for that reason: this is where containment is actually
/// requested, and every property in it is a security decision that should be provable without a
/// machine. A missing <c>TasksMax</c> or a <c>KillMode</c> that is not <c>control-group</c>
/// produces a run that looks completely normal and is not contained, which is exactly the kind
/// of defect a test has to be able to see.
///
/// The shape was measured against the installed systemd rather than taken from documentation
/// (systemd 259). Every option below exists in that version's <c>systemd-run --help</c>, and the
/// behaviour each one is relied on for was verified on the machine:
///
///   --quiet            keeps systemd-run's own chatter out of the workload's stdout
///   --wait             blocks until the service is done, and returns its exit status
///   --pipe             connects the workload's stdin/stdout/stderr straight through
///   --collect          unloads the unit afterwards even when it failed, so a Hub that dies
///                      mid-run leaves nothing behind to reset
///   --unit=            the Hub-owned identity, never a name from anywhere else
///   --uid=             the untrusted workload's Linux identity
///   --working-directory=
///   --property=        the ceilings, below
///   --setenv=          the workload's explicit environment
///
/// There is no shell anywhere in it. The executable and each argument stay separate vector
/// entries, so there is no command line to quote and nothing for a quoting rule to get wrong.
/// <c>systemd-run</c> is orchestration; it is not an interpreter.
/// </summary>
public static class SystemdRunCommand
{
    public const string Executable = "/usr/bin/systemd-run";

    public const string SystemctlExecutable = "/usr/bin/systemctl";

    /// <summary>
    /// Ends the entire control group rather than one process.
    ///
    /// The alternative modes end the main process and leave its descendants running, which for
    /// an agent that spawns a toolchain is the difference between stopping a run and appearing
    /// to stop one.
    /// </summary>
    public const string KillMode = "control-group";

    /// <summary>
    /// Builds the invocation, or explains what is wrong with the request.
    ///
    /// The policy comes from the profile and is re-checked here rather than trusted. Phase 10C-1
    /// already refuses a profile without finite ceilings, and this is the second lock on the same
    /// door: the primitive is callable directly, and a caller that reached it with a zero limit
    /// must not get an uncontained run out of it.
    /// </summary>
    public static bool TryBuild(
        HardIsolationProfile profile,
        HardProcessRequest request,
        string unitName,
        out IReadOnlyList<string> arguments,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        arguments = [];
        problem = null;

        if (!HardUnitName.IsHubOwned(unitName))
        {
            problem = $"'{unitName}' is not a unit name this Hub created.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Executable) || !request.Executable.StartsWith('/'))
        {
            problem = "the workload executable must be an absolute path inside the environment.";
            return false;
        }

        if (request.Arguments.Any(a => a is null))
        {
            problem = "the workload argument vector contains a null entry.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.AgentUser))
        {
            problem = "the profile names no agent user; a hard isolated workload never runs as root.";
            return false;
        }

        if (!LinuxPath.TryContain(profile.ExecutionRoot, request.WorkingDirectory, out var containment))
        {
            problem = $"the working directory is refused: {containment}";
            return false;
        }

        if (profile.ProcessLimit <= 0)
        {
            problem = "the profile declares no finite process ceiling, so TasksMax cannot be set.";
            return false;
        }

        if (profile.MemoryLimitBytes <= 0)
        {
            problem = "the profile declares no finite memory ceiling, so MemoryMax cannot be set.";
            return false;
        }

        if (request.HardTimeout <= TimeSpan.Zero)
        {
            problem = "the request declares no hard runtime, so RuntimeMaxSec cannot be set.";
            return false;
        }

        foreach (var entry in request.Environment)
        {
            if (!IsUsableEnvironmentName(entry.Key))
            {
                problem = $"'{entry.Key}' is not a usable environment variable name.";
                return false;
            }

            if (entry.Value is null || entry.Value.Contains('\0', StringComparison.Ordinal))
            {
                problem = $"the value for '{entry.Key}' is not representable as an environment value.";
                return false;
            }
        }

        var built = new List<string>
        {
            Executable,
            "--quiet",
            "--wait",
            "--pipe",
            "--collect",
            "--unit=" + unitName,
            "--uid=" + profile.AgentUser.Trim(),
            "--working-directory=" + LinuxPath.Join(NormalizedWorkingDirectory(request)),

            // Every ceiling is stated on every run. None of them is conditional, because a run
            // that is missing one is not a slightly less contained run - it is an uncontained one
            // in the dimension that was left out.
            "--property=TasksMax=" + profile.ProcessLimit.ToString(CultureInfo.InvariantCulture),
            "--property=MemoryMax=" + profile.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture),

            // MemoryMax on its own is not a memory ceiling, which was measured rather than
            // reasoned about: a workload asked to allocate 192 MiB under a 64 MiB MemoryMax
            // reached 192 MiB, because everything over the ceiling went to swap. The declared
            // limit is a limit on what the run may consume, so the swap allowance is zero and
            // the ceiling is the whole of it.
            "--property=MemorySwapMax=0",
            "--property=KillMode=" + KillMode,
            "--property=RuntimeMaxSec=" + RuntimeSeconds(request.HardTimeout).ToString(CultureInfo.InvariantCulture),
        };

        // Ordered, so the same request always produces the same vector and a test can compare
        // one against a literal rather than against a set.
        foreach (var entry in request.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            built.Add("--setenv=" + entry.Key + "=" + entry.Value);
        }

        // Everything after this separator is the workload, which is why the workload cannot
        // introduce another option however its arguments are spelled.
        built.Add("--");
        built.Add(request.Executable.Trim());
        built.AddRange(request.Arguments);

        arguments = built;
        return true;
    }

    /// <summary>The command that ends a run, and the only one this system uses for that.</summary>
    public static bool TryBuildStop(string unitName, out IReadOnlyList<string> arguments, out string? problem)
    {
        arguments = [];
        problem = null;

        if (!HardUnitName.IsHubOwned(unitName))
        {
            // The important half of this check is what it refuses: an arbitrary unit name, from
            // an API caller or a task, would otherwise be a way to stop anything on the machine.
            problem = $"'{unitName}' is not a unit name this Hub created; it will not be stopped.";
            return false;
        }

        arguments = [SystemctlExecutable, "stop", unitName];
        return true;
    }

    /// <summary>Asks whether a unit name is already taken, so a collision can fail closed.</summary>
    public static IReadOnlyList<string> BuildLoadStateQuery(string unitName) =>
        [SystemctlExecutable, "show", unitName, "--property=LoadState"];

    /// <summary>
    /// How much longer the environment's ceiling runs than the host's.
    ///
    /// Long enough that the host wins the race whenever the Hub is alive - it notices its own
    /// deadline, stops the unit, and reports a timeout the caller can act on. Short enough that
    /// a unit nobody is watching any more is bounded soon afterwards rather than eventually.
    /// </summary>
    public const int RuntimeCeilingGraceSeconds = 15;

    /// <summary>
    /// The runtime ceiling the environment itself is given, in whole seconds.
    ///
    /// Rounded up rather than down, never below one, and deliberately longer than the caller's
    /// hard deadline. It is not a second opinion about how long the work may take: it is the
    /// bound that still holds when the Hub has gone and there is nobody left to enforce the
    /// first one. See <see cref="Application.Isolation.HardProcessRequest.HardTimeout"/>.
    /// </summary>
    public static long RuntimeSeconds(TimeSpan hardTimeout) =>
        Math.Max(1, (long)Math.Ceiling(hardTimeout.TotalSeconds)) + RuntimeCeilingGraceSeconds;

    private static IReadOnlyList<string> NormalizedWorkingDirectory(HardProcessRequest request)
    {
        LinuxPath.TryNormalize(request.WorkingDirectory, out var segments, out _);
        return segments;
    }

    private static bool IsUsableEnvironmentName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('=', StringComparison.Ordinal)
        && !name.Contains('\0', StringComparison.Ordinal);
}
