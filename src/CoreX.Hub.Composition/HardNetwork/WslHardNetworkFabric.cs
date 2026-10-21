using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// The trusted network plane, built and measured.
///
/// Two things live here that look similar and are not. Preparing the plane is machine-global
/// mutation: interfaces appear, a policy is loaded, forwarding is turned on. Verifying it changes
/// nothing and is what readiness calls. Keeping them apart is the reason a diagnostic can be
/// trusted at all - one that provisioned what it was measuring could always report success.
///
/// Everything the plane touches is shared with the operator's own environment, which is what makes
/// the small decisions here load-bearing. The Hub owns one table and never flushes another's. It
/// turns global forwarding on only if it was off, and turns it back off only when it was the one
/// that turned it on and nothing else still needs it. And because two Hub processes could be
/// running - a CLI beside an API - every mutation happens under a machine-wide lock rather than a
/// lock inside one process, which would be a lock that agreed with itself and nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslHardNetworkFabric : IHardNetworkFabric
{
    /// <summary>
    /// The machine-wide gate on shared-plane mutation.
    ///
    /// A named system object rather than anything in this process, because the CLI and the API are
    /// different processes and the state they would race over is the machine's.
    /// </summary>
    private const string MutationLockName = @"Global\CoreX.Hub.HardNetworkFabric";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Where the Hub records that it, and not the operator, enabled global forwarding.
    ///
    /// On the Hub's own side rather than inside an environment, and that placement is the whole
    /// point. The value it guards belongs to the shared plane every environment sees, but each
    /// environment has a filesystem of its own - a note written inside one is invisible to the
    /// next, so the Hub would forget what it had changed and leave the machine altered. It is also
    /// somewhere no agent can reach, which a note inside a shared environment mount would not be.
    /// </summary>
    private const string ForwardingMarkerFile = "hard-network-forwarding.owned";

    private readonly IWslExecutionGateway _gateway;
    private readonly string? _unavailable;
    private readonly string _hostDirectory;

    private WslHardNetworkFabric(IWslExecutionGateway gateway, string? unavailable, string hostDirectory)
    {
        _gateway = gateway;
        _unavailable = unavailable;
        _hostDirectory = hostDirectory;
    }

    public static WslHardNetworkFabric Create(string hostWorkingDirectory) =>
        WslExecutionGateway.TryCreate(hostWorkingDirectory, out var gateway, out var problem)
            ? new WslHardNetworkFabric(gateway!, null, hostWorkingDirectory)
            : new WslHardNetworkFabric(null!, problem, hostWorkingDirectory);

    internal static WslHardNetworkFabric Scripted(IWslExecutionGateway gateway) =>
        new(gateway, null, Path.GetTempPath());

    /// <summary>The Hub-side note that this build turned global forwarding on.</summary>
    private string ForwardingMarker => Path.Combine(_hostDirectory, ForwardingMarkerFile);

    // -- preparation --------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<HardNetworkAttachment?> PrepareAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);

        if (_unavailable is not null || !HardNetworkIdentity.IsNameable(organizationSlug))
        {
            return null;
        }

        using var gate = MutationLock.Acquire(MutationLockName, LockTimeout);

        if (!gate.Held)
        {
            return null;
        }

        var topology = await ReadTopologyAsync(profile, ct);

        if (topology.Mode != HardNetworkMode.Nat)
        {
            return null;
        }

        var attachment = HardNetworkIdentity.Describe(organizationSlug);

        // The pool has to be the Hub's before anything is put in it. A private range this build
        // finds convenient may be a range the machine already routes.
        if (await PoolCollidesAsync(profile, attachment, ct))
        {
            return null;
        }

        var holder = await EnsureHolderAsync(profile, attachment, ct);

        if (holder is null)
        {
            return null;
        }

        if (!await EnsureLinkAsync(profile, attachment, holder, ct))
        {
            return null;
        }

        if (!await EnsureForwardingAsync(profile, ct))
        {
            return null;
        }

        var attachments = await PresentAttachmentsAsync(profile, organizationSlug, attachment, holder, ct);

        return await LoadPolicyAsync(profile, topology, attachments, ct)
            ? attachments.Single(a => a.AttachmentId == attachment.AttachmentId)
            : null;
    }

    /// <summary>Starts the namespace holder if it is not already running, and reports it.</summary>
    private async Task<HolderState?> EnsureHolderAsync(
        HardIsolationProfile profile, HardNetworkAttachment attachment, CancellationToken ct)
    {
        var unit = HardNetworkIdentity.HolderUnit(attachment.AttachmentId);

        var running = await HolderAsync(profile, unit, ct);

        if (running is not null)
        {
            return running;
        }

        if (!(await RootAsync(profile, HardNetworkCommands.StartHolder(unit), ct)).Ok)
        {
            return null;
        }

        return await HolderAsync(profile, unit, ct);
    }

    /// <summary>What a holder unit currently is, or nothing if it is not holding anything.</summary>
    private async Task<HolderState?> HolderAsync(HardIsolationProfile profile, string unit, CancellationToken ct)
    {
        var state = HardWorkspace.HardWorkspaceControl.FirstLine(
            await RootAsync(profile, HardNetworkCommands.UnitProperty(unit, "SubState"), ct));

        if (!string.Equals(state, "running", StringComparison.Ordinal))
        {
            return null;
        }

        var pid = HardWorkspace.HardWorkspaceControl.FirstLine(
            await RootAsync(profile, HardNetworkCommands.UnitProperty(unit, "MainPID"), ct));

        if (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 1)
        {
            return null;
        }

        var reference = Reference(value);

        var identity = HardWorkspace.HardWorkspaceControl.FirstLine(
            await RootAsync(profile, HardNetworkCommands.NamespaceIdentity(reference), ct));

        if (identity is null || !identity.StartsWith("net:[", StringComparison.Ordinal))
        {
            return null;
        }

        return new HolderState(unit, value, reference, identity);
    }

    /// <summary>Where a live holder's namespace is reached. A path, because that is what systemd takes.</summary>
    private static string Reference(int pid) =>
        string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/ns/net");

    /// <summary>Creates the attachment's link pair and addresses it, if it is not already there.</summary>
    private async Task<bool> EnsureLinkAsync(
        HardIsolationProfile profile, HardNetworkAttachment attachment, HolderState holder, CancellationToken ct)
    {
        if (await LinkExistsAsync(profile, attachment.TrustedInterface, ct))
        {
            return true;
        }

        var pid = holder.Pid.ToString(CultureInfo.InvariantCulture);

        var steps = new[]
        {
            HardNetworkCommands.CreateVeth(attachment.TrustedInterface, attachment.NamespaceInterface),
            HardNetworkCommands.MoveIntoNamespace(attachment.NamespaceInterface, pid),
            HardNetworkCommands.AddAddress(Cidr(attachment.GatewayAddress, attachment.Subnet), attachment.TrustedInterface),
            HardNetworkCommands.BringUp(attachment.TrustedInterface),
            HardNetworkCommands.InNamespace(pid, HardNetworkCommands.AddAddress(
                Cidr(attachment.NamespaceAddress, attachment.Subnet), attachment.NamespaceInterface)),
            HardNetworkCommands.InNamespace(pid, HardNetworkCommands.BringUp(attachment.NamespaceInterface)),
            HardNetworkCommands.InNamespace(pid, HardNetworkCommands.BringUp("lo")),
            HardNetworkCommands.InNamespace(pid, HardNetworkCommands.DefaultRoute(attachment.GatewayAddress)),
        };

        foreach (var step in steps)
        {
            if (!(await RootAsync(profile, step, ct)).Ok)
            {
                // A half built attachment is worse than none: it would be present without being
                // classified, and the fabric check is what would have caught that.
                await RootAsync(profile, HardNetworkCommands.DeleteLink(attachment.TrustedInterface), CancellationToken.None);
                return false;
            }
        }

        return true;
    }

    private static string Cidr(string address, string subnet) =>
        address + "/" + subnet.Split('/')[1];

    /// <summary>
    /// Turns global forwarding on, and remembers whether it was this build that did it.
    ///
    /// The value is the machine's, not the Hub's. If the operator already had it on, it stays on
    /// afterwards and no marker is written - releasing something the Hub never took would be the
    /// Hub deciding how somebody else's machine should be configured.
    /// </summary>
    private async Task<bool> EnsureForwardingAsync(HardIsolationProfile profile, CancellationToken ct)
    {
        var current = HardWorkspace.HardWorkspaceControl.FirstLine(
            await RootAsync(profile, HardNetworkCommands.ReadForwarding(), ct));

        if (string.Equals(current, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (!(await RootAsync(profile, HardNetworkCommands.SetForwarding(true), ct)).Ok)
        {
            return false;
        }

        try
        {
            await File.WriteAllTextAsync(ForwardingMarker, "1", ct);
        }
        catch (IOException)
        {
            // The note is bookkeeping, not the boundary. Losing it means the Hub leaves a value it
            // set rather than resetting one it did not, which is the safe direction to fail in.
        }

        return true;
    }

    // -- verification -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReadinessCheck>> VerifyAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);

        if (_unavailable is { } problem)
        {
            return Unestablished($"The environment could not be reached: {problem}");
        }

        if (!HardNetworkIdentity.IsNameable(organizationSlug))
        {
            return Unestablished($"'{organizationSlug}' does not derive a usable attachment identity.");
        }

        var topology = await ReadTopologyAsync(profile, ct);
        var expected = HardNetworkIdentity.Describe(organizationSlug);

        var holder = await HolderAsync(profile, HardNetworkIdentity.HolderUnit(expected.AttachmentId), ct);

        var namespaceCheck = holder is null
            ? ReadinessCheck.Failed(
                HardIsolationChecks.OrgNetworkNamespaceReady,
                $"No holder is keeping a network namespace open for '{organizationSlug}', so the "
                + "organization has no network boundary of its own.")
            : ReadinessCheck.Passed(
                HardIsolationChecks.OrgNetworkNamespaceReady,
                $"'{organizationSlug}' holds {holder.Identity} through a Hub owned unit.");

        var fabric = await FabricCheckAsync(profile, topology, ct);
        var attachment = await AttachmentCheckAsync(profile, organizationSlug, expected, holder, ct);

        return [fabric, namespaceCheck, attachment];
    }

    /// <summary>
    /// Whether the trusted plane is exactly what the Hub expects, interfaces included.
    ///
    /// The interface comparison is the part that matters. The policy was measured to survive a
    /// restart that destroys every namespace and link it names, so a table whose text matches
    /// perfectly can be classifying nothing at all. Both directions are checked: a named interface
    /// that is gone, and a present Hub interface that nothing names.
    /// </summary>
    private async Task<ReadinessCheck> FabricCheckAsync(
        HardIsolationProfile profile, HardNetworkTopology topology, CancellationToken ct)
    {
        // Read as root, because reading the ruleset is a privileged operation: the kernel refuses a
        // netlink cache to anything without CAP_NET_ADMIN, so an unprivileged identity gets
        // "Operation not permitted" whether or not the table is there. Asking as the service user
        // made this check unpassable on every machine - the plane could be perfectly built and the
        // answer would still be that it could not be read.
        //
        // Every other read here stays with the service identity, which can genuinely answer them.
        // This is the one question that cannot be asked from outside the capability it inspects.
        var active = await RootAsync(profile, HardNetworkCommands.ActivePolicy(), ct);

        if (!active.Ok)
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.NetworkFabricReady,
                $"The Hub's network table '{NftablesRuleset.TableFamily} {NftablesRuleset.TableName}' could "
                + "not be read; the trusted plane is not built, or the environment has no policy engine.");
        }

        var present = await PresentInterfacesAsync(profile, ct);

        var named = active.Lines
            .SelectMany(l => l.Split('"'))
            .Where(t => t.StartsWith("cxr", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var missing = named.Except(present, StringComparer.Ordinal).ToList();
        var uncovered = present.Except(named, StringComparer.Ordinal).ToList();

        if (missing.Count > 0)
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.NetworkFabricReady,
                $"The policy classifies {string.Join(", ", missing)}, which the plane no longer has; "
                + "the table survived a restart that its interfaces did not, so it filters nothing.");
        }

        if (uncovered.Count > 0)
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.NetworkFabricReady,
                $"{string.Join(", ", uncovered)} is attached to the trusted plane and no rule names it; "
                + "the chain policy cannot be a denial here, so an unnamed attachment is unfiltered.");
        }

        if (present.Count == 0)
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.NetworkFabricReady,
                "The trusted plane holds no Hub attachment at all.");
        }

        if (!NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10,
                topology,
                present.Select(Recover).ToList(),
                out _,
                out var problem))
        {
            return ReadinessCheck.Unknown(
                HardIsolationChecks.NetworkFabricReady,
                $"The expected policy could not be rendered for this machine: {problem}");
        }

        return ReadinessCheck.Passed(
            HardIsolationChecks.NetworkFabricReady,
            $"The trusted plane carries {present.Count} attachment(s) and every one of them is classified.");
    }

    /// <summary>
    /// Whether a workload would actually land where this organization was assigned.
    ///
    /// Not the same question as the namespace existing. A launch against a missing namespace was
    /// measured starting the workload in a fresh empty one instead of failing, so what is compared
    /// here is the kernel identity a launch really produces.
    /// </summary>
    private async Task<ReadinessCheck> AttachmentCheckAsync(
        HardIsolationProfile profile,
        string organizationSlug,
        HardNetworkAttachment expected,
        HolderState? holder,
        CancellationToken ct)
    {
        if (holder is null)
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.OrgNetworkAttachmentReady,
                $"'{organizationSlug}' has no namespace to attach to.");
        }

        if (!await LinkExistsAsync(profile, expected.TrustedInterface, ct))
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.OrgNetworkAttachmentReady,
                $"'{organizationSlug}' has a namespace and no link into it; nothing it sent would be "
                + "classified as its own.");
        }

        var observed = HardWorkspace.HardWorkspaceControl.FirstLine(
            await AgentInNamespaceAsync(profile, holder, ["/usr/bin/readlink", "/proc/self/ns/net"], ct));

        if (!string.Equals(observed, holder.Identity, StringComparison.Ordinal))
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.OrgNetworkAttachmentReady,
                $"A workload launched for '{organizationSlug}' lands in {observed ?? "nowhere"} while its "
                + $"namespace is {holder.Identity}; starting is not evidence of starting in the right place.");
        }

        return ReadinessCheck.Passed(
            HardIsolationChecks.OrgNetworkAttachmentReady,
            $"A workload launched as the agent lands in {holder.Identity} on {expected.TrustedInterface}.");
    }

    // -- release ------------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task ReleaseAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);

        if (_unavailable is not null || !HardNetworkIdentity.IsNameable(organizationSlug))
        {
            return;
        }

        using var gate = MutationLock.Acquire(MutationLockName, LockTimeout);

        if (!gate.Held)
        {
            return;
        }

        var attachment = HardNetworkIdentity.Describe(organizationSlug);

        await RootAsync(profile, HardNetworkCommands.DeleteLink(attachment.TrustedInterface), ct);
        await RootAsync(
            profile, HardNetworkCommands.StopUnit(HardNetworkIdentity.HolderUnit(attachment.AttachmentId)), ct);

        var remaining = await PresentInterfacesAsync(profile, ct);

        if (remaining.Count > 0)
        {
            var topology = await ReadTopologyAsync(profile, ct);

            await LoadPolicyAsync(
                profile,
                topology,
                remaining.Select(Recover).ToList(),
                ct);

            return;
        }

        // The last attachment is gone, so the plane goes with it - including the global value, but
        // only if the Hub was the one that changed it.
        await RootAsync(profile, HardNetworkCommands.DeletePolicy(
            NftablesRuleset.TableFamily, NftablesRuleset.TableName), ct);
        await RootAsync(profile, HardNetworkCommands.DeletePolicy(
            NftablesRuleset.NatTableFamily, NftablesRuleset.NatTableName), ct);

        if (File.Exists(ForwardingMarker))
        {
            await RootAsync(profile, HardNetworkCommands.SetForwarding(false), ct);

            try
            {
                File.Delete(ForwardingMarker);
            }
            catch (IOException)
            {
            }
        }
    }

    // -- shared -------------------------------------------------------------------------------

    private static IReadOnlyList<ReadinessCheck> Unestablished(string detail) =>
    [
        ReadinessCheck.Unknown(HardIsolationChecks.NetworkFabricReady, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.OrgNetworkNamespaceReady, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.OrgNetworkAttachmentReady, detail),
    ];

    private async Task<HardNetworkTopology> ReadTopologyAsync(HardIsolationProfile profile, CancellationToken ct)
    {
        var addresses = await ServiceAsync(profile, HardNetworkCommands.Addresses(), ct);
        var routes = await ServiceAsync(profile, HardNetworkCommands.Routes(), ct);
        var resolver = await ServiceAsync(profile, HardNetworkCommands.Resolver(), ct);

        return addresses.Ok && routes.Ok
            ? HardNetworkTopology.Classify(addresses.Lines, routes.Lines, resolver.Ok ? resolver.Lines : [])
            : HardNetworkTopology.Unknown("The environment did not report its addresses or routes.");
    }

    /// <summary>Every Hub attachment interface the trusted plane currently holds.</summary>
    private async Task<IReadOnlyList<string>> PresentInterfacesAsync(
        HardIsolationProfile profile, CancellationToken ct)
    {
        var links = await ServiceAsync(profile, HardNetworkCommands.Links(), ct);

        if (!links.Ok)
        {
            return [];
        }

        return links.Lines
            .Select(l => l.Split(':', StringSplitOptions.TrimEntries))
            .Where(p => p.Length > 1)
            .Select(p => p[1].Split('@')[0].Trim())
            .Where(n => n.StartsWith("cxr", StringComparison.Ordinal) && n.Length == 11)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<bool> LinkExistsAsync(
        HardIsolationProfile profile, string device, CancellationToken ct) =>
        (await PresentInterfacesAsync(profile, ct)).Contains(device, StringComparer.Ordinal);

    /// <summary>
    /// Whether the machine already routes something inside the Hub's pool.
    ///
    /// Checked before anything is created, because a private range this build treats as its own may
    /// be one the operator's network already uses - and taking it would break their routing rather
    /// than isolate anything.
    /// </summary>
    private async Task<bool> PoolCollidesAsync(
        HardIsolationProfile profile, HardNetworkAttachment attachment, CancellationToken ct)
    {
        var routes = await ServiceAsync(profile, HardNetworkCommands.Routes(), ct);

        if (!routes.Ok)
        {
            return true;
        }

        foreach (var line in routes.Lines)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var destination = tokens.FirstOrDefault();

            if (destination is null || !destination.StartsWith("10.79.", StringComparison.Ordinal))
            {
                continue;
            }

            // A route inside the pool that leaves by a Hub interface is another attachment, which
            // is the normal case on a shared plane rather than a collision. Anything else in the
            // pool belongs to the machine, and taking it would break the operator's routing.
            var device = Device(tokens);

            if (HardNetworkIdentity.IdentityOf(device) is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The interface a route leaves by, or empty.</summary>
    private static string Device(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], "dev", StringComparison.Ordinal))
            {
                return tokens[i + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>Every attachment the plane should classify, this organization's included.</summary>
    private async Task<IReadOnlyList<HardNetworkAttachment>> PresentAttachmentsAsync(
        HardIsolationProfile profile,
        string organizationSlug,
        HardNetworkAttachment attachment,
        HolderState holder,
        CancellationToken ct)
    {
        var present = await PresentInterfacesAsync(profile, ct);

        var attachments = present
            .Where(i => !string.Equals(i, attachment.TrustedInterface, StringComparison.Ordinal))
            .Select(Recover)
            .ToList();

        attachments.Add(HardNetworkIdentity.Describe(organizationSlug, holder.Reference, holder.Identity));

        return attachments;
    }

    /// <summary>Recovers a derivable attachment from an interface the plane already holds.</summary>
    private static HardNetworkAttachment Recover(string trustedInterface) =>
        HardNetworkIdentity.FromAttachmentId(HardNetworkIdentity.IdentityOf(trustedInterface)!);

    private async Task<bool> LoadPolicyAsync(
        HardIsolationProfile profile,
        HardNetworkTopology topology,
        IReadOnlyList<HardNetworkAttachment> attachments,
        CancellationToken ct)
    {
        if (!NftablesRuleset.TryRender(HardNetworkPolicy.Phase10, topology, attachments, out var ruleset, out _))
        {
            return false;
        }

        return await WriteAndLoadAsync(profile, ruleset!.Text, "/run/corex-hard-policy.nft", ct)
               && await WriteAndLoadAsync(profile, ruleset.RenderNat(), "/run/corex-hard-nat.nft", ct);
    }

    private async Task<bool> WriteAndLoadAsync(
        HardIsolationProfile profile, string text, string path, CancellationToken ct)
    {
        var written = await RunAsync(profile, "root", ["/usr/bin/tee", path], ct, text);

        return written.Ok && (await RootAsync(profile, HardNetworkCommands.LoadPolicy(path), ct)).Ok;
    }

    private Task<WslCallResult> AgentInNamespaceAsync(
        HardIsolationProfile profile, HolderState holder, IReadOnlyList<string> command, CancellationToken ct) =>
        RootAsync(
            profile,
            [
                HardNetworkCommands.SystemdRun, "--quiet", "--pipe", "--wait", "--collect",
                "--uid", profile.AgentUser.Trim(),
                "--property=NetworkNamespacePath=" + holder.Reference,
                .. command,
            ],
            ct);

    private Task<WslCallResult> ServiceAsync(
        HardIsolationProfile profile, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(profile, profile.ServiceUser.Trim(), arguments, ct, null);

    private Task<WslCallResult> RootAsync(
        HardIsolationProfile profile, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(profile, "root", arguments, ct, null);

    private Task<WslCallResult> RunAsync(
        HardIsolationProfile profile,
        string user,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? stdin) =>
        _gateway.RunAsync(
            profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = user,
                HardTimeout = Deadline,
                StdinPayload = stdin,
                MaxCapturedCharactersPerStream = 512 * 1024,
            },
            ct);

    /// <summary>A live namespace holder, as measured rather than as remembered.</summary>
    private sealed record HolderState(string Unit, int Pid, string Reference, string Identity);
}
