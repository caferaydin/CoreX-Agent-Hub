using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// What the network policy says, and what the rendered ruleset does with it.
///
/// None of this needs a machine. That is deliberate: the policy is the part that has to be right
/// before enforcement is worth attempting, and the failure modes worth catching here are the ones
/// that would still load, still look like a firewall, and still deny the wrong set - a permissive
/// default, an allowance placed where it can never be reached, a denial that swallows the route
/// out, or a table whose identity does not change when its contents do.
/// </summary>
public sealed class HardNetworkPolicyTests
{
    private static HardNetworkTopology Nat(string resolver = "10.255.255.254") => new()
    {
        Mode = HardNetworkMode.Nat,
        DnsEndpoint = resolver,
        DefaultGateway = "192.168.64.1",
        LocalAddresses = ["192.168.66.185"],
        Detail = "fixture",
    };

    private static NftablesRuleset Render(HardNetworkTopology topology)
    {
        Assert.True(
            NftablesRuleset.TryRender(HardNetworkPolicy.Phase10, topology, out var ruleset, out var problem),
            problem);

        return ruleset!;
    }

    private static List<string> Rules(NftablesRuleset ruleset) =>
        ruleset.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static int IndexOf(NftablesRuleset ruleset, string fragment)
    {
        var rules = Rules(ruleset);

        var index = rules
            .Select((line, i) => (line, i))
            .Where(x => x.line.Contains(fragment, StringComparison.Ordinal))
            .Select(x => x.i)
            .ToList();

        Assert.NotEmpty(index);
        return index[0];
    }

    // -- the policy itself --------------------------------------------------------------------

    /// <summary>
    /// The policy is capabilities and address ranges, and there is one of it.
    ///
    /// A second instance would be a second boundary, and the one an organization got would depend
    /// on which code path built it.
    /// </summary>
    [Fact]
    public void The_policy_is_a_single_frozen_instance()
    {
        Assert.Same(HardNetworkPolicy.Phase10, HardNetworkPolicy.Phase10);

        Assert.True(HardNetworkPolicy.Phase10.AllowDns);
        Assert.True(HardNetworkPolicy.Phase10.AllowPublicHttps);

        // Every denial carries a reason, so a later reader can judge whether it is load bearing.
        Assert.All(HardNetworkPolicy.Phase10.DeniedDestinations, d => Assert.NotEmpty(d.Reason));
        Assert.All(HardNetworkPolicy.Phase10.DeniedDestinationsV6, d => Assert.NotEmpty(d.Reason));
    }

    /// <summary>
    /// Every address family a workload could use is closed, not just the one in use today.
    ///
    /// A boundary that held only for IPv4 would fail the first time an environment routed IPv6,
    /// silently and in the permissive direction.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.168.0.0/16")]
    [InlineData("100.64.0.0/10")]
    [InlineData("169.254.0.0/16")]
    [InlineData("127.0.0.0/8")]
    public void Every_private_class_is_denied(string cidr) =>
        Assert.Contains(HardNetworkPolicy.Phase10.DeniedDestinations, d => d.Cidr == cidr);

    [Theory]
    [InlineData("fc00::/7")]
    [InlineData("fe80::/10")]
    [InlineData("::1/128")]
    public void Every_private_v6_class_is_denied(string cidr) =>
        Assert.Contains(HardNetworkPolicy.Phase10.DeniedDestinationsV6, d => d.Cidr == cidr);

    // -- default posture ----------------------------------------------------------------------

    /// <summary>
    /// Every chain starts from denial.
    ///
    /// The difference between "drop by default, allow these" and "allow by default, drop these" is
    /// the difference between a rule nobody wrote being safe and being a hole. A future addition
    /// to the machine's network stack is the case that decides it, and nobody will be reviewing
    /// this file when that happens.
    /// </summary>
    [Fact]
    public void Every_chain_starts_from_denial()
    {
        var ruleset = Render(Nat());

        Assert.Equal(3, Rules(ruleset).Count(l => l.Contains("policy drop;", StringComparison.Ordinal)));

        foreach (var hook in new[] { "hook output", "hook input", "hook forward" })
        {
            var chain = Rules(ruleset).Single(l => l.Contains(hook, StringComparison.Ordinal));

            Assert.Contains("policy drop;", chain, StringComparison.Ordinal);
        }

        // And each chain also ends in an explicit drop, so the file reads as what it does.
        Assert.Equal(3, Rules(ruleset).Count(l => l == "drop"));
    }

    /// <summary>Nothing is forwarded, because a workload is not a router.</summary>
    [Fact]
    public void The_forward_chain_permits_nothing()
    {
        var rules = Rules(Render(Nat()));

        var start = rules.FindIndex(l => l.Contains("hook forward", StringComparison.Ordinal));

        Assert.True(start > 0);

        var chain = rules.Skip(start).TakeWhile(l => l != "}").ToList();

        Assert.DoesNotContain(chain, l => l.Contains("accept", StringComparison.Ordinal));
    }

    // -- ordering -----------------------------------------------------------------------------

    /// <summary>
    /// The resolver allowance is reachable, which is only true because it precedes the denials.
    ///
    /// The resolver is itself a private address. Placed after the private denials the rule would
    /// load, read correctly and never match, and name resolution would fail in a way that looked
    /// like a network problem rather than a policy one.
    /// </summary>
    [Fact]
    public void The_resolver_allowance_precedes_the_private_denials()
    {
        var ruleset = Render(Nat());

        var dns = IndexOf(ruleset, "ip daddr 10.255.255.254 udp dport 53 accept");
        var privateDenial = IndexOf(ruleset, "ip daddr 10.0.0.0/8 drop");

        Assert.True(dns < privateDenial);
    }

    /// <summary>
    /// The public allowance comes after every denial, which is what makes it "public".
    ///
    /// Placed first it would be an unrestricted outbound rule on one port - the host's own
    /// services included - and would read exactly the same in review.
    /// </summary>
    [Fact]
    public void The_public_allowance_follows_every_denial()
    {
        var ruleset = Render(Nat());

        var https = IndexOf(ruleset, "tcp dport 443 accept");

        foreach (var denied in HardNetworkPolicy.Phase10.DeniedDestinations)
        {
            Assert.True(IndexOf(ruleset, $"ip daddr {denied.Cidr} drop") < https, denied.Cidr);
        }

        foreach (var denied in HardNetworkPolicy.Phase10.DeniedDestinationsV6)
        {
            Assert.True(IndexOf(ruleset, $"ip6 daddr {denied.Cidr} drop") < https, denied.Cidr);
        }
    }

    /// <summary>
    /// An established flow to a denied source is still denied.
    ///
    /// The conntrack allowance is what lets a permitted connection get its replies, and it is also
    /// the classic way a boundary is bypassed: a flow opened before the policy existed, or to a
    /// destination the policy denies, keeps working because it is "established". Placing the
    /// source denials first is what stops that from being true.
    /// </summary>
    [Fact]
    public void Established_traffic_is_permitted_only_after_the_source_denials()
    {
        var ruleset = Render(Nat());

        var established = IndexOf(ruleset, "ct state established,related accept");

        foreach (var denied in HardNetworkPolicy.Phase10.DeniedDestinations)
        {
            Assert.True(IndexOf(ruleset, $"ip saddr {denied.Cidr} drop") < established, denied.Cidr);
        }

        // And it is an input concern only; nothing outbound is permitted for being established.
        var rules = Rules(ruleset);
        var outputStart = rules.FindIndex(l => l.Contains("hook output", StringComparison.Ordinal));
        var inputStart = rules.FindIndex(l => l.Contains("hook input", StringComparison.Ordinal));

        Assert.True(established > inputStart && inputStart > outputStart);
    }

    /// <summary>
    /// Loopback is permitted as an interface, not as an address.
    ///
    /// A packet addressed to 127.0.0.1 that leaves by any other interface is how a bridged host
    /// loopback appears. Permitting the address rather than the interface would let exactly that
    /// through while looking like an ordinary local-traffic exemption.
    /// </summary>
    [Fact]
    public void Loopback_is_permitted_by_interface_and_denied_by_address()
    {
        var ruleset = Render(Nat());

        Assert.Contains(Rules(ruleset), l => l == "oifname \"lo\" ip daddr 127.0.0.0/8 accept");

        // The bare address is still denied for anything leaving another way.
        Assert.Contains(Rules(ruleset), l => l == "ip daddr 127.0.0.0/8 drop");

        Assert.True(
            IndexOf(ruleset, "oifname \"lo\" ip daddr 127.0.0.0/8 accept")
            < IndexOf(ruleset, "ip daddr 127.0.0.0/8 drop"));
    }

    /// <summary>
    /// The gateway is never named as a destination, and is denied as one.
    ///
    /// The trap the whole design turns on. The default gateway is a private address and is still
    /// the next hop for every public packet; denying a destination is not denying a route. A rule
    /// that named the gateway specifically would either be redundant or would be somebody
    /// confusing the two.
    /// </summary>
    [Fact]
    public void The_gateway_is_not_a_named_destination_but_its_range_is_denied()
    {
        var topology = Nat();
        var ruleset = Render(topology);

        Assert.DoesNotContain(topology.DefaultGateway, ruleset.Text, StringComparison.Ordinal);

        // It falls inside a denied range, so reaching the host directly is refused.
        Assert.Contains(Rules(ruleset), l => l == "ip daddr 192.168.0.0/16 drop");

        // And nothing in the ruleset matches on an interface for outbound public traffic, which
        // is what would have coupled the denial to the route.
        Assert.DoesNotContain("oifname \"eth", ruleset.Text, StringComparison.Ordinal);
    }

    // -- refusal ------------------------------------------------------------------------------

    /// <summary>
    /// An unsupported arrangement produces no ruleset at all.
    ///
    /// Rendering something anyway would produce a table that loads, looks enforced, and denies a
    /// set that has nothing to do with where the host actually is.
    /// </summary>
    [Theory]
    [InlineData(HardNetworkMode.Mirrored)]
    [InlineData(HardNetworkMode.Unknown)]
    public void An_unsupported_arrangement_renders_nothing(HardNetworkMode mode)
    {
        var topology = Nat() with { Mode = mode };

        Assert.False(
            NftablesRuleset.TryRender(HardNetworkPolicy.Phase10, topology, out var ruleset, out var problem));

        Assert.Null(ruleset);
        Assert.NotNull(problem);
    }

    /// <summary>
    /// An environment that reports no resolver renders nothing.
    ///
    /// The name resolution allowance has to name an address. Rendering without one would either
    /// drop the allowance - leaving a workload that cannot resolve anything and no rule saying so -
    /// or widen it to a range, which is a different policy than the one that was reviewed.
    /// </summary>
    [Fact]
    public void An_environment_with_no_resolver_renders_nothing()
    {
        Assert.False(
            NftablesRuleset.TryRender(
                HardNetworkPolicy.Phase10, Nat(resolver: string.Empty), out var ruleset, out _));

        Assert.Null(ruleset);
    }

    // -- identity -----------------------------------------------------------------------------

    /// <summary>
    /// The same inputs always render the same bytes, because the bytes are the policy's name.
    ///
    /// Non-determinism here would not look like a bug. It would look like policy drift: the Hub
    /// would report the loaded table as unexpected on every second run.
    /// </summary>
    [Fact]
    public void Rendering_is_deterministic()
    {
        var first = Render(Nat());
        var second = Render(Nat());

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.Identity, second.Identity);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first.Text))),
            first.Identity);
    }

    /// <summary>A different environment is a different policy, and says so.</summary>
    [Fact]
    public void A_different_resolver_is_a_different_identity()
    {
        Assert.NotEqual(
            Render(Nat()).Identity,
            Render(Nat(resolver: "10.255.255.53")).Identity);
    }

    // -- comparison ---------------------------------------------------------------------------

    /// <summary>
    /// A table matches itself once the noise a loaded table adds is normalised away.
    ///
    /// The noise is real - the kernel assigns handles, and the preamble that replaces the table is
    /// not part of the table - so the comparison has to tolerate it. The next test is the one that
    /// matters: tolerating that must not become tolerating a changed rule.
    /// </summary>
    [Fact]
    public void A_loaded_table_matches_what_was_rendered()
    {
        var ruleset = Render(Nat());

        // How nft prints it back: no preamble, indented differently, handles attached.
        var loaded = ruleset.Text
            .Split('\n')
            .Where(l => !l.StartsWith("delete table", StringComparison.Ordinal))
            .Where(l => l.Trim() != "table inet corex_hard {}")
            .Select((l, i) => l.Trim().Length == 0 ? l : "\t\t" + l.Trim() + " # handle " + i)
            .ToList();

        Assert.True(ruleset.Matches(loaded));
    }

    /// <summary>
    /// One changed address is not noise, and the comparison says so.
    ///
    /// This is what stops normalisation from becoming the hole. A table that denies 10/8 and a
    /// table that denies nothing of the sort must not compare equal because they are the same
    /// shape.
    /// </summary>
    [Theory]
    [InlineData("ip daddr 10.0.0.0/8 drop", "ip daddr 10.0.0.0/32 drop")]
    [InlineData("ip daddr 192.168.0.0/16 drop", "ip daddr 192.168.0.0/16 accept")]
    [InlineData("tcp dport 443 accept", "tcp dport 1-65535 accept")]
    [InlineData("policy drop;", "policy accept;")]
    public void A_changed_rule_does_not_match(string original, string tampered)
    {
        var ruleset = Render(Nat());

        var loaded = ruleset.Text
            .Replace(original, tampered, StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        Assert.False(ruleset.Matches(loaded));
    }

    /// <summary>A missing rule does not match either, which a shape comparison would allow.</summary>
    [Fact]
    public void A_removed_rule_does_not_match()
    {
        var ruleset = Render(Nat());

        var loaded = ruleset.Text
            .Split('\n')
            .Where(l => !l.Contains("ip daddr 10.0.0.0/8 drop", StringComparison.Ordinal))
            .ToList();

        Assert.False(ruleset.Matches(loaded));
    }

    /// <summary>The Hub's table is the only thing named, so an operator's rules survive.</summary>
    [Fact]
    public void Only_the_hubs_own_table_is_named()
    {
        var ruleset = Render(Nat());

        Assert.Contains("table inet corex_hard", ruleset.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("flush ruleset", ruleset.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("flush table", ruleset.Text, StringComparison.Ordinal);

        // The only delete is of the Hub's own table, and it is what makes the load a replacement
        // rather than an append.
        var deletes = Rules(ruleset).Where(l => l.StartsWith("delete", StringComparison.Ordinal)).ToList();

        Assert.Equal(["delete table inet corex_hard"], deletes);
    }
}
