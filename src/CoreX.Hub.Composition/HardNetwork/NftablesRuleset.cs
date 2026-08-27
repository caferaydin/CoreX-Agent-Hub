using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// The Hub's network policy for the whole machine, rendered as the one table it owns.
///
/// Global rather than per organization, because the plane is global. Every environment on this
/// platform shares one network namespace and therefore one ruleset - a table loaded "for" one
/// organization is visible to, and enforced on, all of them. So there is one table, it belongs to
/// the Hub rather than to any organization, and organizations are told apart inside it.
///
/// They are told apart by the interface a packet arrived on, never by its source address. An
/// address is configuration inside a namespace; an ingress interface is a fact about where the
/// packet crossed into the trusted plane, and a workload that sets its neighbour's address on its
/// own interface is still arriving on its own.
///
/// The chain policy is <c>accept</c>, and that is not a weaker posture - it is the only correct
/// one here. These chains sit in the namespace the operator's own environment uses, so a default
/// drop would take the developer's network down. Denial is scoped instead: every Hub interface
/// ends in a drop of its own, so nothing that arrives on one escapes for want of a rule. What that
/// costs is the guarantee a default drop would have given for free - an interface nobody named is
/// unfiltered - and it is why the fabric is only ready when the set of named interfaces and the
/// set of present ones are the same set.
/// </summary>
public sealed record NftablesRuleset
{
    /// <summary>The one table the Hub owns. Nothing outside it is read, written or judged.</summary>
    public const string TableFamily = "inet";

    public const string TableName = "corex_hard";

    /// <summary>The Hub's own source translation, kept apart from its filtering.</summary>
    public const string NatTableFamily = "ip";

    public const string NatTableName = "corex_hard_nat";

    private NftablesRuleset(string text, IReadOnlyList<HardNetworkAttachment> attachments, string uplink)
    {
        Text = text;
        Attachments = attachments;
        Uplink = uplink;
        Identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Exactly what would be handed to <c>nft -f -</c>.</summary>
    public string Text { get; }

    /// <summary>Every attachment this policy classifies. The fabric is ready only for these.</summary>
    public IReadOnlyList<HardNetworkAttachment> Attachments { get; }

    /// <summary>The interface the trusted plane reaches the outside world through.</summary>
    public string Uplink { get; }

    /// <summary>The SHA-256 of <see cref="Text"/>. The Hub's name for this policy.</summary>
    public string Identity { get; }

    /// <summary>The interfaces this policy names, which is what the fabric must match exactly.</summary>
    public IReadOnlyList<string> TrustedInterfaces { get; init; } = [];

    /// <summary>
    /// Renders the policy for the attachments that currently exist.
    ///
    /// Refuses rather than improvises. An unsupported topology, a resolver the environment does not
    /// report, or an attachment whose names do not fit are all machines the policy was not designed
    /// for, and rendering something anyway would produce a table that loads, looks enforced, and
    /// denies a different set than the Hub believes.
    /// </summary>
    public static bool TryRender(
        HardNetworkPolicy policy,
        HardNetworkTopology topology,
        IReadOnlyList<HardNetworkAttachment> attachments,
        out NftablesRuleset? ruleset,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(attachments);

        ruleset = null;
        problem = null;

        if (topology.Mode != HardNetworkMode.Nat)
        {
            problem = $"the network arrangement is {topology.Mode}, and only Nat is supported: {topology.Detail}";
            return false;
        }

        if (policy.AllowDns && !HardNetworkTopology.IsIpv4(topology.DnsEndpoint))
        {
            problem = "the environment reports no IPv4 resolver, so the name resolution allowance "
                      + "has no address to name.";
            return false;
        }

        if (topology.Uplink.Length == 0)
        {
            problem = "the trusted plane reports no uplink interface, so nothing can be translated out of it.";
            return false;
        }

        var duplicate = attachments
            .GroupBy(a => a.TrustedInterface, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            problem = $"two attachments share the trusted interface '{duplicate.Key}', which would "
                      + "classify one organization's traffic as another's.";
            return false;
        }

        var ordered = attachments.OrderBy(a => a.TrustedInterface, StringComparer.Ordinal).ToList();

        ruleset = new NftablesRuleset(Render(policy, topology, ordered), ordered, topology.Uplink)
        {
            TrustedInterfaces = ordered.Select(a => a.TrustedInterface).ToList(),
        };

        return true;
    }

    private static string Render(
        HardNetworkPolicy policy,
        HardNetworkTopology topology,
        IReadOnlyList<HardNetworkAttachment> attachments)
    {
        var text = new StringBuilder();

        // Replaced rather than flushed, and only the Hub's own table: an operator's rules are
        // theirs, and nft applies a file atomically so a partially updated policy is never live.
        text.Append(CultureInfo.InvariantCulture, $"table {TableFamily} {TableName} {{}}\n");
        text.Append(CultureInfo.InvariantCulture, $"delete table {TableFamily} {TableName}\n");
        text.Append(CultureInfo.InvariantCulture, $"table {TableFamily} {TableName} {{\n");

        RenderForward(text, policy, attachments);
        RenderInput(text, policy, topology, attachments);

        text.Append("}\n");

        return text.ToString();
    }

    private static void RenderForward(
        StringBuilder text, HardNetworkPolicy policy, IReadOnlyList<HardNetworkAttachment> attachments)
    {
        text.Append("  chain corex_forward {\n");

        // accept, not drop - see the type comment. Scoped denial follows, per interface.
        text.Append("    type filter hook forward priority -300; policy accept;\n");

        foreach (var attachment in attachments)
        {
            var iif = attachment.TrustedInterface;

            // Peers first and by interface, so the denial says what it means rather than relying
            // on the address ranges below to happen to cover it.
            foreach (var peer in attachments.Where(a => a.TrustedInterface != iif))
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"    iifname \"{iif}\" oifname \"{peer.TrustedInterface}\" drop\n");
            }

            foreach (var denied in policy.DeniedDestinations)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"    iifname \"{iif}\" ip daddr {denied.Cidr} drop\n");
            }

            foreach (var denied in policy.DeniedDestinationsV6)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"    iifname \"{iif}\" ip6 daddr {denied.Cidr} drop\n");
            }

            if (policy.AllowPublicHttps)
            {
                // Reached only by what survived every denial above, which is what makes this
                // "public" rather than "any".
                text.Append(CultureInfo.InvariantCulture,
                    $"    iifname \"{iif}\" tcp dport {policy.PublicHttpsPort} accept\n");
            }

            text.Append(CultureInfo.InvariantCulture,
                $"    iifname \"{iif}\" ct state established,related accept\n");

            // The scoped equivalent of a default drop. Without it an attachment would fall through
            // to the chain policy, which cannot be drop.
            text.Append(CultureInfo.InvariantCulture, $"    iifname \"{iif}\" drop\n");
        }

        text.Append("  }\n");
    }

    private static void RenderInput(
        StringBuilder text,
        HardNetworkPolicy policy,
        HardNetworkTopology topology,
        IReadOnlyList<HardNetworkAttachment> attachments)
    {
        text.Append("  chain corex_input {\n");
        text.Append("    type filter hook input priority -300; policy accept;\n");

        foreach (var attachment in attachments)
        {
            var iif = attachment.TrustedInterface;

            if (policy.AllowDns)
            {
                // The one service on the trusted plane an attachment may terminate at, at the one
                // address the environment reported and on the one port that makes it a resolver.
                text.Append(CultureInfo.InvariantCulture,
                    $"    iifname \"{iif}\" ip daddr {topology.DnsEndpoint} udp dport {policy.DnsPort} accept\n");
                text.Append(CultureInfo.InvariantCulture,
                    $"    iifname \"{iif}\" ip daddr {topology.DnsEndpoint} tcp dport {policy.DnsPort} accept\n");
            }

            // Everything else that would terminate on the trusted plane, including any service the
            // Hub itself might run there later.
            text.Append(CultureInfo.InvariantCulture, $"    iifname \"{iif}\" drop\n");
        }

        text.Append("  }\n");
    }

    /// <summary>
    /// The source translation that lets an attachment reach the public internet.
    ///
    /// Scoped to the Hub's own pool and to the measured uplink. Unrelated traffic on the shared
    /// plane is not translated by anything here, which is the difference between adding a rule and
    /// taking over the machine's networking.
    /// </summary>
    public string RenderNat()
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"table {NatTableFamily} {NatTableName} {{}}\n");
        text.Append(CultureInfo.InvariantCulture, $"delete table {NatTableFamily} {NatTableName}\n");
        text.Append(CultureInfo.InvariantCulture, $"table {NatTableFamily} {NatTableName} {{\n");
        text.Append("  chain corex_postrouting {\n");
        text.Append("    type nat hook postrouting priority srcnat; policy accept;\n");
        text.Append(CultureInfo.InvariantCulture,
            $"    ip saddr {HardNetworkIdentity.Pool} oifname \"{Uplink}\" masquerade\n");
        text.Append("  }\n");
        text.Append("}\n");

        return text.ToString();
    }

    /// <summary>
    /// Whether the table the environment reports is the one this build renders.
    ///
    /// Compared after normalisation, because <c>nft</c> prints what it holds rather than what it
    /// was given. What normalisation must never do is discard a rule, an address, an interface or a
    /// verdict - so it removes handles and whitespace and nothing else, and any line it does not
    /// recognise is kept and therefore has to match.
    /// </summary>
    public bool Matches(IReadOnlyList<string> activeTableLines) =>
        string.Equals(Canonical(activeTableLines), CanonicalSelf(), StringComparison.Ordinal);

    /// <summary>This ruleset as the loaded table would read.</summary>
    public string CanonicalSelf() => Canonical(Text.Split('\n'));

    /// <summary>Normalises either side of the comparison to the same shape.</summary>
    public static string Canonical(IReadOnlyList<string> lines)
    {
        var kept = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Replace('\t', ' ').Trim();

            if (line.Length == 0)
            {
                continue;
            }

            // The preamble that replaces the table is not part of the loaded table.
            if (line.StartsWith("delete table ", StringComparison.Ordinal)
                || string.Equals(line, $"table {TableFamily} {TableName} {{}}", StringComparison.Ordinal)
                || string.Equals(line, $"table {NatTableFamily} {NatTableName} {{}}", StringComparison.Ordinal))
            {
                continue;
            }

            // Handles are assigned by the kernel and differ between two identical loads.
            var handle = line.IndexOf(" # handle ", StringComparison.Ordinal);

            if (handle >= 0)
            {
                line = line[..handle];
            }

            while (line.Contains("  ", StringComparison.Ordinal))
            {
                line = line.Replace("  ", " ", StringComparison.Ordinal);
            }

            kept.Add(line.TrimEnd());
        }

        return string.Join('\n', kept);
    }
}
