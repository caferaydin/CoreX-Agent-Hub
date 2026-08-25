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
    internal static Task RunAsync(string[] args) => HubApi.Build(new HubApiOptions(), args).RunAsync();
}

/// <summary>Named so a test host can reference this assembly.</summary>
public partial class Program;
