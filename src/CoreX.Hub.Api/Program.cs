using System.Runtime.Versioning;
using CoreX.Hub.Api;

// The local operator API. Loopback only, no authentication, single user - in that order,
// because the first is what makes the second defensible.
//
// Windows only, and said out loud rather than assumed: the Hub drives agents through a Job
// Object based process runner, and a host that started on another platform would fail later
// and less clearly than it fails here.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine(
        "the Hub API runs on Windows: the execution boundary it composes is a Job Object based "
        + "process runner.");

    return 1;
}

await Host.RunAsync(args);
return 0;

/// <summary>
/// The composition this executable performs, in one place a platform attribute can sit on.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Host
{
    /// <summary>
    /// Which loopback port to listen on, for the case where the default one is taken.
    ///
    /// A port setting and nothing more: whatever this says, <see cref="HubApiOptions.Bind"/>
    /// still refuses any address that is not loopback, so this cannot be the thing that puts
    /// an unauthenticated Hub on the network.
    /// </summary>
    internal const string ListenUrlEnvironmentVariable = "COREX_HUB_LISTEN_URL";

    internal static Task RunAsync(string[] args) =>
        HubApi
            .Build(new HubApiOptions { ListenUrl = ListenUrl() }, args)
            .RunAsync();

    private static string ListenUrl()
    {
        var configured = Environment.GetEnvironmentVariable(ListenUrlEnvironmentVariable);

        return string.IsNullOrWhiteSpace(configured) ? HubApiOptions.LoopbackUrl : configured;
    }
}

/// <summary>Named so a test host can reference this assembly.</summary>
public partial class Program;
