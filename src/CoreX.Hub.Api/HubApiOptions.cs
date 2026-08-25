namespace CoreX.Hub.Api;

/// <summary>
/// How this host is allowed to be reached, and where it keeps its state.
///
/// The binding is the part worth reading twice. The Hub drives real agents against real
/// repositories with no authentication of any kind, because it is a single-user local tool -
/// which is a defensible design exactly as long as nothing outside the machine can reach it.
/// So the default is a loopback address and there is no configuration switch here that turns
/// that off. A deployment that wants to be reachable has to say so somewhere this type does
/// not read, and that is deliberate.
/// </summary>
public sealed record HubApiOptions
{
    /// <summary>The only interface this host binds by default. Never 0.0.0.0.</summary>
    public const string LoopbackUrl = "http://127.0.0.1:5177";

    /// <summary>
    /// Which loopback address to bind.
    ///
    /// Overridable so a caller can pick the port - a test host needs an ephemeral one, and an
    /// operator may already have something on the default. What it cannot do is leave
    /// loopback: <see cref="Bind"/> refuses any other host, so this stays a port setting
    /// rather than a way to put the Hub on the network.
    /// </summary>
    public string ListenUrl { get; init; } = LoopbackUrl;

    /// <summary>
    /// The address to bind, checked.
    ///
    /// A wildcard or an external interface is not a configuration mistake to be reported
    /// later - it is the one thing that would make an unauthenticated Hub reachable by
    /// someone else, so it is refused here and the loopback default is used instead.
    /// </summary>
    public string Bind() =>
        Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) && IsLoopback(uri)
            ? ListenUrl
            : throw new ArgumentException(
                $"'{ListenUrl}' is not a loopback address; the Hub API has no authentication and "
                + "binds only to the local machine.");

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(uri.Host, "[::1]", StringComparison.Ordinal);

    /// <summary>Overrides the Hub data directory. Null means the ordinary resolution order.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Overrides the configuration file. Null means the one beside the data directory.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// The Vite dev server, when a developer is running the Phase 9B shell separately.
    ///
    /// Empty by default, which means same-origin only. Any entry here is honoured only in the
    /// Development environment - a production host with a stray origin in configuration still
    /// serves same-origin, because the check is on the environment and not on the list.
    /// </summary>
    public IReadOnlyList<string> DevelopmentCorsOrigins { get; init; } = [];

    /// <summary>
    /// Whether an execution may actually be started by this host.
    ///
    /// True in normal use. Tests that only exercise the query surface turn it off so a
    /// misrouted request can never launch a real agent.
    /// </summary>
    public bool AllowExecutionLaunch { get; init; } = true;
}
