using System.Globalization;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// Every command the network probe may run inside an environment, and no others.
///
/// A closed surface like the workspace's and the evidence store's. Two of these names could change
/// the machine if they were invoked differently - <c>nft</c> and <c>ip</c> both have mutating
/// forms - so the shapes below are the only ones this file spells, and a test pins each of them.
/// Readiness measures; it does not repair, and a probe that could install its own policy would be
/// a probe whose answer meant nothing.
/// </summary>
public static class HardNetworkCommands
{
    public const string Nft = "/usr/sbin/nft";
    public const string Ip = "/usr/sbin/ip";
    public const string Nc = "/usr/bin/nc";
    public const string Cat = "/usr/bin/cat";
    public const string Printenv = "/usr/bin/printenv";

    /// <summary>The environment's own addresses. <c>-o</c> so one address is one line.</summary>
    public static IReadOnlyList<string> Addresses() => [Ip, "-o", "-4", "addr"];

    /// <summary>The routing table, for the next hop and for nothing else.</summary>
    public static IReadOnlyList<string> Routes() => [Ip, "-4", "route"];

    /// <summary>What the environment believes its resolver is.</summary>
    public static IReadOnlyList<string> Resolver() => [Cat, "/etc/resolv.conf"];

    /// <summary>
    /// The Hub's own table, read back.
    ///
    /// <c>list</c> is the only verb this file knows. Not <c>add</c>, not <c>flush</c>, not
    /// <c>delete</c> - the probe's job is to say what is loaded, and a probe that could load it
    /// would always be able to report success.
    /// </summary>
    public static IReadOnlyList<string> ActivePolicy() =>
        [Nft, "list", "table", NftablesRuleset.TableFamily, NftablesRuleset.TableName];

    /// <summary>Every base chain on the machine, so a foreign one cannot go unnoticed.</summary>
    public static IReadOnlyList<string> ActiveChains() => [Nft, "list", "chains"];

    /// <summary>
    /// One reachability attempt, from inside the environment.
    ///
    /// <c>-w</c> bounds it so an address that drops rather than refuses still answers in finite
    /// time, and the difference between the two is itself evidence: a refusal is a host saying no,
    /// a timeout is something in between deciding for it.
    /// </summary>
    public static IReadOnlyList<string> Reach(string host, int port, int seconds) =>
    [
        Nc,
        "-w",
        seconds.ToString(CultureInfo.InvariantCulture),
        host,
        port.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>What the environment inherited that looks like a proxy.</summary>
    public static IReadOnlyList<string> ProxyEnvironment() => [Printenv];
}
