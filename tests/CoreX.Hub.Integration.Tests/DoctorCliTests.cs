using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// <c>hub doctor isolation</c>, driven through the same dispatch the shipped executable uses.
///
/// The command is read only by design, and these tests are written so that a version of it
/// which started provisioning things would fail them: the environments named below do not
/// exist, are never created, and the assertions are about the diagnostic saying so.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DoctorCliTests
{
    /// <summary>
    /// A hard organization pointing at an environment that has never existed.
    ///
    /// The name is deliberately not a real distribution on any machine, so the check that
    /// matters - "no environment of that name is registered" - is the same everywhere.
    /// </summary>
    private const string HardConfig = """
        schemaVersion: 1
        workspace:
          root: 'D:\cx'
          fixtureRoot: 'D:\cx\_fixtures'
        organizations:
          - slug: hard-fixture
            name: Hard Isolation Fixture
            filesystemRoot: 'D:\cx\_fixtures\hard-fixture'
            isolationLevel: Hard
            requiresHardIsolation: true
            hardIsolation:
              backend: Wsl2
              environmentId: corex-environment-that-does-not-exist
              agentUser: corex-agent
              serviceUser: corex-hub
              executionRoot: /srv/corex/exec
              evidenceRoot: /srv/corex/evidence
              processLimit: 64
              memoryLimitBytes: 4294967296
            environmentAllowlist: [PATH, SystemRoot]
            agentProfiles: []
            projects: []
        """;

    /// <summary>The same organization, declaring a boundary and describing none.</summary>
    private const string UndescribedHardConfig = """
        schemaVersion: 1
        workspace:
          root: 'D:\cx'
          fixtureRoot: 'D:\cx\_fixtures'
        organizations:
          - slug: hard-fixture
            name: Hard Isolation Fixture
            filesystemRoot: 'D:\cx\_fixtures\hard-fixture'
            isolationLevel: Hard
            requiresHardIsolation: true
            environmentAllowlist: [PATH, SystemRoot]
            agentProfiles: []
            projects: []
        """;

    [Fact]
    public async Task A_machine_with_no_hard_organization_has_nothing_to_establish()
    {
        using var hub = TempHub.Create();

        var result = await hub.RunAsync("doctor", "isolation");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("nothing to establish", result.StdOut);
    }

    /// <summary>
    /// The exit code an operator scripts against, and the reason they read.
    ///
    /// Four rather than two: nothing was refused here. The command did exactly what it was
    /// asked to do and the answer is that the machine does not provide what was declared.
    /// </summary>
    [Fact]
    public async Task An_environment_that_does_not_exist_is_reported_and_exits_four()
    {
        using var hub = TempHub.Create(HardConfig.ReplaceLineEndings("\n"));

        var result = await hub.RunAsync("doctor", "isolation");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("environment: NOT READY", result.StdOut);
        Assert.Contains("corex-environment-that-does-not-exist", result.StdOut);
        Assert.Contains("executions: refused", result.StdOut);
    }

    /// <summary>Every question is reported, including the ones nobody could answer.</summary>
    [Fact]
    public async Task Every_declared_check_appears_in_the_report()
    {
        using var hub = TempHub.Create(HardConfig.ReplaceLineEndings("\n"));

        var result = await hub.RunAsync("doctor", "isolation");

        Assert.All(HardIsolationChecks.Required, id => Assert.Contains(id, result.StdOut));
    }

    /// <summary>
    /// Declaring a boundary without describing one is its own refusal.
    ///
    /// Reported as a configuration problem rather than as a machine that failed a probe,
    /// because nothing was measured - there was nothing to measure.
    /// </summary>
    [Fact]
    public async Task A_hard_organization_with_no_isolation_block_is_refused_as_unconfigured()
    {
        using var hub = TempHub.Create(UndescribedHardConfig.ReplaceLineEndings("\n"));

        var result = await hub.RunAsync("doctor", "isolation");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains(IsolationRefusal.NotConfigured, result.StdOut);
    }

    [Fact]
    public async Task An_unknown_doctor_subcommand_is_a_usage_error()
    {
        using var hub = TempHub.Create();

        var result = await hub.RunAsync("doctor", "provision");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command 'doctor provision'", result.StdErr);
    }

    /// <summary>
    /// What the probe reports never carries anything worth stealing.
    ///
    /// Scoped to the readiness rows rather than the whole output on purpose. The header names
    /// the data directory and the configuration file, which are the operator's own paths and
    /// are the reason the command is useful at all; what must never appear is anything the
    /// probe learned by looking inside an environment - a host profile path, an account name,
    /// or a credential location.
    /// </summary>
    [Fact]
    public async Task What_the_probe_reports_carries_no_host_identity_or_credential_material()
    {
        using var hub = TempHub.Create(HardConfig.ReplaceLineEndings("\n"));

        var result = await hub.RunAsync("doctor", "isolation");

        var reported = string.Join(
            Environment.NewLine,
            result.StdOut
                .Split('\n')
                .Where(line => HardIsolationChecks.Required.Any(
                    id => line.Contains(id, StringComparison.Ordinal))));

        Assert.NotEmpty(reported);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.DoesNotContain(profile, reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/etc/shadow", reported, StringComparison.Ordinal);
        Assert.DoesNotContain(".credentials", reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", reported, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The command changes nothing it looked at.
    ///
    /// Asserted rather than asserted-about-the-source, because the promise an operator relies on
    /// is behavioural: they run this to find out what is wrong, and it must not quietly become
    /// part of what is wrong. The configuration file is compared byte for byte, and the data
    /// directory is compared against the run before it, so a probe that started writing a cache
    /// or a marker file would fail here.
    /// </summary>
    [Fact]
    public async Task The_command_changes_neither_the_configuration_nor_the_installation()
    {
        using var hub = TempHub.Create(HardConfig.ReplaceLineEndings("\n"));

        // Once first, so the database and its journals already exist: what is being measured is
        // what doctor adds, not what opening the Hub adds.
        await hub.RunAsync("doctor", "isolation");

        var configBefore = await File.ReadAllBytesAsync(hub.ConfigPath);
        var before = Snapshot(hub.DataDirectory);

        await hub.RunAsync("doctor", "isolation");

        Assert.Equal(configBefore, await File.ReadAllBytesAsync(hub.ConfigPath));
        Assert.Equal(before, Snapshot(hub.DataDirectory));
    }

    /// <summary>Every file under a directory, by relative path. Sizes are deliberately ignored:
    /// SQLite rewrites its own journal on any open, and that is not something doctor did.</summary>
    private static IReadOnlyList<string> Snapshot(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)];

    /// <summary>The usage text has to describe the command, or an operator cannot find it.</summary>
    [Fact]
    public void The_usage_text_documents_the_command_and_its_exit_code()
    {
        Assert.Contains("hub doctor isolation", Cli.HubCli.UsageText);
        Assert.Contains("4 hard isolation not established", Cli.HubCli.UsageText);
    }
}
