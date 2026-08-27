using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardEvidence;

/// <summary>What one hard isolated blob is, once it is stored.</summary>
public sealed record HardStoredBlob(string Sha256, long SizeBytes, bool Deduplicated);

/// <summary>
/// One hard isolated organization's content addressed evidence, inside its own environment.
///
/// Physically its own store. Two organizations that store identical bytes store them twice, and
/// neither can observe that the other did - which is the point: a shared content addressed store
/// answers "do these bytes already exist" to anyone who can ask, and that answer is about
/// somebody else's content.
///
/// Read and written only by the organization's service identity. The agent has no access at all,
/// not even to its own execution's evidence: evidence is what a human decision is made from, and
/// a party that can read or alter the record of its own run is a party whose record means
/// nothing. That is enforced by ownership rather than by unpredictable names - a hash is not a
/// secret.
///
/// The Windows Hub never touches the environment's filesystem to do any of this. There is no
/// <c>\\wsl$</c> path and no mounted drive in the authority path; bytes travel base64 on the
/// control channel, and the hash is verified on the side that wrote the file before the file is
/// given its name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardEvidenceStore
{
    /// <summary>
    /// How many raw bytes cross the channel in one call.
    ///
    /// A transport window, and deliberately not a storage ceiling. There is no maximum artifact
    /// size here, because the evidence contract does not have one: an artifact the Windows store
    /// accepts has to be an artifact this store accepts, or the isolation backend would be
    /// deciding what counts as evidence.
    ///
    /// The window exists because the control channel is a pipe with a capture bound and base64
    /// makes a payload a third larger again. Bounding the window rather than the artifact keeps
    /// memory flat while a large artifact still stores whole.
    /// </summary>
    public const long TransferChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What one window becomes on the wire.
    ///
    /// Base64 encodes three bytes as four characters, so this is always larger than the window
    /// itself. Kept as its own number because the two are measured on different sides of the
    /// encoding: a raw artifact must never be judged against an encoded bound, or a legitimate
    /// artifact would be refused for the size of its representation.
    /// </summary>
    public const int EncodedChunkCharacters = (int)((TransferChunkBytes + 2) / 3 * 4);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private readonly IWslExecutionGateway _gateway;
    private readonly HardIsolationProfile _profile;

    internal HardEvidenceStore(IWslExecutionGateway gateway, HardIsolationProfile profile)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Layout = HardEvidenceLayout.For(profile.EvidenceRoot);
    }

    public HardEvidenceLayout Layout { get; }

    /// <summary>Builds the store for one organization's environment.</summary>
    public static HardEvidenceStore Create(HardIsolationProfile profile, string hostWorkingDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!WslExecutionGateway.TryCreate(hostWorkingDirectory, out var gateway, out var problem))
        {
            throw new InvalidOperationException($"hard evidence needs a reachable environment: {problem}");
        }

        return new HardEvidenceStore(gateway!, profile);
    }

    /// <summary>A store over a scripted environment, for this assembly's tests.</summary>
    internal static HardEvidenceStore Scripted(IWslExecutionGateway gateway, HardIsolationProfile profile) =>
        new(gateway, profile);

    /// <summary>
    /// Creates the store and closes it to every principal but its owner.
    ///
    /// Ownership is the boundary. The mode is <c>0700</c> and the owner is the organization's
    /// service identity, so the agent cannot list the directory, cannot read a blob whose hash it
    /// happens to know, and cannot write one - and neither can another organization's service.
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        // Created by the one identity that can then give it away. The service cannot make this
        // directory itself - its parent is not the service's - and handing it over afterwards is
        // the provisioning step, not something a stored blob ever goes through.
        var created = await RootAsync(HardEvidenceCommands.Create(Layout.PendingRoot), ct);

        if (!created.Ok)
        {
            return false;
        }

        var owned = await RootAsync(
            HardEvidenceCommands.Own(_profile.ServiceUser.Trim(), Layout.EvidenceRoot), ct);

        if (!owned.Ok)
        {
            return false;
        }

        var closed = await RootAsync(HardEvidenceCommands.Close(Layout.EvidenceRoot), ct);

        return closed.Ok;
    }

    /// <summary>
    /// Stores bytes, and refuses to store them under a name that is not theirs.
    ///
    /// The expected hash is the Hub's measurement of what it sent. The environment measures what
    /// it received. Publication happens only if the two agree, so a blob's name always describes
    /// the bytes inside it - a claim that would otherwise rest on a channel behaving.
    ///
    /// Nothing here caps the artifact. Bytes cross in windows and are appended, so an artifact of
    /// any size the evidence contract accepts stores whole while the memory this costs stays the
    /// size of one window.
    /// </summary>
    public async Task<HardStoredBlob?> WriteAsync(byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var blob = Layout.Blob(expected);

        if (await ExistsAsync(expected, ct))
        {
            // Identical content is already stored, within this organization only. Content
            // addressing makes converging safe: the name is the hash of the bytes.
            return new HardStoredBlob(expected, bytes.LongLength, true);
        }

        if (!(await ServiceAsync(HardEvidenceCommands.EnsureShard(Layout.Shard(expected)), ct)).Ok)
        {
            return null;
        }

        var attempt = Guid.CreateVersion7();
        var pending = Layout.Pending(expected, attempt);
        var part = Layout.Part(expected, attempt);

        try
        {
            // At least one window even for no bytes at all: an empty artifact is still an
            // artifact, and the file it publishes has to exist before it can be hashed.
            var windows = Math.Max(1, (bytes.LongLength + TransferChunkBytes - 1) / TransferChunkBytes);

            for (var window = 0L; window < windows; window++)
            {
                var offset = window * TransferChunkBytes;
                var count = (int)Math.Min(TransferChunkBytes, bytes.LongLength - offset);

                var received = await ServiceAsync(
                    HardEvidenceCommands.ReceivePending(part),
                    ct,
                    Convert.ToBase64String(bytes, (int)offset, count));

                if (!received.Ok)
                {
                    return null;
                }

                if (!(await ServiceAsync(HardEvidenceCommands.AppendPending(part, pending), ct)).Ok)
                {
                    return null;
                }
            }

            var measured = HardWorkspace.HardWorkspaceControl.FirstLine(
                await ServiceAsync(HardEvidenceCommands.HashPending(pending), ct));

            // sha256sum prints the digest, two spaces, then the name it hashed.
            var actual = measured?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                // The bytes that arrived are not the bytes that were sent. Publishing them under
                // the expected name would make the store's central claim false.
                return null;
            }

            var published = await ServiceAsync(HardEvidenceCommands.Publish(pending, blob), ct);

            if (!published.Ok)
            {
                // A concurrent writer of identical content got there first; both agree on the
                // bytes, so the blob that exists is the right one.
                return await ExistsAsync(expected, ct)
                    ? new HardStoredBlob(expected, bytes.LongLength, true)
                    : null;
            }

            return new HardStoredBlob(expected, bytes.LongLength, false);
        }
        finally
        {
            // An interrupted write leaves an unpublished file at worst, and never a blob: only
            // the rename creates that name.
            await ServiceAsync(HardEvidenceCommands.Discard(pending), CancellationToken.None);
            await ServiceAsync(HardEvidenceCommands.Discard(part), CancellationToken.None);
        }
    }

    /// <summary>Stores text as UTF-8. Callers redact before they reach here.</summary>
    public Task<HardStoredBlob?> WriteTextAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);

        return WriteAsync(Utf8NoBom.GetBytes(text), ct);
    }

    /// <summary>
    /// Reads a blob back by content identity, exactly.
    ///
    /// The hash is validated before a path exists, so there is nothing a caller can put here that
    /// resolves anywhere but this organization's own store. What comes back is checked against
    /// the name it was stored under, because a store that returns whatever is at a path is not a
    /// content addressed store.
    ///
    /// Read in windows for the same reason it is written in them: one captured stream never holds
    /// more than one window, whatever the artifact's size.
    /// </summary>
    public async Task<byte[]?> ReadAsync(string sha256, CancellationToken ct)
    {
        if (!HardEvidenceLayout.IsHash(sha256))
        {
            return null;
        }

        var size = await SizeAsync(sha256, ct);

        if (size is not { } total)
        {
            return null;
        }

        var attempt = Guid.CreateVersion7();
        var slice = Layout.Slice(sha256, attempt);
        var bytes = new byte[total];
        var filled = 0L;

        try
        {
            var windows = Math.Max(1, (total + TransferChunkBytes - 1) / TransferChunkBytes);

            for (var window = 0L; window < windows; window++)
            {
                if (!(await ServiceAsync(
                        HardEvidenceCommands.Slice(Layout.Blob(sha256), slice, TransferChunkBytes, window),
                        ct)).Ok)
                {
                    return null;
                }

                var read = await ServiceAsync(HardEvidenceCommands.ReadBlob(slice), ct);

                if (!read.Ok)
                {
                    return null;
                }

                byte[] windowBytes;

                try
                {
                    windowBytes = Convert.FromBase64String(string.Concat(read.Lines));
                }
                catch (FormatException)
                {
                    return null;
                }

                if (filled + windowBytes.LongLength > total)
                {
                    // More came back than the file says it holds, which means the two answers
                    // are about different things.
                    return null;
                }

                windowBytes.CopyTo(bytes, filled);
                filled += windowBytes.LongLength;
            }
        }
        finally
        {
            await ServiceAsync(HardEvidenceCommands.Discard(slice), CancellationToken.None);
        }

        if (filled != total)
        {
            return null;
        }

        return string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256.ToLowerInvariant(), StringComparison.Ordinal)
            ? bytes
            : null;
    }

    public async Task<string?> ReadTextAsync(string sha256, CancellationToken ct)
    {
        var bytes = await ReadAsync(sha256, ct);
        return bytes is null ? null : Utf8NoBom.GetString(bytes);
    }

    /// <summary>Whether this organization already holds these bytes. Says nothing about any other.</summary>
    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct) =>
        await SizeAsync(sha256, ct) is not null;

    /// <summary>How many bytes the stored blob holds, or nothing if it is not stored.</summary>
    public async Task<long?> SizeAsync(string sha256, CancellationToken ct)
    {
        if (!HardEvidenceLayout.IsHash(sha256))
        {
            return null;
        }

        var measured = await ServiceAsync(HardEvidenceCommands.Size(Layout.Blob(sha256)), ct);

        if (!measured.Ok)
        {
            return null;
        }

        return long.TryParse(
            HardWorkspace.HardWorkspaceControl.FirstLine(measured),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var size) && size >= 0
            ? size
            : null;
    }

    private Task<WslCallResult> ServiceAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? stdin = null) =>
        RunAsync(_profile.ServiceUser.Trim(), arguments, ct, stdin);

    private Task<WslCallResult> RootAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync("root", arguments, ct, null);

    private Task<WslCallResult> RunAsync(
        string user,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? stdin) =>
        _gateway.RunAsync(
            _profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = user,
                HardTimeout = Deadline,
                StdinPayload = stdin,

                // One window's encoded form, plus room for a diagnostic. Bounded by the window
                // rather than by the artifact, which is what lets the artifact be unbounded.
                MaxCapturedCharactersPerStream = EncodedChunkCharacters + (64 * 1024),
            },
            ct);
}
