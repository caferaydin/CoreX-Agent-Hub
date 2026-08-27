using System.Globalization;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>How an environment's network is arranged, as measured rather than as configured.</summary>
public enum HardNetworkMode
{
    /// <summary>Not established. Never ready.</summary>
    Unknown = 0,

    /// <summary>The environment has its own subnet behind a gateway. The supported arrangement.</summary>
    Nat = 1,

    /// <summary>
    /// The environment shares the host's interfaces, including its loopback.
    ///
    /// Unsupported, and not because it is inconvenient: the control plane would be reachable at an
    /// address no destination rule denies, so the boundary would be absent rather than weaker.
    /// </summary>
    Mirrored = 2,
}

/// <summary>
/// The facts a network policy has to be rendered against.
///
/// Measured from the environment every time. The resolver's address in particular is not something
/// this code may believe it knows - it is an implementation detail of the platform's DNS
/// arrangement, it has changed before, and a policy that allowed a remembered address would either
/// break name resolution or leave an allowance pointing somewhere nothing is listening.
/// </summary>
public sealed record HardNetworkTopology
{
    public required HardNetworkMode Mode { get; init; }

    /// <summary>The resolver the environment reports, or empty if it reports none.</summary>
    public required string DnsEndpoint { get; init; }

    /// <summary>The default route's next hop, or empty. Never used as a policy destination.</summary>
    public required string DefaultGateway { get; init; }

    /// <summary>The environment's own routable addresses, for diagnosis only.</summary>
    public required IReadOnlyList<string> LocalAddresses { get; init; }

    /// <summary>Why the mode was classified as it was, in one line.</summary>
    public required string Detail { get; init; }

    public static HardNetworkTopology Unknown(string detail) => new()
    {
        Mode = HardNetworkMode.Unknown,
        DnsEndpoint = string.Empty,
        DefaultGateway = string.Empty,
        LocalAddresses = [],
        Detail = detail,
    };

    /// <summary>
    /// Classifies the arrangement from what the environment says about itself.
    ///
    /// The distinguishing fact is not a configuration key. Under the shared-interface arrangement
    /// the environment carries the host's own addresses and has no gateway of its own; under the
    /// supported one it has a private address on its own link and a next hop to reach anything
    /// else. So the classification is made from the addresses and the routing table, and a machine
    /// that produces neither shape is Unknown rather than assumed.
    /// </summary>
    public static HardNetworkTopology Classify(
        IReadOnlyList<string> addressLines,
        IReadOnlyList<string> routeLines,
        IReadOnlyList<string> resolverLines)
    {
        var addresses = ParseAddresses(addressLines);
        var gateway = ParseDefaultGateway(routeLines);
        var resolver = ParseResolver(resolverLines);

        var routable = addresses
            .Where(a => !a.StartsWith("127.", StringComparison.Ordinal))
            .ToList();

        if (routable.Count == 0)
        {
            return Unknown("The environment reports no routable address.");
        }

        if (gateway.Length == 0)
        {
            // No next hop at all. Either the shared-interface arrangement or a broken link; both
            // are outside what the boundary was designed against, and neither may be guessed.
            return new HardNetworkTopology
            {
                Mode = HardNetworkMode.Mirrored,
                DnsEndpoint = resolver,
                DefaultGateway = string.Empty,
                LocalAddresses = routable,
                Detail = "The environment has routable addresses and no default route of its own, "
                         + "which is how a shared-interface arrangement appears; it is not supported.",
            };
        }

        // A gateway that is one of the environment's own addresses means the environment is not
        // behind anything, which is the same shape by another name.
        if (routable.Contains(gateway, StringComparer.Ordinal))
        {
            return new HardNetworkTopology
            {
                Mode = HardNetworkMode.Mirrored,
                DnsEndpoint = resolver,
                DefaultGateway = gateway,
                LocalAddresses = routable,
                Detail = $"The default route's next hop '{gateway}' is one of the environment's own "
                         + "addresses, so the environment is not behind a gateway.",
            };
        }

        return new HardNetworkTopology
        {
            Mode = HardNetworkMode.Nat,
            DnsEndpoint = resolver,
            DefaultGateway = gateway,
            LocalAddresses = routable,
            Detail = $"The environment holds {string.Join(", ", routable)} behind '{gateway}'.",
        };
    }

    /// <summary>Reads <c>ip -o -4 addr</c> output; the address is the token after "inet".</summary>
    private static List<string> ParseAddresses(IReadOnlyList<string> lines)
    {
        var addresses = new List<string>();

        foreach (var line in lines)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i + 1 < tokens.Length; i++)
            {
                if (!string.Equals(tokens[i], "inet", StringComparison.Ordinal))
                {
                    continue;
                }

                var address = tokens[i + 1].Split('/')[0];

                if (address.Length > 0 && !addresses.Contains(address, StringComparer.Ordinal))
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }

    /// <summary>Reads <c>ip route</c> output; the next hop follows "default via".</summary>
    private static string ParseDefaultGateway(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length >= 3
                && string.Equals(tokens[0], "default", StringComparison.Ordinal)
                && string.Equals(tokens[1], "via", StringComparison.Ordinal))
            {
                return tokens[2];
            }
        }

        return string.Empty;
    }

    /// <summary>Reads a resolver file; the first nameserver is the one the policy allows.</summary>
    private static string ParseResolver(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith("nameserver", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length >= 2 && IsIpv4(tokens[1]))
            {
                return tokens[1];
            }
        }

        return string.Empty;
    }

    /// <summary>Whether a token is a dotted quad. Narrow on purpose: it becomes a policy address.</summary>
    internal static bool IsIpv4(string? candidate) =>
        System.Net.IPAddress.TryParse(candidate, out var parsed)
        && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && candidate!.Count(c => c == '.') == 3;

        /// <summary>The environment's own subnet, for diagnostics that need to name it.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Mode}: {Detail} resolver='{(DnsEndpoint.Length == 0 ? "none" : DnsEndpoint)}'.");
}
