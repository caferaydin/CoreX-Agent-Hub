using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>
/// What an environment's network really does, asked from the identity that matters.
///
/// Every negative answer here is measured from the agent's own account, inside the target
/// environment, through the same mechanism a workload would use. That is the whole point: a
/// configuration file saying the host is unreachable is a statement of intent, and the question is
/// whether the operating system agrees.
///
/// Every negative answer is also paired with a positive one. An environment whose network is
/// simply broken refuses everything, and a probe that reported that as isolation would hand out
/// the strongest possible verdict for the weakest possible reason. So when the permitted control
/// fails, the result is unknown - which is not ready either, and is honest.
///
/// The listeners this opens are the Hub's own, hold a marker and nothing else, and live for the
/// length of one probe. They bind the loopback the Hub itself binds and the host's address on the
/// environment's own subnet - the two places a control plane would be reachable from inside - and
/// deliberately not every interface the machine has, because a diagnostic should not put a port on
/// the operator's network to find out whether it is safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslHardNetworkProbe : IHardNetworkProbe
{
    private const string Marker = "corex-hard-network-probe-marker";

    private const int ReachSeconds = 3;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private readonly IWslExecutionGateway _gateway;
    private readonly string? _unavailable;

    private WslHardNetworkProbe(IWslExecutionGateway gateway, string? unavailable)
    {
        _gateway = gateway;
        _unavailable = unavailable;
    }

    /// <summary>Builds the probe for this installation.</summary>
    public static WslHardNetworkProbe Create(string hostWorkingDirectory) =>
        WslExecutionGateway.TryCreate(hostWorkingDirectory, out var gateway, out var problem)
            ? new WslHardNetworkProbe(gateway!, null)
            : new WslHardNetworkProbe(null!, problem);

    /// <summary>A probe over a scripted environment, for this assembly's tests.</summary>
    internal static WslHardNetworkProbe Scripted(IWslExecutionGateway gateway) => new(gateway, null);

    public async Task<IReadOnlyList<ReadinessCheck>> ProbeAsync(
        HardIsolationProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_unavailable is { } problem)
        {
            return Every($"The environment could not be reached: {problem}");
        }

        var topology = await ReadTopologyAsync(profile, ct);

        var checks = new List<ReadinessCheck> { ModeCheck(topology) };

        checks.Add(await PolicyCheckAsync(profile, topology, ct));

        var reach = await ReachAsync(profile, topology, ct);

        checks.Add(reach.HostLoopback);
        checks.Add(reach.ControlPlane);
        checks.Add(await ProxyCheckAsync(profile, ct));
        checks.Add(PeerCheck());

        return checks;
    }

    /// <summary>Every network answer, unknown, for the one reason that made them all unknown.</summary>
    private static IReadOnlyList<ReadinessCheck> Every(string detail) =>
    [
        ReadinessCheck.Unknown(HardIsolationChecks.NetworkModeSupported, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.NetworkPolicyEnforced, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.HostLoopbackDisabled, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.ControlPlaneUnreachable, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.ProxyNotInherited, detail),
        ReadinessCheck.Unknown(HardIsolationChecks.PeerNetworkUnreachable, detail),
    ];

    private async Task<HardNetworkTopology> ReadTopologyAsync(HardIsolationProfile profile, CancellationToken ct)
    {
        var addresses = await ServiceAsync(profile, HardNetworkCommands.Addresses(), ct);
        var routes = await ServiceAsync(profile, HardNetworkCommands.Routes(), ct);
        var resolver = await ServiceAsync(profile, HardNetworkCommands.Resolver(), ct);

        if (!addresses.Ok || !routes.Ok)
        {
            return HardNetworkTopology.Unknown("The environment did not report its addresses or routes.");
        }

        return HardNetworkTopology.Classify(addresses.Lines, routes.Lines, resolver.Ok ? resolver.Lines : []);
    }

    private static ReadinessCheck ModeCheck(HardNetworkTopology topology) => topology.Mode switch
    {
        HardNetworkMode.Nat => ReadinessCheck.Passed(
            HardIsolationChecks.NetworkModeSupported, topology.Describe()),

        HardNetworkMode.Mirrored => ReadinessCheck.Failed(
            HardIsolationChecks.NetworkModeSupported,
            "The environment shares the host's interfaces, so the control plane is reachable at an "
            + $"address no destination policy denies. {topology.Detail}"),

        _ => ReadinessCheck.Unknown(HardIsolationChecks.NetworkModeSupported, topology.Detail),
    };

    /// <summary>
    /// Whether the table that is loaded is the one this build would write.
    ///
    /// Not "are there rules". A table that exists and says something else is worse than none at
    /// all, because every diagnostic short of this one would report enforcement.
    /// </summary>
    private async Task<ReadinessCheck> PolicyCheckAsync(
        HardIsolationProfile profile,
        HardNetworkTopology topology,
        CancellationToken ct)
    {
        if (!NftablesRuleset.TryRender(HardNetworkPolicy.Phase10, topology, out var expected, out var problem))
        {
            return ReadinessCheck.Unknown(
                HardIsolationChecks.NetworkPolicyEnforced,
                $"The policy could not be rendered for this environment, so nothing can be compared: {problem}");
        }

        var active = await RootAsync(profile, HardNetworkCommands.ActivePolicy(), ct);

        if (!active.Ok)
        {
            // A missing tool and a missing table are both "the policy is not loaded". Neither is
            // repaired here: installing a network policy engine is provisioning, and a diagnostic
            // that provisioned would always be able to report success.
            return ReadinessCheck.Failed(
                HardIsolationChecks.NetworkPolicyEnforced,
                $"The Hub's network policy table '{NftablesRuleset.TableFamily} {NftablesRuleset.TableName}' "
                + "could not be read; it is not loaded, or the environment has no policy engine of its own. "
                + "Providing one is an operator step, not something readiness does.");
        }

        return expected!.Matches(active.Lines)
            ? ReadinessCheck.Passed(
                HardIsolationChecks.NetworkPolicyEnforced,
                $"The loaded policy is the expected one ({expected.Identity[..12]}).")
            : ReadinessCheck.Failed(
                HardIsolationChecks.NetworkPolicyEnforced,
                $"A table named '{NftablesRuleset.TableName}' is loaded but is not the policy this build "
                + $"renders ({expected.Identity[..12]}); it may deny a different set than the Hub believes.");
    }

    /// <summary>Whether the agent inherited anything that would route its traffic through the host.</summary>
    private async Task<ReadinessCheck> ProxyCheckAsync(HardIsolationProfile profile, CancellationToken ct)
    {
        var environment = await AgentAsync(profile, HardNetworkCommands.ProxyEnvironment(), ct);

        if (!environment.Ok)
        {
            return ReadinessCheck.Unknown(
                HardIsolationChecks.ProxyNotInherited,
                "The agent's environment could not be read, so proxy inheritance is unestablished.");
        }

        // Names only. A proxy setting can carry a credential in its value, and a diagnostic that
        // printed one would put it in every log that ever captured this output.
        var inherited = environment.Lines
            .Select(l => l.Split('=', 2)[0].Trim())
            .Where(n => n.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith("_proxy", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();

        return inherited.Count == 0
            ? ReadinessCheck.Passed(
                HardIsolationChecks.ProxyNotInherited,
                "The agent inherited no proxy configuration.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.ProxyNotInherited,
                $"The agent inherited {string.Join(", ", inherited)}; a host proxy is a host channel, "
                + "and one that can carry the developer's credentials.");
    }

    /// <summary>
    /// Peer separation cannot be established from inside one environment.
    ///
    /// Unknown rather than absent, and unknown is not ready. Asserting it from a single environment
    /// would be asserting it from nothing: there is no peer here to fail to reach.
    /// </summary>
    private static ReadinessCheck PeerCheck() => ReadinessCheck.Unknown(
        HardIsolationChecks.PeerNetworkUnreachable,
        "Only one environment is known to this build, so no peer was available to fail to reach; "
        + "this is established by a two-environment acceptance and not by a single machine.");

    private async Task<(ReadinessCheck HostLoopback, ReadinessCheck ControlPlane)> ReachAsync(
        HardIsolationProfile profile,
        HardNetworkTopology topology,
        CancellationToken ct)
    {
        using var loopback = Listener(IPAddress.Loopback);

        using var hostSide = topology.DefaultGateway.Length > 0 && IPAddress.TryParse(topology.DefaultGateway, out var gateway)
            ? Listener(gateway)
            : null;

        if (loopback is null)
        {
            const string Detail = "The Hub could not open its own probe listener, so nothing was asked.";

            return (
                ReadinessCheck.Unknown(HardIsolationChecks.HostLoopbackDisabled, Detail),
                ReadinessCheck.Unknown(HardIsolationChecks.ControlPlaneUnreachable, Detail));
        }

        using var served = new CancellationTokenSource();

        var serving = Task.WhenAll(
            new[] { loopback, hostSide }
                .Where(l => l is not null)
                .Select(l => ServeAsync(l!, served.Token))
                .ToArray());

        try
        {
            var loopbackPort = ((IPEndPoint)loopback.LocalEndpoint).Port;

            var targets = new List<(string Host, int Port, string What)>
            {
                ("127.0.0.1", loopbackPort, "the environment's own loopback"),
                ("localhost", loopbackPort, "the name 'localhost'"),
            };

            if (topology.DefaultGateway.Length > 0)
            {
                targets.Add((topology.DefaultGateway, loopbackPort, "the host's address, on the Hub's loopback port"));

                if (hostSide is not null)
                {
                    targets.Add((
                        topology.DefaultGateway,
                        ((IPEndPoint)hostSide.LocalEndpoint).Port,
                        "the host's address, where the Hub is listening"));
                }
            }

            var reached = new List<string>();

            foreach (var (host, port, what) in targets)
            {
                var attempt = await AgentAsync(
                    profile, HardNetworkCommands.Reach(host, port, ReachSeconds), ct);

                if (attempt.Ok && string.Join('\n', attempt.Lines).Contains(Marker, StringComparison.Ordinal))
                {
                    reached.Add(what);
                }
            }

            var control = await ControlAsync(profile, topology, ct);

            return (
                HostLoopbackCheck(reached, control, hostSide is not null),
                ControlPlaneCheck(reached, control, hostSide is not null));
        }
        finally
        {
            await served.CancelAsync();

            try
            {
                await serving;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private static ReadinessCheck HostLoopbackCheck(
        IReadOnlyList<string> reached,
        bool control,
        bool hostSideBound)
    {
        if (reached.Count > 0)
        {
            // Two different failures wear this check, and calling one by the other's name would
            // send an operator to the wrong setting. Reaching the Hub through the environment's own
            // loopback means the host's loopback is bridged in; reaching it at the host's address
            // means the host is simply reachable, which is a different thing to go and fix.
            var bridged = reached.Any(r => r.Contains("loopback", StringComparison.Ordinal)
                                           || r.Contains("localhost", StringComparison.Ordinal));

            return ReadinessCheck.Failed(
                HardIsolationChecks.HostLoopbackDisabled,
                $"The agent reached a Hub listener through {string.Join("; ", reached)}. "
                + (bridged
                    ? "The host's loopback is bridged into the environment."
                    : "The host itself answers on the environment's subnet, so a service the Hub "
                      + "did not bind to loopback would be reachable."));
        }

        if (!control)
        {
            return ReadinessCheck.Unknown(
                HardIsolationChecks.HostLoopbackDisabled,
                "Nothing was reachable from the environment, including what the policy permits, so a "
                + "silent network cannot be told apart from an enforced boundary.");
        }

        return hostSideBound
            ? ReadinessCheck.Passed(
                HardIsolationChecks.HostLoopbackDisabled,
                "The agent could not reach the Hub at the host's own address or through loopback, "
                + "while permitted traffic still worked.")
            : ReadinessCheck.Unknown(
                HardIsolationChecks.HostLoopbackDisabled,
                "The Hub could not listen on the host's address on this environment's subnet, so the "
                + "one attempt that would have proved the bridge open was never made.");
    }

    private static ReadinessCheck ControlPlaneCheck(
        IReadOnlyList<string> reached,
        bool control,
        bool hostSideBound)
    {
        if (reached.Count > 0)
        {
            return ReadinessCheck.Failed(
                HardIsolationChecks.ControlPlaneUnreachable,
                $"The agent reached a Hub listener through {string.Join("; ", reached)}; an agent that "
                + "can reach the Hub can edit the record of its own run.");
        }

        if (!control)
        {
            return ReadinessCheck.Unknown(
                HardIsolationChecks.ControlPlaneUnreachable,
                "The environment reached nothing at all, permitted traffic included, so this says "
                + "nothing about the control plane.");
        }

        return hostSideBound
            ? ReadinessCheck.Passed(
                HardIsolationChecks.ControlPlaneUnreachable,
                "A Hub-shaped listener on loopback and on the host's address on this subnet was "
                + "unreachable from the agent's own account, while permitted traffic worked.")
            : ReadinessCheck.Unknown(
                HardIsolationChecks.ControlPlaneUnreachable,
                "The Hub could not listen on the host's address on this environment's subnet, so the "
                + "decisive attempt was never made.");
    }

    /// <summary>
    /// The permitted control: the resolver the policy allows, on the port it allows.
    ///
    /// Chosen because it is the allowance itself. If this fails, the environment either has no
    /// network or the policy is denying what it is supposed to permit, and in both cases the
    /// refusals above stop being evidence of anything.
    /// </summary>
    private async Task<bool> ControlAsync(
        HardIsolationProfile profile,
        HardNetworkTopology topology,
        CancellationToken ct)
    {
        if (topology.DnsEndpoint.Length == 0)
        {
            return false;
        }

        var attempt = await AgentAsync(
            profile,
            HardNetworkCommands.Reach(topology.DnsEndpoint, HardNetworkPolicy.Phase10.DnsPort, ReachSeconds),
            ct);

        return attempt.Ok;
    }

    private static TcpListener? Listener(IPAddress address)
    {
        try
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Says the marker to whoever connects, and holds nothing else.</summary>
    private static async Task ServeAsync(TcpListener listener, CancellationToken ct)
    {
        var payload = Encoding.ASCII.GetBytes(Marker + "\n");

        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);

            await client.GetStream().WriteAsync(payload, ct);
            await client.GetStream().FlushAsync(ct);
        }
    }

    private Task<WslCallResult> ServiceAsync(
        HardIsolationProfile profile, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(profile, profile.ServiceUser.Trim(), arguments, ct);

    private Task<WslCallResult> AgentAsync(
        HardIsolationProfile profile, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(profile, profile.AgentUser.Trim(), arguments, ct);

    private Task<WslCallResult> RootAsync(
        HardIsolationProfile profile, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(profile, "root", arguments, ct);

    private Task<WslCallResult> RunAsync(
        HardIsolationProfile profile,
        string user,
        IReadOnlyList<string> arguments,
        CancellationToken ct) =>
        _gateway.RunAsync(
            profile.EnvironmentId,
            new WslCall
            {
                Arguments = arguments,
                LinuxUser = user,
                HardTimeout = Deadline,
                MaxCapturedCharactersPerStream = 256 * 1024,
            },
            ct);

    /// <summary>The rendered policy for one environment, for the operator to read or install.</summary>
    public static bool TryRenderFor(
        HardNetworkTopology topology, out NftablesRuleset? ruleset, out string? problem) =>
        NftablesRuleset.TryRender(HardNetworkPolicy.Phase10, topology, out ruleset, out problem);

    /// <summary>How this probe names itself in a diagnostic.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{nameof(WslHardNetworkProbe)}({_unavailable ?? "ready"})");
}
