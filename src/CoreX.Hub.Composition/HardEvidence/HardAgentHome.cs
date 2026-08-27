using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardEvidence;

/// <summary>Where a hard isolated agent's own state lives, or why there is no such place.</summary>
/// <param name="Path">The account's home, when it is usable. Empty otherwise.</param>
public sealed record HardAgentHomeResult(bool IsUsable, string Path, string Detail)
{
    public static HardAgentHomeResult Unusable(string detail) => new(false, string.Empty, detail);
}

/// <summary>
/// Resolves the directory a hard isolated agent's provider authentication would live in.
///
/// This slice does not authenticate anything. What it establishes is the operating system
/// condition that has to hold before any provider could: the agent's own state belongs to the
/// agent, inside the environment, on the environment's own storage - and nowhere else.
///
/// Resolved from the passwd database rather than composed from a string. <c>/home/&lt;user&gt;</c>
/// is a convention, not a fact: a system account's home is frequently somewhere else entirely,
/// often a directory it does not own and cannot write. Concatenating the name would produce a
/// path that looks right, fails silently, and would have been reported as isolation.
///
/// Three things are then required, and each is a different failure:
///
///   * the home has to exist and be a directory, or there is nowhere for state to live;
///   * it has to be owned by the agent, or the agent cannot keep its own session there - and
///     something else can read it;
///   * it has to be on the environment's own filesystem, because a home under a mounted host
///     volume is the host's directory wearing a Linux name.
///
/// Deliberately provider-neutral. There is no field here for a Claude home, a Codex home or a
/// token, and <see cref="HardIsolationProfile"/> has none either: isolation supplies identities
/// and boundaries, and where a particular provider keeps its own session inside them is that
/// provider adapter's business, later.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardAgentHome
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    private readonly IWslExecutionGateway _gateway;
    private readonly HardIsolationProfile _profile;

    internal HardAgentHome(IWslExecutionGateway gateway, HardIsolationProfile profile)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public static HardAgentHome Create(HardIsolationProfile profile, string hostWorkingDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!WslExecutionGateway.TryCreate(hostWorkingDirectory, out var gateway, out var problem))
        {
            throw new InvalidOperationException($"resolving an agent home needs a reachable environment: {problem}");
        }

        return new HardAgentHome(gateway!, profile);
    }

    internal static HardAgentHome Scripted(IWslExecutionGateway gateway, HardIsolationProfile profile) =>
        new(gateway, profile);

    public async Task<HardAgentHomeResult> ResolveAsync(CancellationToken ct)
    {
        var agent = _profile.AgentUser.Trim();

        var entry = await RunAsync(HardEvidenceCommands.PasswdEntry(agent), ct);

        if (!entry.Ok || entry.Lines.FirstOrDefault() is not { Length: > 0 } line)
        {
            return HardAgentHomeResult.Unusable($"the environment has no account '{agent}'.");
        }

        // name:password:uid:gid:gecos:home:shell
        var fields = line.Split(':');

        if (fields.Length < 6 || fields[5].Length == 0)
        {
            return HardAgentHomeResult.Unusable($"the account '{agent}' declares no home directory.");
        }

        var home = fields[5];

        if (!LinuxPath.TryNormalize(home, out var segments, out var problem))
        {
            return HardAgentHomeResult.Unusable(
                $"the home of '{agent}' is not usable as isolated storage: {problem}");
        }

        var path = LinuxPath.Join(segments);
        var described = await RunAsync(HardEvidenceCommands.Describe(path), ct);

        if (!described.Ok || described.Lines.FirstOrDefault() is not { Length: > 0 } description)
        {
            return HardAgentHomeResult.Unusable($"the home of '{agent}' ('{path}') does not exist.");
        }

        var parts = description.Split('|');

        if (parts.Length < 3 || !parts[2].Contains("directory", StringComparison.OrdinalIgnoreCase))
        {
            return HardAgentHomeResult.Unusable($"the home of '{agent}' ('{path}') is not a directory.");
        }

        if (!string.Equals(parts[0], agent, StringComparison.Ordinal))
        {
            // Owned by somebody else. The agent could not keep its own session there, and
            // whoever does own it could read one that was.
            return HardAgentHomeResult.Unusable(
                $"the home of '{agent}' ('{path}') is owned by '{parts[0]}'; an agent's own state has to "
                + "belong to the agent.");
        }

        return new HardAgentHomeResult(true, path, $"'{agent}' owns '{path}' (mode {parts[1]}).");
    }

    private Task<WslCallResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        _gateway.RunAsync(
            _profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = _profile.ServiceUser.Trim(),
                HardTimeout = Deadline,
            },
            ct);
}
