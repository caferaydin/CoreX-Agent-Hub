namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// A destination range a hard isolated workload may not address, and why.
///
/// The reason is not decoration. A denied range without one is a range nobody can later judge:
/// the next person cannot tell whether it is load bearing or was copied from a list, and the
/// safe-looking edit is to remove it.
/// </summary>
public sealed record DeniedDestination(string Cidr, string Reason);

/// <summary>
/// What a hard isolated workload may and may not reach on the network.
///
/// Effects, not syntax. This says nothing about nftables, chains or priorities - a policy that
/// described a mechanism would have to change when the mechanism did, and the thing worth keeping
/// stable is the claim: the host and its control plane are unreachable, so are peer organizations
/// and the private networks they live on, while name resolution and ordinary public HTTPS still
/// work because a provider runtime cannot function without them.
///
/// Provider neutral by construction. There is no hostname here, no vendor, no token, and no way to
/// express one: an allowance is a capability - resolve names, reach public HTTPS - and which
/// service a workload then talks to is not isolation's business. A hostname allowlist would also
/// be worse security theatre than it looks, because it would have to be enforced on a name the
/// workload itself supplies.
///
/// Closed rather than configurable. This is product behaviour for Phase 10, so there is one
/// instance and no builder: an organization cannot widen its own boundary through configuration,
/// and an operator cannot accidentally ship a hard environment with a policy nobody reviewed.
/// </summary>
public sealed record HardNetworkPolicy
{
    private HardNetworkPolicy()
    {
    }

    /// <summary>
    /// The one policy this build enforces.
    ///
    /// Frozen deliberately. Every future relaxation should be visible as a change to this file
    /// rather than as a value in somebody's configuration.
    /// </summary>
    public static HardNetworkPolicy Phase10 { get; } = new();

    /// <summary>
    /// Whether the workload may open ordinary public HTTPS.
    ///
    /// Required, not optional: a provider runtime that cannot reach its own service is not
    /// isolated, it is broken. The allowance is a port and a destination class, never a name.
    /// </summary>
    public bool AllowPublicHttps => true;

    /// <summary>
    /// Whether the workload may resolve names.
    ///
    /// Narrow on purpose - see <see cref="DnsPort"/>. The resolver is reached at an address the
    /// environment reports rather than one this file believes, and only on the resolver port, so
    /// the allowance cannot become a general channel to whatever else is listening there.
    /// </summary>
    public bool AllowDns => true;

    /// <summary>The only port the DNS allowance covers.</summary>
    public int DnsPort => 53;

    /// <summary>The only port the public allowance covers.</summary>
    public int PublicHttpsPort => 443;

    /// <summary>
    /// Everything a hard workload may not address, with the reason each range is here.
    ///
    /// Two of these carry most of the weight. RFC1918 is what makes the host unreachable on every
    /// address it has - the environment's gateway subnet, the machine's LAN address and any
    /// hypervisor switch are all inside it - and it is also what denies a peer organization's
    /// environment, because in the supported topology every environment shares one private
    /// subnet. The rest close the ways a private destination can be spelled without looking
    /// private.
    ///
    /// Deliberately expressed as destinations. The default gateway is a private address and it is
    /// still the next hop for public traffic; denying a destination is not denying a route, and
    /// conflating the two would either leave the host reachable or leave the workload with no
    /// network at all.
    /// </summary>
    public IReadOnlyList<DeniedDestination> DeniedDestinations { get; } =
    [
        new("10.0.0.0/8", "RFC1918 private; carries the host on some topologies and every peer environment."),
        new("172.16.0.0/12", "RFC1918 private; a hypervisor's default switch subnet lives here."),
        new("192.168.0.0/16", "RFC1918 private; the environment's gateway subnet and the host's own LAN address."),
        new("100.64.0.0/10", "Carrier grade NAT; private in effect and routable to a provider's own network."),
        new("169.254.0.0/16", "IPv4 link-local; reaches neighbours without a router, including the host."),
        new("127.0.0.0/8", "Loopback addressed off the loopback interface, which is how a host bridge appears."),
        new("0.0.0.0/8", "This network; an unspecified destination is not a destination a workload needs."),
        new("224.0.0.0/4", "Multicast; discovery of hosts and services on the local segment."),
        new("240.0.0.0/4", "Reserved; no legitimate destination, and a gap policy would otherwise leave open."),
    ];

    /// <summary>
    /// The same closures for IPv6, for environments that have any of it.
    ///
    /// Enforced whether or not this machine routes IPv6 today. A boundary that holds only for the
    /// address family the operator happens to be using is a boundary that fails the first time
    /// that changes, silently and in the permissive direction.
    /// </summary>
    public IReadOnlyList<DeniedDestination> DeniedDestinationsV6 { get; } =
    [
        new("::1/128", "IPv6 loopback addressed off the loopback interface."),
        new("fc00::/7", "Unique local addresses; the IPv6 equivalent of RFC1918."),
        new("fe80::/10", "Link-local; reaches the host and neighbours without a router."),
        new("ff00::/8", "Multicast; local segment discovery."),
        new("::/128", "Unspecified."),
    ];
}
