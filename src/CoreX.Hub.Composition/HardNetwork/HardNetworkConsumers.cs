using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// Who still needs an organization's network attachment, recorded where a restart can find it.
///
/// The two lifetimes underneath this do not match, and that mismatch was a real defect. A physical
/// attachment - the namespace holder, the veth, the organization's place in the trusted ruleset -
/// belongs to an organization and is deliberately shared by everything that organization runs. A
/// lease belongs to one execution. So the first execution to finish was calling a teardown that
/// unconditionally deleted the veth and stopped the holder, and a second execution of the same
/// organization lost its network in the middle of its own run.
///
/// It fails closed rather than open, because a workload whose namespace reference names a dead
/// holder is refused before it starts. That makes it a correctness defect rather than an isolation
/// breach - but a verification step that cannot launch because another execution finished is not
/// something to leave in place.
///
/// So ownership is counted, and counted on disk. An in-memory count would be the obvious fix and
/// the wrong one: the Hub runs as a CLI beside an API, and a process that died holding a count
/// would come back believing nobody was using an attachment that three live workloads were sitting
/// in. What survives a restart is a directory of markers, one per execution, which the next process
/// can read and reconcile against the executions the database already knows about.
///
/// This deliberately sits above the physical fabric and changes none of it. The fabric still means
/// "make this organization's attachment exist" and "tear this organization's attachment down"; what
/// changes is who is allowed to ask for the second one, and when.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkConsumers
{
    /// <summary>
    /// The machine wide lock for ownership transitions.
    ///
    /// Its own, and deliberately not the fabric's. Two locks with one order - ownership first,
    /// then the physical plane - is what keeps a first-consumer acquire and a last-consumer release
    /// from interleaving into two writers of the same attachment. Taking them in the other order
    /// anywhere would be a deadlock waiting for the two paths to meet.
    /// </summary>
    private const string OwnershipLockName = @"Global\CoreX.Hub.HardNetworkConsumers";

    private const string Directory = "hard-network-consumers";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private readonly IHardNetworkFabric _fabric;
    private readonly string _hostDirectory;

    public HardNetworkConsumers(IHardNetworkFabric fabric, string hostDirectory)
    {
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _hostDirectory = hostDirectory ?? throw new ArgumentNullException(nameof(hostDirectory));
    }

    /// <summary>
    /// Claims a share of an organization's attachment for one execution, preparing it if needed.
    ///
    /// The marker is written before the physical preparation on purpose. The two orders fail
    /// differently and only one of them fails safely: a marker with no attachment is a stale claim
    /// that reconciliation removes and that no workload can use, because admission measures the
    /// attachment rather than believing the claim. An attachment with no marker is a live namespace
    /// holder nobody owns, which the next release would decline to touch and which would sit there
    /// until somebody noticed.
    /// </summary>
    public async Task<HardNetworkAttachment?> AcquireAsync(
        HardIsolationProfile profile,
        string organizationSlug,
        Guid executionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);

        if (executionId == Guid.Empty || !HardNetworkIdentity.IsNameable(organizationSlug))
        {
            return null;
        }

        using var gate = MutationLock.Acquire(OwnershipLockName, LockTimeout);

        if (!gate.Held)
        {
            return null;
        }

        var marker = MarkerPath(organizationSlug, executionId);
        var created = !File.Exists(marker);

        if (created)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(marker)!);

            // Content is deliberately nothing anybody has to parse. The claim is the file's
            // existence and its name; a format would be a format to keep compatible.
            await File.WriteAllTextAsync(marker, string.Empty, ct);
        }

        var attachment = await _fabric.PrepareAsync(profile, organizationSlug, ct);

        if (attachment is null)
        {
            // Only unwind what this call created. An execution that was already a consumer keeps
            // its claim: this acquisition failing says nothing about the one that succeeded.
            if (created)
            {
                Delete(marker);
            }

            return null;
        }

        return attachment;
    }

    /// <summary>
    /// Gives up one execution's claim, and tears the attachment down only when it was the last.
    ///
    /// The check and the teardown happen under one lock, so a second execution acquiring at the
    /// same moment either gets its marker in first - and this sees it and leaves the attachment
    /// alone - or arrives afterwards and prepares it again. What cannot happen is both deciding
    /// they were alone.
    /// </summary>
    public async Task<string?> ReleaseAsync(
        HardIsolationProfile profile,
        string organizationSlug,
        Guid executionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!HardNetworkIdentity.IsNameable(organizationSlug))
        {
            return null;
        }

        using var gate = MutationLock.Acquire(OwnershipLockName, LockTimeout);

        if (!gate.Held)
        {
            return $"the network ownership lock could not be taken, so '{organizationSlug}' was left as it is.";
        }

        var marker = MarkerPath(organizationSlug, executionId);

        // Whether this call is giving anything up. A release for an execution that is not a
        // consumer has nothing to release, and must not reach the physical teardown: with no
        // consumers left it would look exactly like a last-consumer release and tear down an
        // attachment this caller never held.
        var held = File.Exists(marker);

        Delete(marker);

        if (!held)
        {
            return null;
        }

        if (Consumers(organizationSlug).Count > 0)
        {
            // Somebody else is still inside this organization's namespace. Its holder and veth are
            // theirs for as long as that is true.
            return null;
        }

        try
        {
            await _fabric.ReleaseAsync(profile, organizationSlug, ct);
            return null;
        }
        catch (Exception ex)
        {
            // The claim is already gone and is not put back to hide this. A Hub owned attachment
            // with no consumers is exactly what reconciliation is able to find and release later,
            // and recreating the marker would make it invisible to that.
            return $"the network attachment for '{organizationSlug}' could not be released: {ex.Message}";
        }
    }

    /// <summary>Which executions still claim this organization's attachment.</summary>
    public IReadOnlyList<Guid> Consumers(string organizationSlug)
    {
        var directory = OrganizationDirectory(organizationSlug);

        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        return
        [
            .. System.IO.Directory.EnumerateFiles(directory, "*.lease")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Select(n => Guid.TryParseExact(n, "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty),
        ];
    }

    /// <summary>
    /// Drops claims belonging to executions that are no longer entitled to one.
    ///
    /// Called after execution state reconciliation rather than before it, so an execution the Hub
    /// is about to mark Interrupted is judged on its settled state rather than mid-decision. The
    /// caller supplies the verdict; this does not read the database, because an ownership registry
    /// that decided execution lifecycle would be a second opinion about it.
    ///
    /// Ambiguity preserves. An id this cannot classify keeps its claim, which at worst leaves an
    /// attachment alive for an organization nothing is using - visible, releasable later, and a far
    /// better failure than tearing down a namespace somebody is working in.
    /// </summary>
    public IReadOnlyList<Guid> Forget(string organizationSlug, Func<Guid, bool> isEntitled)
    {
        ArgumentNullException.ThrowIfNull(isEntitled);

        using var gate = MutationLock.Acquire(OwnershipLockName, LockTimeout);

        if (!gate.Held)
        {
            return [];
        }

        var dropped = new List<Guid>();

        foreach (var consumer in Consumers(organizationSlug))
        {
            if (!isEntitled(consumer))
            {
                Delete(MarkerPath(organizationSlug, consumer));
                dropped.Add(consumer);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Every organization that currently has claims, for reconciliation to walk.
    ///
    /// A directory is not a claim. One is created before the first marker is written and is not
    /// removed when the last is deleted, so an organization that has finished everything it was
    /// running still has a directory - and counting those would mean reconciliation believed there
    /// was work to do for the rest of the installation's life, on every session that opened.
    ///
    /// So the answer is the organizations holding at least one marker. That is also the right answer
    /// after a crash between creating the directory and writing the marker, which leaves the same
    /// empty directory and no claim.
    /// </summary>
    public IReadOnlyList<string> ClaimedAttachments()
    {
        var root = Path.Combine(_hostDirectory, Directory);

        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. System.IO.Directory.EnumerateDirectories(root)
                .Where(d => System.IO.Directory.EnumerateFiles(d, "*.lease").Any())
                .Select(Path.GetFileName)
                .OfType<string>(),
        ];
    }

    /// <summary>
    /// Where one execution's claim lives.
    ///
    /// Both segments are Hub derived: the organization's attachment identity is a hash the network
    /// identity authority produces, and the execution is a guid rendered in hexadecimal. No display
    /// name, task key or repository string reaches the filesystem, so there is nothing here for a
    /// name to escape through.
    /// </summary>
    private string MarkerPath(string organizationSlug, Guid executionId) =>
        Path.Combine(
            OrganizationDirectory(organizationSlug),
            executionId.ToString("N", CultureInfo.InvariantCulture) + ".lease");

    private string OrganizationDirectory(string organizationSlug) =>
        Path.Combine(
            _hostDirectory, Directory, HardNetworkIdentity.AttachmentId(organizationSlug));

    private static void Delete(string marker)
    {
        try
        {
            File.Delete(marker);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
