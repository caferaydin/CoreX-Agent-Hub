using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// How an organization's place on the trusted plane is named, and why it is derived rather than
/// configured.
///
/// Every name here comes from the organization's own identity through one hash. Nothing is
/// allocated from a counter, remembered in a file or persisted in the database: the plane is
/// machine state that the platform destroys on its own schedule, and a name that had to be
/// remembered would be a name that could disagree with what is actually loaded. Derivation means
/// the Hub can always recompute what an organization's attachment must look like and compare that
/// against what is there.
///
/// Derivation is not the same as safety, so collisions are checked rather than assumed away. Two
/// organizations whose names hash together would otherwise share an interface and a subnet, which
/// on a shared plane is not a mix-up - it is one organization's traffic being classified as
/// another's. That is refused.
/// </summary>
public static class HardNetworkIdentity
{
    /// <summary>
    /// The pool every attachment subnet is taken from.
    ///
    /// Chosen inside RFC1918 because the policy already denies every private destination, so an
    /// attachment address is non-public by construction and no separate rule is needed to keep one
    /// organization from addressing another's subnet. The pool is checked against the machine's
    /// existing routes before anything is created, because a private range this build finds
    /// convenient may be a range the operator's network already uses.
    /// </summary>
    public const string Pool = "10.79.0.0/16";

    /// <summary>Linux limits an interface name to fifteen characters including the terminator.</summary>
    public const int MaximumInterfaceNameLength = 15;

    private const string TrustedPrefix = "cxr";
    private const string NamespacePrefix = "cxo";
    private const string HolderPrefix = "corex-netns-";

    /// <summary>
    /// The stable short identity every other name is built from.
    ///
    /// Hex rather than the organization's own text: a display name can contain anything, and an
    /// interface name cannot. Eight characters keeps every derived name inside the kernel's limit
    /// while leaving the accidental collision rate small enough to be worth checking for rather
    /// than designing around.
    /// </summary>
    public static string AttachmentId(string organizationSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(organizationSlug.Trim().ToLowerInvariant()));

        return Convert.ToHexStringLower(digest)[..8];
    }

    /// <summary>The interface on the trusted side. This is what the policy classifies by.</summary>
    public static string TrustedInterface(string attachmentId) => TrustedPrefix + Hex(attachmentId);

    /// <summary>The interface inside the organization's own namespace.</summary>
    public static string NamespaceInterface(string attachmentId) => NamespacePrefix + Hex(attachmentId);

    /// <summary>The unit that holds the namespace open. Hub owned, and never named by a caller.</summary>
    public static string HolderUnit(string attachmentId) => HolderPrefix + Hex(attachmentId) + ".service";

    /// <summary>
    /// The attachment's own bounded subnet.
    ///
    /// A /30 - two usable addresses, the trusted side and the organization side - because an
    /// attachment is a point to point link and anything larger would be addressable space nobody
    /// asked for. The third and fourth octets come from the same derivation as the name, so an
    /// attachment's address and its interface can never describe different organizations.
    /// </summary>
    public static string Subnet(string attachmentId)
    {
        var index = Index(attachmentId);

        return string.Create(
            CultureInfo.InvariantCulture, $"10.79.{index / 64}.{(index % 64) * 4}/30");
    }

    /// <summary>The trusted side's address: the first usable one in the attachment's subnet.</summary>
    public static string GatewayAddress(string attachmentId) => Address(attachmentId, 1);

    /// <summary>The organization side's address: the second.</summary>
    public static string NamespaceAddress(string attachmentId) => Address(attachmentId, 2);

    private static string Address(string attachmentId, int offset)
    {
        var index = Index(attachmentId);

        return string.Create(
            CultureInfo.InvariantCulture, $"10.79.{index / 64}.{((index % 64) * 4) + offset}");
    }

    /// <summary>
    /// Where in the pool an attachment lands.
    ///
    /// Sixteen thousand three hundred and eighty four /30s, which is the whole of a /16 divided by
    /// four. Far more than a machine will ever hold at once, which is what keeps the collision
    /// check below a check rather than a queue.
    /// </summary>
    private static int Index(string attachmentId) =>
        (int)(uint.Parse(Hex(attachmentId), NumberStyles.HexNumber, CultureInfo.InvariantCulture) % 16384);

    /// <summary>Validates the derived identity before it becomes a name the kernel will hold.</summary>
    private static string Hex(string attachmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);

        if (attachmentId.Length != 8 || !attachmentId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "An attachment identity is eight hexadecimal characters.", nameof(attachmentId));
        }

        return attachmentId.ToLowerInvariant();
    }

    /// <summary>
    /// The whole attachment for one organization, derived.
    ///
    /// The namespace reference and identity are not known until the holder exists, so they are
    /// filled in by whoever prepared or measured it. Everything a name can decide is decided here.
    /// </summary>
    public static HardNetworkAttachment Describe(
        string organizationSlug, string namespaceReference = "", string namespaceIdentity = "")
    {
        var id = AttachmentId(organizationSlug);

        return new HardNetworkAttachment
        {
            AttachmentId = id,
            TrustedInterface = TrustedInterface(id),
            NamespaceInterface = NamespaceInterface(id),
            Subnet = Subnet(id),
            GatewayAddress = GatewayAddress(id),
            NamespaceAddress = NamespaceAddress(id),
            NamespaceReference = namespaceReference,
            NamespaceIdentity = namespaceIdentity,
        };
    }

    /// <summary>
    /// The attachment an identity describes, without knowing which organization produced it.
    ///
    /// The interface name carries the identity, so a Hub process that finds an attachment on the
    /// plane can say exactly what it should look like without remembering who created it. That is
    /// what makes reconciling a plane this process did not build possible at all, and it is the
    /// reason the identity is derived rather than allocated from a counter somebody has to store.
    /// </summary>
    public static HardNetworkAttachment FromAttachmentId(
        string attachmentId, string namespaceReference = "", string namespaceIdentity = "")
    {
        var id = Hex(attachmentId);

        return new HardNetworkAttachment
        {
            AttachmentId = id,
            TrustedInterface = TrustedInterface(id),
            NamespaceInterface = NamespaceInterface(id),
            Subnet = Subnet(id),
            GatewayAddress = GatewayAddress(id),
            NamespaceAddress = NamespaceAddress(id),
            NamespaceReference = namespaceReference,
            NamespaceIdentity = namespaceIdentity,
        };
    }

    /// <summary>The identity inside a trusted interface name, or nothing if it is not one.</summary>
    public static string? IdentityOf(string? trustedInterface) =>
        trustedInterface is { Length: 11 }
        && trustedInterface.StartsWith(TrustedPrefix, StringComparison.Ordinal)
        && trustedInterface[3..].All(Uri.IsHexDigit)
            ? trustedInterface[3..]
            : null;

    /// <summary>
    /// Whether two organizations would end up sharing a place on the plane.
    ///
    /// On a shared plane this is not an inconvenience to be worked around by picking the next free
    /// slot. Two organizations on one interface means one organization's traffic is classified as
    /// the other's, and a Hub that quietly reallocated would be a Hub whose boundary depended on
    /// the order things were started in.
    /// </summary>
    public static bool Collides(string firstSlug, string secondSlug)
    {
        if (string.Equals(firstSlug?.Trim(), secondSlug?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var first = Describe(firstSlug!);
        var second = Describe(secondSlug!);

        return string.Equals(first.TrustedInterface, second.TrustedInterface, StringComparison.Ordinal)
               || string.Equals(first.Subnet, second.Subnet, StringComparison.Ordinal);
    }

    /// <summary>Whether every derived name fits what the kernel will accept.</summary>
    public static bool IsNameable(string organizationSlug)
    {
        var attachment = Describe(organizationSlug);

        return attachment.TrustedInterface.Length < MaximumInterfaceNameLength
               && attachment.NamespaceInterface.Length < MaximumInterfaceNameLength
               && attachment.TrustedInterface.All(c => char.IsAsciiLetterOrDigit(c))
               && attachment.NamespaceInterface.All(c => char.IsAsciiLetterOrDigit(c));
    }
}
