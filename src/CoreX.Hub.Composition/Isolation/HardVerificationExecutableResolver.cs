using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// What a verification step's logical executable name means inside one organization's environment.
///
/// The whole security shape of this type is the direction information flows. A verification profile
/// may choose a name - "dotnet", "node" - and nothing else; the operator's per organization map is
/// the only thing that says what a name is; and the environment itself is the only thing that says
/// whether that answer is real. A repository can influence the arguments a tool is given and can
/// never influence which tool that is.
///
/// There is no host in this path anywhere. The configured value is a guest path, existence is
/// measured by the trusted service identity inside the environment, and the resolved result is a
/// guest path. <see cref="File.Exists(string)"/> would answer a question about the developer's
/// machine, which has nothing to do with what an isolated workload is about to execute, and would
/// answer it wrongly in both directions - refusing a real Linux binary and accepting a Windows one.
///
/// It never falls back to the host allowlist. A missing mapping is a refusal, because the
/// alternative is running a Windows path in a Linux namespace or inventing a translation between
/// two filesystems the Hub does not own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardVerificationExecutableResolver : IVerificationExecutableResolver
{
    /// <summary>
    /// Prefixes a verification tool may never live under.
    ///
    /// Everything here is either writable by the workload or shared with something outside the
    /// boundary. An executable taken from the execution root would be a binary the agent had just
    /// written being run as the verification of the agent's own work; one under the evidence root
    /// would be the record of the run choosing what runs. The shared surfaces are the ones the
    /// filesystem policy already denies for the same reason - they are not this environment's own
    /// storage.
    ///
    /// Checked against the physically resolved path, so a symlink from a respectable directory into
    /// the workspace is refused rather than followed.
    /// </summary>
    private static readonly string[] AlwaysUntrustedRoots = ["/run/user", "/tmp", "/var/tmp"];

    private readonly TrustedGuestFileTransfer _guest;
    private readonly HardIsolationProfile _profile;

    internal HardVerificationExecutableResolver(
        TrustedGuestFileTransfer guest,
        HardIsolationProfile profile)
    {
        _guest = guest ?? throw new ArgumentNullException(nameof(guest));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>
    /// Whether the configuration alone could ever satisfy a profile, with no machine involved.
    ///
    /// Asked during composition so that an organization whose map is missing an entry the profile
    /// needs is refused before an execution row exists, rather than after a workspace, an agent turn
    /// and a result commit. It deliberately proves nothing about the binaries themselves: that
    /// question needs the environment, and starting one to answer it here would mean booting a
    /// distribution to read a dictionary.
    /// </summary>
    public static IReadOnlyList<string> MissingMappings(
        HardIsolationProfile profile,
        IEnumerable<string> logicalNames)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return
        [
            .. (logicalNames ?? [])
                .Select(name => (name ?? string.Empty).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !profile.VerificationExecutables.ContainsKey(name))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<ExecutableResolution> ResolveAsync(
        string? logicalName,
        CancellationToken cancellationToken)
    {
        var name = (logicalName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return ExecutableResolution.Rejected("the step declares no executable");
        }

        if (!_profile.VerificationExecutables.TryGetValue(name, out var configured))
        {
            var known = _profile.VerificationExecutables.Count == 0
                ? "none are configured"
                : string.Join(", ", _profile.VerificationExecutables.Keys.Order(StringComparer.OrdinalIgnoreCase));

            // Named as the hard map explicitly, so an operator reading this is not left wondering
            // why a tool that plainly works on the host is not found. The host map is not consulted
            // and is not a fallback.
            return ExecutableResolution.Rejected(
                $"'{name}' is not mapped in this organization's hard verification executables "
                + $"(mapped: {known}); a hard organization never falls back to the host allowlist");
        }

        // Shape first, before the environment is asked anything. A value that is not a Linux path is
        // a configuration mistake, and running realpath on it would be asking the machine about a
        // string that could not have been right whatever it answered.
        if (!LinuxPath.TryNormalize(configured, out var segments, out var problem))
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' is not usable: {problem}");
        }

        var stated = LinuxPath.Join(segments);

        if (Untrusted(stated) is { } statedProblem)
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' {statedProblem}");
        }

        var facts = await _guest.DescribeAsync(stated, cancellationToken);

        if (facts is null)
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' at '{stated}' could not be "
                + "resolved inside the environment");
        }

        // The physical answer is judged, not the name it was reached by. A link is an ordinary thing
        // for a distribution to install; a link out of the trusted part of the filesystem is not.
        if (!LinuxPath.TryNormalize(facts.PhysicalPath, out var physicalSegments, out var physicalProblem))
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' resolves to "
                + $"'{facts.PhysicalPath}', which is not a usable guest path: {physicalProblem}");
        }

        var physical = LinuxPath.Join(physicalSegments);

        if (Untrusted(physical) is { } physicalUntrusted)
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' resolves to '{physical}', which "
                + physicalUntrusted);
        }

        if (facts.Type is null)
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' does not exist at '{physical}' "
                + "inside the environment");
        }

        if (!facts.IsRegularFile)
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' is a {facts.Type} at "
                + $"'{physical}', not a regular file");
        }

        if (!facts.IsExecutable)
        {
            return ExecutableResolution.Rejected(
                $"the hard verification executable configured for '{name}' at '{physical}' is not "
                + "executable inside the environment");
        }

        return ExecutableResolution.Allowed(physical);
    }

    /// <summary>
    /// Why a path may not be a verification tool, or null when there is no reason.
    ///
    /// Mechanical, and that is what makes it a rule rather than a judgement: the execution root and
    /// the evidence root are named by the same trusted profile that names the tool, so "is this
    /// binary somewhere the workload can write" is a containment question this can answer without
    /// modelling users, groups or permission bits.
    /// </summary>
    private string? Untrusted(string path)
    {
        if (LinuxPath.TryContain(_profile.ExecutionRoot, path, out _))
        {
            return "is inside the execution root, where the workload writes; a tool the agent could "
                   + "replace is not a tool that can verify the agent";
        }

        if (LinuxPath.TryContain(_profile.EvidenceRoot, path, out _))
        {
            return "is inside the evidence root, which records runs and does not supply them";
        }

        foreach (var root in AlwaysUntrustedRoots)
        {
            if (LinuxPath.TryContain(root, path, out _))
            {
                return $"is under '{root}', which is scratch space rather than installed tooling";
            }
        }

        return null;
    }
}
