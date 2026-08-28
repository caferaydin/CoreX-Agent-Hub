using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardEvidence;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Two synthetic hard organizations sharing one environment, each with its own principals.
///
/// The cross-organization tests need four distinct operating system identities, and creating four
/// accounts on the operator's distribution is exactly the kind of persistent mutation this phase
/// forbids. So the fixture uses four system accounts every Debian derived image already has -
/// <c>bin</c>, <c>daemon</c>, <c>sys</c>, <c>games</c> - as stand-ins. They are real uids with
/// real permissions, so the denials below are real operating system denials rather than a model
/// of one; what they are not is a real deployment, and nothing here claims otherwise.
///
/// Everything the fixture creates lives under one uniquely named directory in <c>/var/tmp</c> and
/// is removed at the end. No account, group, mount, <c>wsl.conf</c> or provider home is touched.
///
/// What this cannot prove is cross-<i>environment</i> denial. Both organizations are in one
/// distribution here, so what is established is the storage and ownership model - which is the
/// part that has to be right before separate environments would mean anything. Genuine
/// cross-distribution separation belongs to live acceptance.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardEvidenceFixture : IAsyncLifetime
{
    /// <summary>Organization A: service identity and untrusted agent.</summary>
    public const string ServiceA = "bin";

    public const string AgentA = "daemon";

    /// <summary>Organization B: a different service identity and a different agent.</summary>
    public const string ServiceB = "sys";

    public const string AgentB = "games";

    private string _hostRoot = string.Empty;

    public string EnvironmentId { get; private set; } = string.Empty;

    /// <summary>The unique Linux directory this fixture owns.</summary>
    public string Root { get; } =
        "/var/tmp/corex-hard-evidence-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    public bool Available => EnvironmentId.Length > 0;

    public string EvidenceRootA => Root + "/org-a/evidence";

    public string EvidenceRootB => Root + "/org-b/evidence";

    /// <summary>Synthetic auth homes, owned by each agent, standing in for a provider session.</summary>
    public string AuthHomeA => Root + "/org-a/agent-home";

    public string AuthHomeB => Root + "/org-b/agent-home";

    public string AuthMarkerA => AuthHomeA + "/session.marker";

    public string AuthMarkerB => AuthHomeB + "/session.marker";

    /// <summary>A synthetic marker on the Windows side, standing in for the developer's session.</summary>
    public string HostAuthMarkerWindows { get; private set; } = string.Empty;

    public string HostAuthMarkerLinux { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _hostRoot = Path.Combine(
            Path.GetTempPath(), "corex-hard-evidence", Guid.CreateVersion7().ToString("N"));

        Directory.CreateDirectory(_hostRoot);

        EnvironmentId = await FindEnvironmentAsync();

        if (!Available)
        {
            return;
        }

        // The host side synthetic marker. Placed under the Hub's own temp root rather than in a
        // real provider directory: the question is whether the agent can reach the host's
        // filesystem at all, and a real auth path is not needed to ask it.
        HostAuthMarkerWindows = Path.Combine(_hostRoot, "host-session.marker");
        await File.WriteAllTextAsync(HostAuthMarkerWindows, "COREX-HOST-AUTH-MARKER\n");
        HostAuthMarkerLinux = ToLinuxMountPath(HostAuthMarkerWindows);

        foreach (var (home, agent) in new[] { (AuthHomeA, AgentA), (AuthHomeB, AgentB) })
        {
            await ControlAsync(["/usr/bin/mkdir", "-p", home]);
            await ControlAsync(["/usr/bin/chown", "-R", agent, home]);
            await ControlAsync(["/usr/bin/chmod", "-R", "0700", home]);
        }

        // Each agent writes its own marker, as the provider process would.
        await ControlAsync(["/usr/bin/tee", AuthMarkerA], AgentA, "COREX-AUTH-A\n");
        await ControlAsync(["/usr/bin/tee", AuthMarkerB], AgentB, "COREX-AUTH-B\n");
    }

    public async Task DisposeAsync()
    {
        if (Available)
        {
            await ControlAsync(["/usr/bin/rm", "-rf", Root]);
        }

        try
        {
            if (Directory.Exists(_hostRoot))
            {
                Directory.Delete(_hostRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    public HardIsolationProfile Profile(string service, string agent, string evidenceRoot)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = EnvironmentId,
            AgentUser = agent,
            ServiceUser = service,
            ExecutionRoot = Root + "/exec-" + agent,
            EvidenceRoot = evidenceRoot,
            ProcessLimit = 64,
            MemoryLimitBytes = 512L * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    public HardIsolationProfile ProfileA() => Profile(ServiceA, AgentA, EvidenceRootA);

    public HardIsolationProfile ProfileB() => Profile(ServiceB, AgentB, EvidenceRootB);

    public HardEvidenceGateway GatewayA() => HardEvidenceGateway.Create(ProfileA(), _hostRoot);

    public HardEvidenceGateway GatewayB() => HardEvidenceGateway.Create(ProfileB(), _hostRoot);

    /// <summary>A gateway whose redactor a test supplies, for the failure paths.</summary>
    public HardEvidenceGateway GatewayWithRedactor(
        HardIsolationProfile profile, CoreX.Hub.Infrastructure.Evidence.EvidenceRedactor redactor) =>
        new(HardEvidenceStore.Create(profile, _hostRoot), redactor);

    public HardEvidenceStore StoreA() => HardEvidenceStore.Create(ProfileA(), _hostRoot);

    public HardEvidenceStore StoreB() => HardEvidenceStore.Create(ProfileB(), _hostRoot);

    public HardAgentHome AgentHome(HardIsolationProfile profile) => HardAgentHome.Create(profile, _hostRoot);

    /// <summary>Runs a command in the environment as a named identity.</summary>
    public async Task<(int? ExitCode, string Stdout, string Stderr)> ControlAsync(
        IReadOnlyList<string> arguments,
        string user = "root",
        string? stdin = null)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return (null, string.Empty, string.Empty);
        }

        var result = await new ProcessRunner(FileSystemGuard.Create(_hostRoot)).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["-d", EnvironmentId, "-u", user, "--exec", .. arguments],
                WorkingDirectory = _hostRoot,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                StdinPayload = stdin,
                HardTimeout = TimeSpan.FromMinutes(3),
                MaxCapturedCharactersPerStream = 32 * 1024 * 1024,
            },
            CancellationToken.None);

        return (result.ExitCode, result.NormalizedStdout, result.NormalizedStderr);
    }

    /// <summary>Where a Windows path appears inside the environment, when host drives are mounted.</summary>
    private static string ToLinuxMountPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);

        return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    private async Task<string> FindEnvironmentAsync()
    {
        // Explicit permission first, before this fixture asks the machine anything at all.
        var selected = WslTestEnvironment.Selected();

        if (selected is null)
        {
            return string.Empty;
        }

        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return string.Empty;
        }

        var result = await new ProcessRunner(FileSystemGuard.Create(_hostRoot)).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["--list", "--quiet"],
                WorkingDirectory = _hostRoot,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromSeconds(90),
            },
            CancellationToken.None);

        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        var registered = result.NormalizedStdout
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // The operator named it, so a name that is not there is a typo in the variable rather than
        // an absent machine, and it fails as one. There is deliberately nothing to fall back to.
        if (!WslTestEnvironment.IsRegistered(registered, selected))
        {
            Assert.Fail(
                $"{WslTestEnvironment.Variable} names '{selected}', which is not a registered WSL "
                + "distribution on this machine.");
        }

        return selected;
    }
}
