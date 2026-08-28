using System.Runtime.Versioning;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Reads a hard organization's verification reports from inside the environment.
///
/// Wiring the isolated command executor without this would have been the more dangerous half of a
/// change: the test suite would run inside the boundary and its report would then be looked for on
/// the developer's filesystem, where either nothing is found - so every parsed step fails for a
/// reason that has nothing to do with the code - or something is, which is worse.
///
/// It is the already approved Structured Output Readback direction and nothing new. A file the
/// execution produced comes back through the trusted service identity, bounded by the same eight
/// mebibyte ceiling a structured agent result is bounded by, after being shown to physically resolve
/// inside the workspace it claims to be in. There is no host mount, no <c>\\wsl$</c>, no UNC path
/// and no new authority granted to the agent so that the Hub can read something.
///
/// What it does not do is interpret. The bytes go to the same TRX and JUnit parsers a logical run
/// uses, so isolation changes where a report is read and nothing about what it means.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardVerificationFileReader : IVerificationFileReader
{
    private readonly TrustedGuestFileTransfer _guest;

    internal HardVerificationFileReader(TrustedGuestFileTransfer guest) =>
        _guest = guest ?? throw new ArgumentNullException(nameof(guest));

    /// <summary>
    /// Decides where a declared path really is, in the environment's own terms.
    ///
    /// Two checks, and both are needed. The textual one refuses the shapes that could never be
    /// contained - a walk upwards, a Windows path, an absolute path somewhere else entirely - and
    /// refuses them without asking the machine anything. The physical one then resolves what is left
    /// and asks whether it still lands inside the root, which is the only way to catch a symlink the
    /// workload created pointing at a peer's workspace, the evidence store or an auth home.
    ///
    /// A relative path is composed against the root here rather than by the caller, so the root is
    /// always the Hub's and never something a verification profile named.
    /// </summary>
    public async Task<VerificationPathDecision> ResolveUnderRootAsync(
        string trustRoot,
        string relativePath,
        bool mustExist,
        CancellationToken cancellationToken)
    {
        if (!LinuxPath.TryNormalize(trustRoot, out var rootSegments, out var rootProblem))
        {
            return VerificationPathDecision.Rejected($"trust root '{trustRoot}' is not usable: {rootProblem}");
        }

        var root = LinuxPath.Join(rootSegments);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return VerificationPathDecision.Rejected("no path was declared");
        }

        var declared = relativePath.Trim();

        // A profile may declare its report path either way, so both are accepted and both end up
        // judged as one absolute guest path. Composition happens here so that a relative path cannot
        // be composed against anything but the root the run was admitted under.
        var candidate = declared.StartsWith('/') ? declared : root + "/" + declared;

        if (!LinuxPath.TryNormalize(candidate, out var candidateSegments, out var candidateProblem))
        {
            return VerificationPathDecision.Rejected($"'{declared}' is not usable: {candidateProblem}");
        }

        var stated = LinuxPath.Join(candidateSegments);

        if (!LinuxPath.TryContain(root, stated, out var containment))
        {
            return VerificationPathDecision.Rejected(containment ?? $"'{stated}' is not inside '{root}'");
        }

        var facts = await _guest.DescribeAsync(stated, cancellationToken);

        if (facts is null)
        {
            return VerificationPathDecision.Rejected(
                $"'{stated}' could not be resolved inside the environment");
        }

        if (!LinuxPath.TryContain(root, facts.PhysicalPath, out var physicalProblem))
        {
            // The interesting case, and the reason this is asked at all: every character of the
            // declared path can sit under the root while the thing it names does not.
            return VerificationPathDecision.Rejected(
                $"'{stated}' physically resolves to '{facts.PhysicalPath}', which is outside "
                + $"'{root}': {physicalProblem}");
        }

        if (mustExist && facts.Type is null)
        {
            return VerificationPathDecision.Rejected($"'{stated}' does not exist inside the environment");
        }

        if (mustExist && facts.Type is { } type && !facts.IsRegularFile && type != "directory")
        {
            return VerificationPathDecision.Rejected(
                $"'{stated}' is a {type} inside the environment, which is not something to read");
        }

        return VerificationPathDecision.Allowed(facts.PhysicalPath);
    }

    /// <summary>
    /// Brings a report back whole, or not at all.
    ///
    /// The ceiling is the frozen readback ceiling rather than the caller's, when the caller's is
    /// larger. Truncation is refused outright: half a JUnit document parses into a smaller number of
    /// tests rather than into an error, and a verification result that quietly counted fewer tests
    /// than ran is the one failure mode this whole path exists to prevent.
    /// </summary>
    public async Task<VerificationReadResult> ReadTextAsync(
        string absolutePath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (!LinuxPath.TryNormalize(absolutePath, out var segments, out var problem))
        {
            return VerificationReadResult.Failed(VerificationReadFailure.Unreadable, 0, problem ?? "unusable path");
        }

        var path = LinuxPath.Join(segments);

        // Read against its own directory as the root: containment has already been decided by
        // ResolveUnderRootAsync against the real trust root, and this call is handed the canonical
        // result of that decision. What the second check still buys is the physical resolution the
        // trusted read performs before touching anything.
        var directory = path[..path.LastIndexOf('/')];

        var read = await _guest.ReadAsync(
            directory.Length == 0 ? "/" : directory, path, cancellationToken);

        if (!read.Ok)
        {
            var tooLarge = read.Detail.Contains("readback ceiling", StringComparison.Ordinal);

            return VerificationReadResult.Failed(
                tooLarge ? VerificationReadFailure.TooLarge : VerificationReadFailure.Unreadable,
                0,
                read.Detail);
        }

        if (read.Content is null)
        {
            return VerificationReadResult.Failed(VerificationReadFailure.NotFound, 0, read.Detail);
        }

        // The caller's ceiling is honoured too when it is the tighter of the two. Neither is
        // negotiated down to fit a document.
        if (read.Content.Length > maxBytes)
        {
            return VerificationReadResult.Failed(
                VerificationReadFailure.TooLarge,
                read.Content.Length,
                $"the report is {read.Content.Length} bytes, above the {maxBytes} byte ceiling");
        }

        return VerificationReadResult.Ok(read.Content, read.Content.Length);
    }
}
