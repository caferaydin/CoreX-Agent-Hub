using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// How an organization is named on the trusted plane, and what the plane's policy says about it.
///
/// None of this needs a machine. That is deliberate: the naming and the policy are the parts that
/// have to be right before anything is built, and the failure modes worth catching here are the
/// ones that would still load and still look like a firewall - two organizations sharing an
/// interface, an allowance placed where it can never be reached, a denial that swallows the route
/// out, or a table whose identity does not change when its contents do.
/// </summary>
public sealed class HardNetworkPolicyTests
{
    private const string OrgA = "acme";
    private const string OrgB = "globex";

    private static HardNetworkTopology Nat(string resolver = "10.255.255.254") => new()
    {
        Mode = HardNetworkMode.Nat,
        DnsEndpoint = resolver,
        DefaultGateway = "192.168.64.1",
        Uplink = "eth0",
        LocalAddresses = ["192.168.66.185"],
        Detail = "fixture",
    };

    private static NftablesRuleset Render(params string[] organizations) =>
        Render(Nat(), organizations);

    private static NftablesRuleset Render(HardNetworkTopology topology, params string[] organizations)
    {
        Assert.True(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10,
                topology,
                organizations.Select(o => HardNetworkIdentity.Describe(o)).ToList(),
                out var ruleset,
                out var problem),
            problem);

        return ruleset!;
    }

    private static List<string> Rules(NftablesRuleset ruleset) =>
        ruleset.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static int IndexOf(NftablesRuleset ruleset, string fragment)
    {
        var index = Rules(ruleset).FindIndex(l => l.Contains(fragment, StringComparison.Ordinal));

        Assert.True(index >= 0, fragment);
        return index;
    }

    // -- identity ------------------------------------------------------------------------------

    /// <summary>
    /// Every name an organization gets comes from its own identity, and always the same one.
    ///
    /// Derived rather than allocated because the plane is machine state the platform destroys on
    /// its own schedule. A name that had to be remembered somewhere could disagree with what is
    /// actually loaded, and the disagreement would be invisible.
    /// </summary>
    [Fact]
    public void An_organizations_names_are_derived_and_stable()
    {
        var first = HardNetworkIdentity.Describe(OrgA);
        var second = HardNetworkIdentity.Describe(OrgA);

        Assert.Equal(first, second);
        Assert.Equal(first, HardNetworkIdentity.Describe(" ACME "));

        Assert.StartsWith("cxr", first.TrustedInterface, StringComparison.Ordinal);
        Assert.StartsWith("cxo", first.NamespaceInterface, StringComparison.Ordinal);
        Assert.EndsWith(".service", HardNetworkIdentity.HolderUnit(first.AttachmentId), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every derived name fits what the kernel will hold, and carries no operator text.
    ///
    /// An organization's display name can contain anything at all; an interface name cannot, and
    /// a name that was truncated to fit would silently become another organization's.
    /// </summary>
    [Theory]
    [InlineData("acme")]
    [InlineData("a-very-long-organization-name-that-nobody-would-choose")]
    [InlineData("Ünïcode Örg")]
    [InlineData("org with spaces")]
    public void Every_derived_name_fits_the_kernels_limit(string organization)
    {
        Assert.True(HardNetworkIdentity.IsNameable(organization));

        var attachment = HardNetworkIdentity.Describe(organization);

        Assert.True(attachment.TrustedInterface.Length < HardNetworkIdentity.MaximumInterfaceNameLength);
        Assert.True(attachment.NamespaceInterface.Length < HardNetworkIdentity.MaximumInterfaceNameLength);
        Assert.All(attachment.TrustedInterface, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    /// <summary>
    /// An attachment can be recovered from the interface it left behind.
    ///
    /// This is what lets a Hub process reconcile a plane it did not build, which is not a nicety:
    /// the platform destroys the plane on its own schedule and the next process to run is the one
    /// that has to make sense of what is there.
    /// </summary>
    [Fact]
    public void An_attachment_is_recoverable_from_its_interface()
    {
        var attachment = HardNetworkIdentity.Describe(OrgA);

        var identity = HardNetworkIdentity.IdentityOf(attachment.TrustedInterface);

        Assert.Equal(attachment.AttachmentId, identity);
        Assert.Equal(attachment, HardNetworkIdentity.FromAttachmentId(identity!));

        // And a name that is not one of ours recovers nothing.
        Assert.Null(HardNetworkIdentity.IdentityOf("eth0"));
        Assert.Null(HardNetworkIdentity.IdentityOf("cxrZZZZZZZZ"));
        Assert.Null(HardNetworkIdentity.IdentityOf(null));
    }

    /// <summary>
    /// Two organizations get different places on the plane, and the same one twice does not.
    ///
    /// On a shared plane a collision is not untidiness. Two organizations on one interface means
    /// one organization's traffic is classified as the other's.
    /// </summary>
    [Fact]
    public void Two_organizations_do_not_share_a_place_on_the_plane()
    {
        var a = HardNetworkIdentity.Describe(OrgA);
        var b = HardNetworkIdentity.Describe(OrgB);

        Assert.NotEqual(a.TrustedInterface, b.TrustedInterface);
        Assert.NotEqual(a.NamespaceInterface, b.NamespaceInterface);
        Assert.NotEqual(a.Subnet, b.Subnet);
        Assert.NotEqual(a.GatewayAddress, b.GatewayAddress);

        Assert.False(HardNetworkIdentity.Collides(OrgA, OrgB));
        Assert.False(HardNetworkIdentity.Collides(OrgA, OrgA));
    }

    /// <summary>
    /// An attachment's subnet, gateway and namespace address describe the same organization.
    ///
    /// They come from one derivation on purpose. Two independent allocations could drift, and an
    /// attachment whose address said one thing and whose interface said another would be a
    /// boundary that classified traffic one way and routed it the other.
    /// </summary>
    [Fact]
    public void An_attachments_addresses_and_interface_agree()
    {
        var attachment = HardNetworkIdentity.Describe(OrgA);

        var network = attachment.Subnet.Split('/')[0];
        var prefix = string.Join('.', network.Split('.').Take(3));

        Assert.StartsWith("10.79.", network, StringComparison.Ordinal);
        Assert.Equal("30", attachment.Subnet.Split('/')[1]);
        Assert.StartsWith(prefix + ".", attachment.GatewayAddress, StringComparison.Ordinal);
        Assert.StartsWith(prefix + ".", attachment.NamespaceAddress, StringComparison.Ordinal);
        Assert.NotEqual(attachment.GatewayAddress, attachment.NamespaceAddress);
    }

    /// <summary>
    /// A rendering that would put two organizations on one interface is refused.
    ///
    /// Not repaired by picking the next free slot: a Hub that reallocated quietly would be a Hub
    /// whose boundary depended on the order things were started in.
    /// </summary>
    [Fact]
    public void A_policy_with_two_attachments_on_one_interface_is_refused()
    {
        var attachment = HardNetworkIdentity.Describe(OrgA);

        Assert.False(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10, Nat(), [attachment, attachment], out var ruleset, out var problem));

        Assert.Null(ruleset);
        Assert.Contains("classify one organization's traffic as another", problem!, StringComparison.Ordinal);
    }

    // -- posture -------------------------------------------------------------------------------

    /// <summary>
    /// The chain policy is not a denial, and every attachment ends in one anyway.
    ///
    /// These chains sit in the namespace the operator's own environment uses, so a default drop
    /// would take the developer's network down. The denial is scoped instead - which is safe for
    /// traffic that arrives on a Hub interface, and is exactly why the fabric is only ready when
    /// every present interface is also a named one.
    /// </summary>
    [Fact]
    public void The_chain_policy_is_permissive_and_every_attachment_still_ends_in_a_drop()
    {
        var ruleset = Render(OrgA, OrgB);
        var rules = Rules(ruleset);

        Assert.DoesNotContain(rules, l => l.Contains("policy drop;", StringComparison.Ordinal));
        Assert.Equal(2, rules.Count(l => l.Contains("policy accept;", StringComparison.Ordinal)));

        foreach (var organization in new[] { OrgA, OrgB })
        {
            var iif = HardNetworkIdentity.Describe(organization).TrustedInterface;

            // One in forward, one in input: nothing arriving on a Hub interface falls through.
            Assert.Equal(2, rules.Count(l => l == $"iifname \"{iif}\" drop"));
        }
    }

    /// <summary>
    /// Nothing the policy says applies to an interface that is not the Hub's.
    ///
    /// The shared plane carries the developer's own traffic. A rule that matched anything else
    /// would be this build quietly filtering somebody else's network.
    /// </summary>
    [Fact]
    public void No_rule_touches_an_interface_that_is_not_a_hub_attachment()
    {
        var ruleset = Render(OrgA, OrgB);

        foreach (var rule in Rules(ruleset).Where(l => l.StartsWith("iifname", StringComparison.Ordinal)))
        {
            var named = rule.Split('"')[1];

            Assert.StartsWith("cxr", named, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("eth0", ruleset.Text, StringComparison.Ordinal);
    }

    /// <summary>Each organization is told apart by interface, never by the address it claims.</summary>
    [Fact]
    public void Organizations_are_classified_by_interface_and_not_by_source_address()
    {
        var ruleset = Render(OrgA, OrgB);

        Assert.DoesNotContain("saddr", ruleset.Text, StringComparison.Ordinal);

        var a = HardNetworkIdentity.Describe(OrgA);

        Assert.DoesNotContain(a.NamespaceAddress, ruleset.Text, StringComparison.Ordinal);
    }

    /// <summary>Peers are denied to each other's interface, explicitly and in both directions.</summary>
    [Fact]
    public void Each_attachment_is_denied_to_every_other()
    {
        var ruleset = Render(OrgA, OrgB);

        var a = HardNetworkIdentity.Describe(OrgA).TrustedInterface;
        var b = HardNetworkIdentity.Describe(OrgB).TrustedInterface;

        Assert.Contains(Rules(ruleset), l => l == $"iifname \"{a}\" oifname \"{b}\" drop");
        Assert.Contains(Rules(ruleset), l => l == $"iifname \"{b}\" oifname \"{a}\" drop");
    }

    // -- ordering ------------------------------------------------------------------------------

    /// <summary>
    /// The resolver allowance is reachable, which is only true because it precedes the drop.
    ///
    /// The resolver terminates on the trusted plane, so the input chain would otherwise swallow it
    /// - the rule would load, read correctly and never match, and name resolution would fail in a
    /// way that looked like a network problem rather than a policy one.
    /// </summary>
    [Fact]
    public void The_resolver_allowance_precedes_the_input_denial()
    {
        var ruleset = Render(OrgA);
        var iif = HardNetworkIdentity.Describe(OrgA).TrustedInterface;

        var dns = IndexOf(ruleset, $"iifname \"{iif}\" ip daddr 10.255.255.254 udp dport 53 accept");
        var deny = Rules(ruleset).FindLastIndex(l => l == $"iifname \"{iif}\" drop");

        Assert.True(dns < deny);
    }

    /// <summary>
    /// The public allowance follows every denial, which is what makes it "public".
    ///
    /// Placed first it would be unrestricted outbound on one port, the host's own services
    /// included, and would read exactly the same in review.
    /// </summary>
    [Fact]
    public void The_public_allowance_follows_every_denial()
    {
        var ruleset = Render(OrgA);
        var iif = HardNetworkIdentity.Describe(OrgA).TrustedInterface;

        var https = IndexOf(ruleset, $"iifname \"{iif}\" tcp dport 443 accept");

        foreach (var denied in HardNetworkPolicy.Phase10.DeniedDestinations)
        {
            Assert.True(IndexOf(ruleset, $"iifname \"{iif}\" ip daddr {denied.Cidr} drop") < https, denied.Cidr);
        }
    }

    /// <summary>The Hub's own pool is inside a denied range, so no attachment is a public destination.</summary>
    [Fact]
    public void The_attachment_pool_is_not_a_public_destination()
    {
        Assert.StartsWith("10.79.", HardNetworkIdentity.Pool, StringComparison.Ordinal);

        Assert.Contains(HardNetworkPolicy.Phase10.DeniedDestinations, d => d.Cidr == "10.0.0.0/8");
    }

    /// <summary>
    /// The uplink is named only where traffic is translated, never where it is classified.
    ///
    /// The gateway and the uplink are how packets leave; denying a destination is not denying a
    /// route, and conflating the two would either leave the host reachable or leave the workload
    /// with no network at all.
    /// </summary>
    [Fact]
    public void The_uplink_appears_only_in_translation()
    {
        var ruleset = Render(OrgA);

        Assert.DoesNotContain("eth0", ruleset.Text, StringComparison.Ordinal);
        Assert.Contains("oifname \"eth0\" masquerade", ruleset.RenderNat(), StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.64.1", ruleset.Text, StringComparison.Ordinal);
    }

    // -- translation ---------------------------------------------------------------------------

    /// <summary>
    /// Translation is scoped to the Hub's own pool, which is the difference between adding a rule
    /// and taking over the machine's networking.
    /// </summary>
    [Fact]
    public void Translation_covers_only_the_hubs_own_pool()
    {
        var nat = Render(OrgA).RenderNat();

        Assert.Contains($"ip saddr {HardNetworkIdentity.Pool}", nat, StringComparison.Ordinal);
        Assert.Contains("policy accept;", nat, StringComparison.Ordinal);
        Assert.DoesNotContain("flush", nat, StringComparison.Ordinal);

        // One rule, one table, and it is the Hub's.
        Assert.Contains(NftablesRuleset.NatTableName, nat, StringComparison.Ordinal);
        Assert.Single(nat.Split('\n'), l => l.Contains("masquerade", StringComparison.Ordinal));
    }

    // -- refusal -------------------------------------------------------------------------------

    [Theory]
    [InlineData(HardNetworkMode.Mirrored)]
    [InlineData(HardNetworkMode.Unknown)]
    public void An_unsupported_arrangement_renders_nothing(HardNetworkMode mode)
    {
        Assert.False(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10,
                Nat() with { Mode = mode },
                [HardNetworkIdentity.Describe(OrgA)],
                out var ruleset,
                out _));

        Assert.Null(ruleset);
    }

    /// <summary>An environment that reports no resolver renders nothing rather than a wider rule.</summary>
    [Fact]
    public void An_environment_with_no_resolver_renders_nothing() =>
        Assert.False(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10,
                Nat(resolver: string.Empty),
                [HardNetworkIdentity.Describe(OrgA)],
                out _,
                out _));

    /// <summary>Without an uplink there is nowhere to translate out of, so nothing is rendered.</summary>
    [Fact]
    public void An_environment_with_no_uplink_renders_nothing() =>
        Assert.False(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10,
                Nat() with { Uplink = string.Empty },
                [HardNetworkIdentity.Describe(OrgA)],
                out _,
                out _));

    // -- identity of the policy ------------------------------------------------------------------

    /// <summary>The same inputs always render the same bytes, because the bytes are the name.</summary>
    [Fact]
    public void Rendering_is_deterministic()
    {
        var first = Render(OrgA, OrgB);
        var second = Render(OrgB, OrgA);

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.Identity, second.Identity);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first.Text))),
            first.Identity);
    }

    /// <summary>A different set of attachments is a different policy, and says so.</summary>
    [Fact]
    public void A_different_attachment_set_is_a_different_identity()
    {
        Assert.NotEqual(Render(OrgA).Identity, Render(OrgA, OrgB).Identity);
        Assert.NotEqual(Render(OrgA).Identity, Render(OrgB).Identity);
    }

    /// <summary>The interfaces a policy names are exactly the ones the fabric must hold.</summary>
    [Fact]
    public void A_policy_reports_the_interfaces_it_classifies()
    {
        var ruleset = Render(OrgA, OrgB);

        Assert.Equal(
            new[] { OrgA, OrgB }.Select(o => HardNetworkIdentity.Describe(o).TrustedInterface).Order(StringComparer.Ordinal),
            ruleset.TrustedInterfaces.Order(StringComparer.Ordinal));
    }

    // -- comparison ----------------------------------------------------------------------------

    /// <summary>A table matches itself once the noise a loaded table adds is normalised away.</summary>
    [Fact]
    public void A_loaded_table_matches_what_was_rendered()
    {
        var ruleset = Render(OrgA, OrgB);

        var loaded = ruleset.Text
            .Split('\n')
            .Where(l => !l.StartsWith("delete table", StringComparison.Ordinal))
            .Where(l => l.Trim() != $"table inet {NftablesRuleset.TableName} {{}}")
            .Select((l, i) => l.Trim().Length == 0 ? l : "\t\t" + l.Trim() + " # handle " + i)
            .ToList();

        Assert.True(ruleset.Matches(loaded));
    }

    /// <summary>
    /// One changed rule is not noise, and the comparison says so.
    ///
    /// This is what stops normalisation from becoming the hole. A table that denies a range and a
    /// table that permits it must not compare equal because they are the same shape.
    /// </summary>
    [Theory]
    [InlineData("ip daddr 10.0.0.0/8 drop", "ip daddr 10.0.0.0/32 drop")]
    [InlineData("tcp dport 443 accept", "tcp dport 1-65535 accept")]
    [InlineData("policy accept;", "policy drop;")]
    [InlineData("udp dport 53 accept", "udp dport 53-1000 accept")]
    public void A_changed_rule_does_not_match(string original, string tampered)
    {
        var ruleset = Render(OrgA);

        Assert.False(
            ruleset.Matches(ruleset.Text.Replace(original, tampered, StringComparison.Ordinal).Split('\n')));
    }

    /// <summary>A removed rule does not match either, which a shape comparison would allow.</summary>
    [Fact]
    public void A_removed_rule_does_not_match()
    {
        var ruleset = Render(OrgA);

        Assert.False(
            ruleset.Matches(
                ruleset.Text.Split('\n')
                    .Where(l => !l.Contains("ip daddr 10.0.0.0/8 drop", StringComparison.Ordinal))
                    .ToList()));
    }

    /// <summary>
    /// A policy for one organization does not match a policy for another.
    ///
    /// The single most important comparison here. On a shared plane every organization sees the
    /// same table, so a verification that could not tell two policies apart would let one
    /// organization's boundary stand in for another's.
    /// </summary>
    [Fact]
    public void One_organizations_policy_does_not_match_anothers()
    {
        Assert.False(Render(OrgA).Matches(Rules(Render(OrgB))));
        Assert.False(Render(OrgA, OrgB).Matches(Rules(Render(OrgA))));
        Assert.False(Render(OrgA).Matches(Rules(Render(OrgA, OrgB))));
    }

    /// <summary>The Hub's table is the only thing named, so an operator's rules survive.</summary>
    [Fact]
    public void Only_the_hubs_own_tables_are_named()
    {
        var ruleset = Render(OrgA);

        Assert.DoesNotContain("flush ruleset", ruleset.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("flush table", ruleset.Text, StringComparison.Ordinal);

        var deletes = Rules(ruleset).Where(l => l.StartsWith("delete", StringComparison.Ordinal)).ToList();

        Assert.Equal([$"delete table inet {NftablesRuleset.TableName}"], deletes);
    }
}
