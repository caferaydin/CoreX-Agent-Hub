namespace CoreX.Hub.Composition.HardEvidence;

/// <summary>
/// Every command the hard evidence store may run inside the environment, and no others.
///
/// A closed surface, like the workspace's. Evidence is control-plane truth: it is what an
/// acceptance decision is made from, and a general "run this command" reachable from anywhere
/// near it would make the ownership model advisory.
///
/// None of these takes a path from a caller. The store builds every path from a hash it has
/// already validated, so there is no traversal to defend against at this level - the defence is
/// that a path cannot be asked for.
/// </summary>
public static class HardEvidenceCommands
{
    public const string OpenSsl = "/usr/bin/openssl";
    public const string Base64 = "/usr/bin/base64";
    public const string Sha256Sum = "/usr/bin/sha256sum";
    public const string Mkdir = "/usr/bin/mkdir";
    public const string Mv = "/usr/bin/mv";
    public const string Rm = "/usr/bin/rm";
    public const string Stat = "/usr/bin/stat";
    public const string Chown = "/usr/bin/chown";
    public const string Chmod = "/usr/bin/chmod";
    public const string Getent = "/usr/bin/getent";

    /// <summary>
    /// Writes the bytes arriving on standard input into an unpublished file.
    ///
    /// Base64 on the way in, because the control channel carries text and evidence does not have
    /// to be text-shaped to be evidence. <c>-A</c> is not optional: without it the same command
    /// silently writes nothing, which was measured rather than assumed.
    /// </summary>
    public static IReadOnlyList<string> ReceivePending(string pendingPath) =>
        [OpenSsl, "base64", "-d", "-A", "-out", pendingPath];

    /// <summary>
    /// Hashes what was actually written, on the side that wrote it.
    ///
    /// The caller's expected hash is a claim about bytes that have crossed a boundary. This is
    /// the measurement, and the publication below happens only when the two agree - so a blob's
    /// name always describes the bytes in it, whatever happened in transit.
    /// </summary>
    public static IReadOnlyList<string> HashPending(string pendingPath) => [Sha256Sum, pendingPath];

    public static IReadOnlyList<string> EnsureShard(string shardPath) => [Mkdir, "-p", shardPath];

    /// <summary>
    /// Publishes the blob under its content addressed name, and never over another.
    ///
    /// <c>-n</c> is the whole guarantee: a rename that would replace an existing blob does not
    /// happen. Two writers of identical bytes converge on one file; a writer of different bytes
    /// cannot reach that name at all, because the name is the hash of what it wrote.
    /// </summary>
    public static IReadOnlyList<string> Publish(string pendingPath, string blobPath) =>
        [Mv, "-n", pendingPath, blobPath];

    /// <summary>Reads a published blob back as base64, so exact bytes survive the channel.</summary>
    public static IReadOnlyList<string> ReadBlob(string blobPath) => [Base64, "-w", "0", blobPath];

    public static IReadOnlyList<string> Size(string path) => [Stat, "-c", "%s", path];

    public static IReadOnlyList<string> Discard(string pendingPath) => [Rm, "-f", pendingPath];

    /// <summary>
    /// Creates the store and closes it to everyone but its owner.
    ///
    /// <c>0700</c> rather than a group: the agent is not in a group that should be able to read
    /// evidence, and neither is another organization's service identity. There is exactly one
    /// principal with access and it is the one that owns the tree.
    /// </summary>
    public static IReadOnlyList<string> Create(string path) => [Mkdir, "-p", path];

    public static IReadOnlyList<string> Own(string owner, string path) => [Chown, "-R", owner, path];

    public static IReadOnlyList<string> Close(string path) => [Chmod, "-R", "0700", path];

    /// <summary>Asks the environment what an account's home really is.</summary>
    public static IReadOnlyList<string> PasswdEntry(string user) => [Getent, "passwd", user];

    /// <summary>Ownership and mode of a path, for the checks that need both.</summary>
    public static IReadOnlyList<string> Describe(string path) => [Stat, "-c", "%U|%A|%F", path];
}
