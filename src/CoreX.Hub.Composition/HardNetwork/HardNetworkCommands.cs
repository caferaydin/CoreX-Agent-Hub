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

    // -- the trusted plane -----------------------------------------------------------------
    //
    // Everything below changes machine-global state and is called only from the trusted
    // preparation path, never from a diagnostic. The read-only surface above is what readiness
    // uses; a probe that could build what it was measuring would always report success.

    public const string Systemctl = "/usr/bin/systemctl";
    public const string SystemdRun = "/usr/bin/systemd-run";
    public const string Nsenter = "/usr/bin/nsenter";
    public const string Sysctl = "/usr/sbin/sysctl";

    /// <summary>Every interface the trusted plane holds, one per line.</summary>
    public static IReadOnlyList<string> Links() => [Ip, "-o", "link"];

    /// <summary>Starts the unit whose only job is to keep one namespace in existence.</summary>
    public static IReadOnlyList<string> StartHolder(string unit) =>
    [
        SystemdRun, "--quiet", "--unit=" + unit, "--property=PrivateNetwork=yes",
        "--property=Type=exec", "--property=KillMode=control-group", "/usr/bin/sleep", "infinity",
    ];

    /// <summary>What systemd says about a unit, in one field.</summary>
    public static IReadOnlyList<string> UnitProperty(string unit, string property) =>
        [Systemctl, "show", "-p", property, "--value", unit];

    public static IReadOnlyList<string> StopUnit(string unit) => [Systemctl, "stop", unit];

    /// <summary>The kernel identity of a namespace, read through a live holder.</summary>
    public static IReadOnlyList<string> NamespaceIdentity(string reference) => ["/usr/bin/readlink", reference];

    public static IReadOnlyList<string> CreateVeth(string trusted, string inner) =>
        [Ip, "link", "add", trusted, "type", "veth", "peer", "name", inner];

    public static IReadOnlyList<string> MoveIntoNamespace(string inner, string holderPid) =>
        [Ip, "link", "set", inner, "netns", holderPid];

    public static IReadOnlyList<string> AddAddress(string address, string device) =>
        [Ip, "addr", "add", address, "dev", device];

    public static IReadOnlyList<string> BringUp(string device) => [Ip, "link", "set", device, "up"];

    public static IReadOnlyList<string> DeleteLink(string device) => [Ip, "link", "del", device];

    public static IReadOnlyList<string> DefaultRoute(string gateway) =>
        [Ip, "route", "add", "default", "via", gateway];

    /// <summary>Runs one configuration step inside a namespace, reached through its holder.</summary>
    public static IReadOnlyList<string> InNamespace(string holderPid, IReadOnlyList<string> command) =>
        [Nsenter, "-t", holderPid, "-n", .. command];

    /// <summary>Loads the Hub's own table from a file. Never a flush, never another table.</summary>
    public static IReadOnlyList<string> LoadPolicy(string path) => [Nft, "-f", path];

    public static IReadOnlyList<string> DeletePolicy(string family, string table) =>
        [Nft, "delete", "table", family, table];

    public static IReadOnlyList<string> ReadForwarding() => [Cat, "/proc/sys/net/ipv4/ip_forward"];

    public static IReadOnlyList<string> SetForwarding(bool enabled) =>
        [Sysctl, "-w", "net.ipv4.ip_forward=" + (enabled ? "1" : "0")];
}
