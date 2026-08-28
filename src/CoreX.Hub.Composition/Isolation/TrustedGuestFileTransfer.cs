using System.Globalization;
using System.Text;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>The commands this transfer runs inside the environment. Closed, and absolute.</summary>
internal static class TrustedGuestFileCommands
{
    private const string OpenSsl = "/usr/bin/openssl";
    private const string Base64 = "/usr/bin/base64";
    private const string Mkdir = "/usr/bin/mkdir";
    private const string Realpath = "/usr/bin/realpath";
    private const string Stat = "/usr/bin/stat";
    private const string Chmod = "/usr/bin/chmod";
    private const string Chown = "/usr/bin/chown";

    /// <summary>Decodes base64 from standard input straight into a file. No shell, no redirection.</summary>
    public static IReadOnlyList<string> Write(string destination) =>
        [OpenSsl, "base64", "-d", "-A", "-out", destination];

    /// <summary>Reads a file back as one base64 line, so exact bytes survive a text channel.</summary>
    public static IReadOnlyList<string> Read(string path) => [Base64, "-w", "0", path];

    public static IReadOnlyList<string> EnsureDirectory(string path) => [Mkdir, "-p", path];

    /// <summary>Resolves a path physically, so a symlink cannot pretend to be inside a root.</summary>
    public static IReadOnlyList<string> Resolve(string path) => [Realpath, "-m", path];

    public static IReadOnlyList<string> Size(string path) => [Stat, "-c", "%s", path];

    /// <summary>What kind of thing a path is, and what it permits. One stat, two facts.</summary>
    public static IReadOnlyList<string> Describe(string path) => [Stat, "-c", "%F|%a", path];

    public static IReadOnlyList<string> Own(string owner, string path) => [Chown, owner, path];

    public static IReadOnlyList<string> Mode(string mode, string path) => [Chmod, mode, path];
}

/// <summary>What a transfer did, or why it did not.</summary>
public sealed record TrustedTransferResult(bool Ok, string Detail)
{
    public static TrustedTransferResult Refused(string detail) => new(false, detail);

    public static TrustedTransferResult Succeeded(string detail) => new(true, detail);
}

/// <summary>
/// What the environment says a path physically is.
///
/// Three separate facts, none of them inferred from another: where the path really leads, what kind
/// of object is there, and what it permits. A null type means nothing was there to describe.
/// </summary>
internal sealed record TrustedPathFacts(string PhysicalPath, string? Type, string? Mode)
{
    /// <summary>stat's own word for an ordinary file, as opposed to a directory or a device.</summary>
    public bool IsRegularFile => string.Equals(Type, "regular file", StringComparison.Ordinal);

    /// <summary>
    /// Whether anything at all may execute this.
    ///
    /// A coarse question deliberately. Which identity is permitted to run it is decided by the
    /// kernel at exec time, as the unprivileged agent user, and no model of that belongs here - this
    /// only refuses what could never be executed by anyone, which is a configuration mistake rather
    /// than a permission one.
    /// </summary>
    public bool IsExecutable =>
        Mode is { Length: > 0 } mode && mode.Any(c => c is '1' or '3' or '5' or '7');
}

/// <summary>What a readback produced, or why it produced nothing.</summary>
public sealed record TrustedReadResult(bool Ok, string? Content, string Detail)
{
    public static TrustedReadResult Refused(string detail) => new(false, null, detail);

    public static TrustedReadResult Absent(string detail) => new(true, null, detail);

    public static TrustedReadResult Read(string content) => new(true, content, "read");
}

/// <summary>
/// Moving trusted Hub bytes in and bounded execution output back out.
///
/// Deliberately not the pinned source transfer, and the distinction is a security one rather than
/// a naming preference. <c>PinnedTransfer</c> means exactly one thing - committed, reachable git
/// objects from the pinned base commit - and that meaning is what guarantees a host's dirty,
/// untracked and ignored files cannot enter the environment. Widening it into "how the Hub puts
/// files in WSL" would quietly dissolve that guarantee, because the next person adding a file to
/// the transfer would inherit a name that says the content was committed source.
///
/// So this carries the other two directions, and only those. Inward: bytes the Hub itself
/// generated, such as a context pack, which are trusted because the Hub built them and are never
/// repository content. Outward: a bounded file an execution produced, read by the trusted service
/// identity so the Hub can interpret it.
///
/// Everything about where is Hub authority. The caller supplies a root it owns and a leaf name;
/// the destination is composed here, and then checked against the machine rather than against the
/// string - a path is resolved physically before it is written to or read from, so a symlink the
/// agent created cannot redirect a trusted read into a peer's workspace, the evidence root or an
/// auth home. There is no shell, no host mount, no <c>\\wsl$</c> and no path a provider, a task or
/// a repository can name.
/// </summary>
internal sealed class TrustedGuestFileTransfer
{
    /// <summary>
    /// The most a single readback may bring back.
    ///
    /// A ceiling rather than none, because a file an execution wrote is a file an execution
    /// controls the size of, and reading it into the Hub's memory over a text channel costs about
    /// four bytes of transport for every three of content. Eight mebibytes is the same ceiling the
    /// process capture already uses, so a structured result and a captured stream are bounded by
    /// one number rather than by two that could drift apart.
    /// </summary>
    public const long MaximumReadBytes = 8L * 1024 * 1024;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    private readonly IWslExecutionGateway _gateway;
    private readonly HardIsolationProfile _profile;

    public TrustedGuestFileTransfer(IWslExecutionGateway gateway, HardIsolationProfile profile)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>
    /// Writes Hub generated bytes to a Hub chosen path, readable by the workload.
    ///
    /// The destination is composed from a root the caller owns and a leaf this method validates,
    /// so there is no caller supplied absolute path and nothing for a traversal to escape through.
    /// </summary>
    public async Task<TrustedTransferResult> WriteAsync(
        string root,
        string leaf,
        ReadOnlyMemory<byte> content,
        CancellationToken ct)
    {
        if (!TryCompose(root, leaf, out var destination, out var problem))
        {
            return TrustedTransferResult.Refused(problem!);
        }

        if (content.Length > MaximumReadBytes)
        {
            return TrustedTransferResult.Refused(
                $"the payload is {content.Length} bytes, over the {MaximumReadBytes} byte transfer ceiling.");
        }

        var absolute = destination!;
        var directory = absolute[..absolute.LastIndexOf('/')];

        var created = await ServiceAsync(TrustedGuestFileCommands.EnsureDirectory(directory), null, ct);

        if (!created.Ok)
        {
            return TrustedTransferResult.Refused($"'{directory}' could not be created.");
        }

        // Resolved before writing, not after. A directory that resolves outside the root is a
        // redirection, and the answer is to refuse rather than to write and then notice.
        if (!await IsInsideAsync(root, directory, ct))
        {
            return TrustedTransferResult.Refused(
                $"'{directory}' does not physically resolve inside '{root}'.");
        }

        var written = await ServiceAsync(
            TrustedGuestFileCommands.Write(absolute),
            Convert.ToBase64String(content.Span),
            ct);

        if (!written.Ok)
        {
            return TrustedTransferResult.Refused($"'{absolute}' could not be written.");
        }

        // The workload reads this; it never writes it. Ownership stays with the service identity
        // so an agent cannot rewrite its own instructions and then be measured against them.
        await RootAsync(TrustedGuestFileCommands.Own(_profile.ServiceUser.Trim(), absolute), ct);
        await RootAsync(TrustedGuestFileCommands.Mode("0644", absolute), ct);

        return TrustedTransferResult.Succeeded(absolute);
    }

    /// <summary>
    /// Reads a bounded file an execution produced, or reports that there is nothing to read.
    ///
    /// Absence is success with no content, because that is what it means to every caller: no
    /// structured result was produced. A file too large to accept is a refusal, which is different
    /// and is reported differently.
    /// </summary>
    public async Task<TrustedReadResult> ReadAsync(
        string root,
        string path,
        CancellationToken ct)
    {
        if (!LinuxPath.TryContain(root, path, out var containment))
        {
            return TrustedReadResult.Refused($"'{path}' is not inside '{root}': {containment}");
        }

        if (!await IsInsideAsync(root, path, ct))
        {
            return TrustedReadResult.Refused($"'{path}' does not physically resolve inside '{root}'.");
        }

        var size = await ServiceAsync(TrustedGuestFileCommands.Size(path), null, ct);

        if (!size.Ok)
        {
            // stat fails when the file is not there, which is the ordinary case for a run that
            // produced no structured result.
            return TrustedReadResult.Absent($"'{path}' does not exist.");
        }

        if (!long.TryParse(size.Lines.FirstOrDefault()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
        {
            return TrustedReadResult.Refused($"the size of '{path}' could not be read.");
        }

        if (bytes > MaximumReadBytes)
        {
            return TrustedReadResult.Refused(
                $"'{path}' is {bytes} bytes, over the {MaximumReadBytes} byte readback ceiling.");
        }

        if (bytes == 0)
        {
            return TrustedReadResult.Read(string.Empty);
        }

        var read = await ServiceAsync(TrustedGuestFileCommands.Read(path), null, ct);

        if (!read.Ok)
        {
            return TrustedReadResult.Refused($"'{path}' could not be read.");
        }

        try
        {
            return TrustedReadResult.Read(
                Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(read.Lines).Trim())));
        }
        catch (FormatException)
        {
            return TrustedReadResult.Refused($"the content of '{path}' did not survive the channel.");
        }
    }

    /// <summary>
    /// Composes a destination from a root the Hub owns and a single leaf name.
    ///
    /// A leaf rather than a path: one segment, no separators, so there is nothing to traverse with
    /// and no way to name a second directory. The absolute path is built here, which is what makes
    /// "the Hub decides where" a property of the code.
    /// </summary>
    private static bool TryCompose(string root, string leaf, out string? destination, out string? problem)
    {
        destination = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(leaf)
            || leaf.Contains('/', StringComparison.Ordinal)
            || leaf.Contains('\\', StringComparison.Ordinal)
            || leaf is "." or ".."
            || leaf.Any(char.IsControl))
        {
            problem = $"'{leaf}' is not a usable file name.";
            return false;
        }

        if (!LinuxPath.TryNormalize(root, out var segments, out var rootProblem))
        {
            problem = $"'{root}' is not a usable root: {rootProblem}";
            return false;
        }

        destination = LinuxPath.Join(segments) + "/" + leaf;
        return true;
    }

    /// <summary>
    /// Resolves a path physically and says what the filesystem found there.
    ///
    /// Physical first, then described. Asking what a path is before resolving it would describe
    /// whatever a symlink pointed at while reporting the name it was reached by, which is the exact
    /// confusion an executable check must not have.
    /// </summary>
    public async Task<TrustedPathFacts?> DescribeAsync(string path, CancellationToken ct)
    {
        var resolved = await ServiceAsync(TrustedGuestFileCommands.Resolve(path), null, ct);

        if (!resolved.Ok || resolved.Lines.FirstOrDefault() is not { Length: > 0 } physical)
        {
            return null;
        }

        var described = await ServiceAsync(TrustedGuestFileCommands.Describe(physical), null, ct);

        if (!described.Ok || described.Lines.FirstOrDefault() is not { Length: > 0 } line)
        {
            // stat refuses what is not there. Absence and unreadability are the same answer to the
            // only caller: this is not something to execute.
            return new TrustedPathFacts(physical.Trim(), null, null);
        }

        var parts = line.Trim().Split('|');

        return new TrustedPathFacts(
            physical.Trim(),
            parts.Length > 0 ? parts[0].Trim() : null,
            parts.Length > 1 ? parts[1].Trim() : null);
    }

    private async Task<bool> IsInsideAsync(string root, string candidate, CancellationToken ct)
    {
        var resolved = await ServiceAsync(TrustedGuestFileCommands.Resolve(candidate), null, ct);

        return resolved.Ok
               && resolved.Lines.FirstOrDefault() is { Length: > 0 } physical
               && LinuxPath.TryContain(root, physical, out _);
    }

    private Task<WslCallResult> ServiceAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken ct) =>
        RunAsync(_profile.ServiceUser.Trim(), arguments, stdin, ct);

    private Task<WslCallResult> RootAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync("root", arguments, null, ct);

    private Task<WslCallResult> RunAsync(
        string user, IReadOnlyList<string> arguments, string? stdin, CancellationToken ct) =>
        _gateway.RunAsync(
            _profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = user,
                HardTimeout = Deadline,
                StdinPayload = stdin,
                MaxCapturedCharactersPerStream = (int)(MaximumReadBytes / 3 * 4) + 1024,
            },
            ct);
}
