using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>
/// The trusted control channel into a hard isolated environment.
///
/// Two identities, and the difference between them is the whole security story. Workspace
/// control - git, the transfer, the layout - runs as the profile's service user, because the
/// controlled repository belongs to it and the agent must never be its administrator. Ownership
/// setup runs as root, because giving a file to another user is a privileged operation and there
/// is no way to establish the boundary without being allowed to impose it.
///
/// The untrusted agent workload does not come through here at all. It goes through the Phase
/// 10C-2 process executor, inside a systemd unit with its own ceilings. This channel is for the
/// Hub's own short, bounded commands, and every one of them has a deadline stated where it is
/// issued rather than inherited from somewhere.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardWorkspaceControl
{
    /// <summary>
    /// The identity that may give a file to another user.
    ///
    /// Used only for ownership setup, and only inside the execution root. Everything else - every
    /// git command, every read - runs as the service user, so a mistake here is a mistake in a
    /// handful of chown and chmod calls rather than in the whole workspace.
    /// </summary>
    private const string OwnershipIdentity = "root";

    /// <summary>Layout and transfer work; short, but a stopped distribution has to boot first.</summary>
    private static readonly TimeSpan SetupDeadline = TimeSpan.FromMinutes(2);

    /// <summary>Git against a real repository. Generous, because a first checkout is real work.</summary>
    private static readonly TimeSpan GitDeadline = TimeSpan.FromMinutes(10);

    private readonly IWslExecutionGateway _gateway;
    private readonly HardIsolationProfile _profile;

    public HardWorkspaceControl(
        IWslExecutionGateway gateway,
        HardIsolationProfile profile,
        HardWorkspaceLayout layout)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public HardWorkspaceLayout Layout { get; }

    public HardIsolationProfile Profile => _profile;

    /// <summary>A workspace control command, as the service identity that owns the repository.</summary>
    public Task<WslCallResult> ServiceAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? stdin = null,
        TimeSpan? deadline = null) =>
        RunAsync(_profile.ServiceUser, arguments, ct, stdin, deadline ?? GitDeadline);

    /// <summary>Ownership setup, as the only identity that can perform it.</summary>
    public Task<WslCallResult> OwnershipAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(OwnershipIdentity, arguments, ct, null, SetupDeadline);

    /// <summary>A git command with the Hub's hardened configuration already in force.</summary>
    public Task<WslCallResult> GitAsync(CancellationToken ct, params string[] arguments) =>
        ServiceAsync(HardGitCommands.Hardened(Layout, arguments), ct);

    /// <summary>The same, plus the Hub's fixed commit identity. Only the result commit uses it.</summary>
    public Task<WslCallResult> GitAsHubAsync(CancellationToken ct, params string[] arguments) =>
        ServiceAsync(HardGitCommands.HardenedWithHubIdentity(Layout, arguments), ct);

    /// <summary>Reads the first line of a command's output, or null when it produced none.</summary>
    public static string? FirstLine(WslCallResult result) =>
        result.Ok ? result.Lines.FirstOrDefault() : null;

    /// <summary>
    /// Whether a path is physically inside a root, as the filesystem sees it.
    ///
    /// The string check alone is not enough and that is the point of asking the machine. A
    /// symlink inside the worktree can name a target anywhere while every character of the path
    /// still sits under the root, so the path is resolved first and the containment decided on
    /// what it resolved to. The agent may create symlinks; the Hub simply does not follow one
    /// out of its own boundary.
    /// </summary>
    public async Task<bool> IsInsideAsync(string root, string candidate, CancellationToken ct)
    {
        if (!LinuxPath.TryContain(root, candidate, out _))
        {
            return false;
        }

        var resolved = FirstLine(await ServiceAsync(HardGitCommands.Resolve(candidate), ct, deadline: SetupDeadline));

        return resolved is { Length: > 0 } && LinuxPath.TryContain(root, resolved, out _);
    }

    private Task<WslCallResult> RunAsync(
        string user,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? stdin,
        TimeSpan deadline) =>
        _gateway.RunAsync(
            _profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = user,
                HardTimeout = deadline,
                StdinPayload = stdin,

                // A transferred artefact arrives base64 encoded on this channel, and a
                // repository of any size makes that large. Diff and status output is bounded by
                // the same ceiling for the same reason: evidence, not logs.
                MaxCapturedCharactersPerStream = 64 * 1024 * 1024,
            },
            ct);
}
