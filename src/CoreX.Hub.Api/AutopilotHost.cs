using System.Runtime.Versioning;
using CoreX.Hub.Composition.Autopilot;

namespace CoreX.Hub.Api;

/// <summary>
/// The autopilot's clock, and nothing else (ADR-0028).
///
/// Every decision is the coordinator's; this only asks for a pass every few seconds for as
/// long as the host is up. A pass that throws is logged and the next one runs, because a
/// transient failure - a locked database file, a configuration being saved - must not leave
/// the autopilot silently stopped until the next restart.
///
/// Registered after <see cref="ExecutionLauncher"/>, so on shutdown it stops first: no new
/// work is started while running work is being asked to finish.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutopilotHost : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly AutopilotCoordinator _coordinator;
    private readonly HubApiOptions _options;
    private readonly ILogger<AutopilotHost> _log;

    public AutopilotHost(AutopilotCoordinator coordinator, HubApiOptions options, ILogger<AutopilotHost> log)
    {
        _coordinator = coordinator;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunAutopilot)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        try
        {
            do
            {
                try
                {
                    var pass = await _coordinator.TickAsync(stoppingToken);

                    foreach (var action in pass.Actions)
                    {
                        _log.LogInformation("autopilot: {Action}", action);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "autopilot pass failed; the next pass runs as usual");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Whatever the last pass started belongs to the launcher, which drains it.
        }
    }
}

/// <summary>
/// The autopilot's start, which is the operator's start: the same launcher, the same planner,
/// the same single execution slot, the same refusals. The implementer is chosen the way it is
/// when a person presses Run without naming one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LauncherRunStarter : IAutopilotRunStarter
{
    private readonly ExecutionLauncher _launcher;

    public LauncherRunStarter(ExecutionLauncher launcher) => _launcher = launcher;

    public async Task<Guid> StartAsync(string taskKey, string organizationSlug, CancellationToken ct) =>
        (await _launcher.StartAsync(taskKey, organizationSlug, agentProfile: null, ct)).ExecutionId;
}
