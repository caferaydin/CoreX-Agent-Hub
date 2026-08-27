using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// The Hub's network policy, rendered as the one table it owns.
///
/// One table, <c>inet corex_hard</c>, and nothing else. The machine's own ruleset is not flushed,
/// not replaced and not read for meaning: an operator's rules are theirs, and a boundary that
/// began by deleting them would be a boundary nobody could run beside anything else.
///
/// Sharing the ruleset is safe here for a reason worth stating, because it is the whole argument
/// for this mechanism. In nftables every base chain registered on a hook sees the packet, and a
/// chain that accepts does not end the packet's journey through the others - but a chain that
/// drops does. So another table cannot rescue traffic this one drops, whatever its priority. That
/// is what makes a Hub-owned table an authority rather than a suggestion, and it is the property
/// that would have to be re-established if the mechanism ever changed.
///
/// Rendering is deterministic to the byte, because the rendered text is the policy's identity. The
/// Hub does not ask "are there some rules"; it asks whether the table that is loaded is the one
/// this build would write, and it can only ask that if the same inputs always produce the same
/// bytes.
/// </summary>
public sealed record NftablesRuleset
{
    /// <summary>The one table the Hub owns. Nothing outside it is read, written or judged.</summary>
    public const string TableFamily = "inet";

    public const string TableName = "corex_hard";

    private NftablesRuleset(string text, HardNetworkTopology topology)
    {
        Text = text;
        Topology = topology;
        Identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Exactly what would be handed to <c>nft -f -</c>.</summary>
    public string Text { get; }

    /// <summary>What the ruleset was rendered against.</summary>
    public HardNetworkTopology Topology { get; }

    /// <summary>The SHA-256 of <see cref="Text"/>. The Hub's name for this policy.</summary>
    public string Identity { get; }

    /// <summary>
    /// Renders the policy for one environment.
    ///
    /// Refuses rather than improvises. An unsupported topology or a missing resolver is not a
    /// ruleset with a gap in it - it is a machine the policy was not designed for, and rendering
    /// something anyway would produce a table that loads, looks enforced and denies the wrong set.
    /// </summary>
    public static bool TryRender(
        HardNetworkPolicy policy,
        HardNetworkTopology topology,
        out NftablesRuleset? ruleset,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(topology);

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

        ruleset = new NftablesRuleset(Render(policy, topology), topology);
        return true;
    }

    private static string Render(HardNetworkPolicy policy, HardNetworkTopology topology)
    {
        var text = new StringBuilder();

        // Written as a replacement of the Hub's own table only. delete-then-add rather than a
        // flush, so a partially updated table is never live: nft applies a file atomically.
        text.Append(CultureInfo.InvariantCulture, $"table {TableFamily} {TableName} {{}}\n");
        text.Append(CultureInfo.InvariantCulture, $"delete table {TableFamily} {TableName}\n");
        text.Append(CultureInfo.InvariantCulture, $"table {TableFamily} {TableName} {{\n");

        RenderOutput(text, policy, topology);
        RenderInput(text, policy);
        RenderForward(text);

        text.Append("}\n");

        return text.ToString();
    }

    private static void RenderOutput(StringBuilder text, HardNetworkPolicy policy, HardNetworkTopology topology)
    {
        text.Append("  chain corex_output {\n");
        text.Append("    type filter hook output priority -300; policy drop;\n");

        // The environment's own loopback, and only as loopback. A packet addressed to 127/8 that
        // leaves by any other interface is how a host bridge appears, and it is dropped below.
        text.Append("    oifname \"lo\" ip daddr 127.0.0.0/8 accept\n");
        text.Append("    oifname \"lo\" ip6 daddr ::1/128 accept\n");

        if (policy.AllowDns)
        {
            // The measured resolver, on the resolver port, and nothing else at that address. The
            // allowance comes before the private denials on purpose: the resolver is itself a
            // private address, so an allowance placed after them would never be reached.
            text.Append(CultureInfo.InvariantCulture,
                $"    ip daddr {topology.DnsEndpoint} udp dport {policy.DnsPort} accept\n");
            text.Append(CultureInfo.InvariantCulture,
                $"    ip daddr {topology.DnsEndpoint} tcp dport {policy.DnsPort} accept\n");
        }

        // Destinations, never next hops. The default gateway is a private address and is still how
        // public traffic leaves; denying a destination does not deny a route.
        foreach (var denied in policy.DeniedDestinations)
        {
            text.Append(CultureInfo.InvariantCulture, $"    ip daddr {denied.Cidr} drop\n");
        }

        foreach (var denied in policy.DeniedDestinationsV6)
        {
            text.Append(CultureInfo.InvariantCulture, $"    ip6 daddr {denied.Cidr} drop\n");
        }

        if (policy.AllowPublicHttps)
        {
            // Reached only by what survived every denial above, which is what makes this "public"
            // rather than "any". A port, not a name: the workload chooses the service.
            text.Append(CultureInfo.InvariantCulture,
                $"    tcp dport {policy.PublicHttpsPort} accept\n");
        }

        // Everything else. Stated rather than left to the policy line, so the file reads as what
        // it does.
        text.Append("    drop\n");
        text.Append("  }\n");
    }

    private static void RenderInput(StringBuilder text, HardNetworkPolicy policy)
    {
        text.Append("  chain corex_input {\n");
        text.Append("    type filter hook input priority -300; policy drop;\n");
        text.Append("    iifname \"lo\" accept\n");

        // Sources, not destinations, and before the established allowance. A flow that was opened
        // to a denied destination does not become acceptable by having been opened.
        foreach (var denied in policy.DeniedDestinations)
        {
            text.Append(CultureInfo.InvariantCulture, $"    ip saddr {denied.Cidr} drop\n");
        }

        foreach (var denied in policy.DeniedDestinationsV6)
        {
            text.Append(CultureInfo.InvariantCulture, $"    ip6 saddr {denied.Cidr} drop\n");
        }

        text.Append("    ct state established,related accept\n");
        text.Append("    drop\n");
        text.Append("  }\n");
    }

    private static void RenderForward(StringBuilder text)
    {
        // A workload is not a router. Nothing is forwarded, and saying so is cheaper than
        // discovering later that something was.
        text.Append("  chain corex_forward {\n");
        text.Append("    type filter hook forward priority -300; policy drop;\n");
        text.Append("    drop\n");
        text.Append("  }\n");
    }

    /// <summary>
    /// Whether the table the environment reports is the one this build renders.
    ///
    /// Compared after normalisation, because <c>nft</c> prints what it holds rather than what it
    /// was given: handles are assigned, indentation differs, and the delete-and-recreate preamble
    /// is not part of a loaded table. What normalisation must never do is discard a rule, an
    /// address or a verdict - so it removes handles and whitespace and nothing else, and any line
    /// it does not recognise is kept and therefore has to match.
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
                || string.Equals(line, $"table {TableFamily} {TableName} {{}}", StringComparison.Ordinal))
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
