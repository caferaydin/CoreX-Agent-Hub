using CoreX.Hub.Application.Configuration;

namespace CoreX.Hub.Application.Isolation;

/// <summary>The mechanisms that can provide a hard boundary. A closed set, not a registry.</summary>
public enum HardIsolationBackend
{
    /// <summary>A per organization WSL2 distribution. The only candidate Phase 10 accepts.</summary>
    Wsl2,
}

/// <summary>
/// A hard isolation configuration that has been checked for internal sense.
///
/// The distinction this type carries is the one the whole slice rests on: parsing succeeded,
/// therefore the operator expressed a coherent intent. It says nothing whatever about the
/// machine. A profile can be perfectly formed and name a distribution that has never existed,
/// and the type system deliberately offers no way to confuse the two - readiness is a separate
/// result, produced by asking the operating system.
/// </summary>
public sealed record HardIsolationProfile
{
    private HardIsolationProfile()
    {
    }

    public required HardIsolationBackend Backend { get; init; }

    public required string EnvironmentId { get; init; }

    public required string AgentUser { get; init; }

    public required string ServiceUser { get; init; }

    public required string ExecutionRoot { get; init; }

    public required string EvidenceRoot { get; init; }

    public required int ProcessLimit { get; init; }

    public required long MemoryLimitBytes { get; init; }

    /// <summary>
    /// Reads a profile, or says exactly what is wrong with it.
    ///
    /// Everything here is a statement the configuration makes about itself, so it can all be
    /// decided without touching the machine. Refusing at this level keeps the readiness probes
    /// honest: they answer questions about a real environment rather than reporting that a
    /// path was blank.
    /// </summary>
    public static bool TryRead(
        HardIsolationConfig? config,
        out HardIsolationProfile? profile,
        out string? problem)
    {
        profile = null;
        problem = null;

        if (config is null)
        {
            problem = "hard isolation is declared but no hardIsolation configuration was supplied.";
            return false;
        }

        if (!Enum.TryParse<HardIsolationBackend>(config.Backend, ignoreCase: true, out var backend))
        {
            var supported = string.Join(", ", Enum.GetNames<HardIsolationBackend>());
            problem = $"hardIsolation.backend '{config.Backend}' is not supported; expected one of: {supported}.";
            return false;
        }

        foreach (var (value, name) in new[]
                 {
                     (config.EnvironmentId, "environmentId"),
                     (config.AgentUser, "agentUser"),
                     (config.ServiceUser, "serviceUser"),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problem = $"hardIsolation.{name} must be set.";
                return false;
            }
        }

        // The evidence store must be unreadable by the thing it is evidence about, which one
        // identity playing both parts cannot provide.
        if (string.Equals(config.AgentUser, config.ServiceUser, StringComparison.Ordinal))
        {
            problem =
                "hardIsolation.agentUser and hardIsolation.serviceUser must differ; the agent may not own the evidence store it is being observed into.";
            return false;
        }

        if (!TryReadRoot(config.ExecutionRoot, "executionRoot", out var executionRoot, out problem)
            || !TryReadRoot(config.EvidenceRoot, "evidenceRoot", out var evidenceRoot, out problem))
        {
            return false;
        }

        if (string.Equals(executionRoot, evidenceRoot, StringComparison.Ordinal))
        {
            problem = "hardIsolation.executionRoot and hardIsolation.evidenceRoot must be different directories.";
            return false;
        }

        // Finite, and explicitly stated. There is no product wide default to fall back on,
        // because ProcessRunner also drives verification and git and a number invented here
        // would silently become their ceiling too.
        if (config.ProcessLimit <= 0)
        {
            problem = "hardIsolation.processLimit must be greater than zero; a hard environment needs a finite process ceiling.";
            return false;
        }

        if (config.MemoryLimitBytes <= 0)
        {
            problem = "hardIsolation.memoryLimitBytes must be greater than zero; a hard environment needs a finite memory ceiling.";
            return false;
        }

        profile = new HardIsolationProfile
        {
            Backend = backend,
            EnvironmentId = config.EnvironmentId.Trim(),
            AgentUser = config.AgentUser.Trim(),
            ServiceUser = config.ServiceUser.Trim(),
            ExecutionRoot = executionRoot!,
            EvidenceRoot = evidenceRoot!,
            ProcessLimit = config.ProcessLimit,
            MemoryLimitBytes = config.MemoryLimitBytes,
        };

        return true;
    }

    /// <summary>
    /// A root must be an absolute Linux path.
    ///
    /// Only shape is judged here. Whether the path is really on a Linux filesystem rather than
    /// a mounted Windows drive is a question for the machine, and a readiness check asks it -
    /// a name can be made to look native without being it.
    /// </summary>
    private static bool TryReadRoot(string value, string name, out string? root, out string? problem)
    {
        root = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            problem = $"hardIsolation.{name} must be set.";
            return false;
        }

        var trimmed = value.Trim();

        if (!trimmed.StartsWith('/'))
        {
            problem = $"hardIsolation.{name} must be an absolute Linux path; '{trimmed}' is not.";
            return false;
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains(':', StringComparison.Ordinal))
        {
            problem = $"hardIsolation.{name} looks like a Windows path; a hard environment stores nothing on the host.";
            return false;
        }

        root = trimmed.TrimEnd('/');

        if (root.Length == 0)
        {
            problem = $"hardIsolation.{name} must name a directory, not the filesystem root.";
            return false;
        }

        return true;
    }
}
