using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// What a verification step's tool name means for a hard organization, and where the answer is
/// measured.
///
/// The defect these exist for is specific and was structural. The host allowlist did two jobs at
/// once - map a name to a path, then ask the host filesystem whether that path is there - and the
/// second job is a question about a machine. For a hard organization the machine that runs the
/// command is not the machine asking, so the combined answer was wrong in both directions: a real
/// Linux binary refused because Windows cannot see it, and a Windows path accepted and then handed
/// to a Linux workload to execute.
///
/// The environment is scripted and the resolver is real, because every interesting answer here -
/// a missing binary, a directory wearing an executable's name, a symlink into the workspace - is
/// one an operator's own machine cannot be made to produce without damaging it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardVerificationResolutionTests
{
    private const string ExecutionRoot = "/srv/corex/exec";
    private const string EvidenceRoot = "/srv/corex/evidence";

    /// <summary>A. A logical name resolves through this organization's own map.</summary>
    [Fact]
    public async Task A_logical_name_resolves_through_the_organizations_own_map()
    {
        var script = Environment();
        script.Answer("/usr/bin/realpath -m /usr/bin/dotnet", "/usr/bin/dotnet");
        script.Answer("/usr/bin/stat -c %F|%a /usr/bin/dotnet", "regular file|755");

        var resolved = await Resolver(script, ("dotnet", "/usr/bin/dotnet")).ResolveAsync("dotnet", default);

        Assert.True(resolved.IsAllowed);
        Assert.Equal("/usr/bin/dotnet", resolved.AbsolutePath);
    }

    /// <summary>
    /// B. Nothing on the host is consulted.
    ///
    /// The strongest available form of the claim: the map names a path that certainly does not exist
    /// on this machine's C: drive and certainly does inside the scripted environment, and the answer
    /// comes back allowed. A resolver reaching for the host would have to refuse.
    /// </summary>
    [Fact]
    public async Task B_the_host_filesystem_is_never_the_authority()
    {
        var script = Environment();
        script.Answer("/usr/bin/realpath -m /opt/toolchain/bin/node", "/opt/toolchain/bin/node");
        script.Answer("/usr/bin/stat -c %F|%a /opt/toolchain/bin/node", "regular file|755");

        Assert.False(File.Exists("/opt/toolchain/bin/node"));

        var resolved = await Resolver(script, ("node", "/opt/toolchain/bin/node")).ResolveAsync("node", default);

        Assert.True(resolved.IsAllowed);
    }

    /// <summary>C. A name the organization does not map is a refusal, not a host lookup.</summary>
    [Fact]
    public async Task C_a_name_with_no_hard_mapping_fails_closed()
    {
        var resolved = await Resolver(Environment(), ("node", "/usr/bin/node")).ResolveAsync("dotnet", default);

        Assert.False(resolved.IsAllowed);
        Assert.Null(resolved.AbsolutePath);
        Assert.Contains("hard verification executables", resolved.Reason!, StringComparison.Ordinal);
        Assert.Contains("never falls back", resolved.Reason!, StringComparison.Ordinal);
    }

    /// <summary>D. A mapped path that is not there is a refusal before anything launches.</summary>
    [Fact]
    public async Task D_a_configured_path_absent_from_the_guest_fails_closed()
    {
        var script = Environment();
        script.Answer("/usr/bin/realpath -m /usr/bin/dotnet", "/usr/bin/dotnet");
        script.Answer("/usr/bin/stat -c %F|%a /usr/bin/dotnet", string.Empty, exitCode: 1);

        var resolved = await Resolver(script, ("dotnet", "/usr/bin/dotnet")).ResolveAsync("dotnet", default);

        Assert.False(resolved.IsAllowed);
        Assert.Contains("does not exist", resolved.Reason!, StringComparison.Ordinal);
    }

    /// <summary>E. A directory is not a tool, however plausibly it is named.</summary>
    [Fact]
    public async Task E_only_a_regular_executable_file_resolves()
    {
        var script = Environment();
        script.Answer("/usr/bin/realpath -m /usr/bin/dotnet", "/usr/bin/dotnet");
        script.Answer("/usr/bin/stat -c %F|%a /usr/bin/dotnet", "directory|755");

        var directory = await Resolver(script, ("dotnet", "/usr/bin/dotnet")).ResolveAsync("dotnet", default);

        Assert.False(directory.IsAllowed);
        Assert.Contains("not a regular file", directory.Reason!, StringComparison.Ordinal);

        var unreadable = Environment();
        unreadable.Answer("/usr/bin/realpath -m /usr/bin/dotnet", "/usr/bin/dotnet");
        unreadable.Answer("/usr/bin/stat -c %F|%a /usr/bin/dotnet", "regular file|644");

        var notExecutable = await Resolver(unreadable, ("dotnet", "/usr/bin/dotnet")).ResolveAsync("dotnet", default);

        Assert.False(notExecutable.IsAllowed);
        Assert.Contains("not\nexecutable".Replace("\n", " "), notExecutable.Reason!, StringComparison.Ordinal);
    }

    /// <summary>F. A Windows path in the hard map is refused where it is configured.</summary>
    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"\\wsl$\corex\usr\bin\dotnet")]
    [InlineData("dotnet")]
    public void F_a_host_shaped_path_is_refused_by_the_profile(string configured)
    {
        var read = HardIsolationProfile.TryRead(
            Config(("dotnet", configured)), out var profile, out var problem);

        Assert.False(read);
        Assert.Null(profile);
        Assert.Contains("verificationExecutables", problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// G. A repository cannot name an absolute path at all.
    ///
    /// The step declares a key; the trusted map declares the path. There is no third thing, so a
    /// verification profile that wrote a path where a name goes gets exactly what any other unmapped
    /// name gets.
    /// </summary>
    [Theory]
    [InlineData("/usr/bin/dotnet")]
    [InlineData("../../binary")]
    [InlineData("workspace/tool.exe")]
    public async Task G_a_step_cannot_supply_its_own_executable_path(string declared)
    {
        var resolved = await Resolver(Environment(), ("dotnet", "/usr/bin/dotnet"))
            .ResolveAsync(declared, default);

        Assert.False(resolved.IsAllowed);
    }

    /// <summary>
    /// H. What resolves is the physical answer, canonical, and it is what the executor is handed.
    ///
    /// A link is an ordinary thing for a distribution to install, so following one is right; what is
    /// judged afterwards is where it led.
    /// </summary>
    [Fact]
    public async Task H_the_canonical_physical_path_is_what_resolves()
    {
        var script = Environment();
        script.Answer("/usr/bin/realpath -m /usr/bin/dotnet", "/usr/share/dotnet/dotnet");
        script.Answer("/usr/bin/stat -c %F|%a /usr/share/dotnet/dotnet", "regular file|755");

        var resolved = await Resolver(script, ("dotnet", "/usr/bin/dotnet")).ResolveAsync("dotnet", default);

        Assert.True(resolved.IsAllowed);
        Assert.Equal("/usr/share/dotnet/dotnet", resolved.AbsolutePath);
    }

    /// <summary>
    /// H2. A link out of the trusted filesystem is refused rather than followed.
    ///
    /// The case the physical check exists for: every character of the configured path is respectable
    /// and the thing it names is a binary the agent wrote a moment ago.
    /// </summary>
    [Theory]
    [InlineData(ExecutionRoot + "/worktrees/abc/tool")]
    [InlineData(EvidenceRoot + "/tool")]
    [InlineData("/tmp/tool")]
    public async Task H2_a_tool_that_resolves_into_a_writable_surface_is_refused(string physical)
    {
        var script = Environment();
        script.Answer("/usr/bin/realpath -m /usr/local/bin/pytest", physical);
        script.Answer("/usr/bin/stat -c %F|%a " + physical, "regular file|755");

        var resolved = await Resolver(script, ("pytest", "/usr/local/bin/pytest"))
            .ResolveAsync("pytest", default);

        Assert.False(resolved.IsAllowed);
        Assert.Contains("resolves to", resolved.Reason!, StringComparison.Ordinal);
    }

    /// <summary>I. The host resolver still means exactly what it always meant.</summary>
    [Fact]
    public async Task I_logical_resolution_is_unchanged()
    {
        var real = System.Environment.ProcessPath!;

        var host = new CoreX.Hub.Verification.Profiles.HostVerificationExecutableResolver(
            CoreX.Hub.Verification.Profiles.ExecutableAllowlist.Create(
                new Dictionary<string, string> { ["host"] = real }, out _));

        var present = await host.ResolveAsync("host", default);
        Assert.True(present.IsAllowed);
        Assert.Equal(real, present.AbsolutePath);

        var absent = await host.ResolveAsync("missing", default);
        Assert.False(absent.IsAllowed);
    }

    /// <summary>
    /// J. Two hard organizations may answer the same name differently.
    ///
    /// The reason the map is per organization rather than global. Different distributions, different
    /// toolchain versions, different prefixes - one map claiming to speak for both would be wrong
    /// about at least one of them.
    /// </summary>
    [Fact]
    public async Task J_two_organizations_may_map_one_name_to_different_paths()
    {
        var first = Environment();
        first.Answer("/usr/bin/realpath -m /usr/bin/dotnet", "/usr/bin/dotnet");
        first.Answer("/usr/bin/stat -c %F|%a /usr/bin/dotnet", "regular file|755");

        var second = Environment();
        second.Answer("/usr/bin/realpath -m /opt/dotnet/9/dotnet", "/opt/dotnet/9/dotnet");
        second.Answer("/usr/bin/stat -c %F|%a /opt/dotnet/9/dotnet", "regular file|755");

        var a = await Resolver(first, ("dotnet", "/usr/bin/dotnet")).ResolveAsync("dotnet", default);
        var b = await Resolver(second, ("dotnet", "/opt/dotnet/9/dotnet")).ResolveAsync("dotnet", default);

        Assert.True(a.IsAllowed);
        Assert.True(b.IsAllowed);
        Assert.NotEqual(a.AbsolutePath, b.AbsolutePath);
    }

    // -- the bounded report readback ----------------------------------------------------------

    /// <summary>K. A report inside the workspace is read.</summary>
    [Fact]
    public async Task K_a_contained_report_is_read()
    {
        const string workspace = ExecutionRoot + "/verification/result-1";
        const string report = workspace + "/TestResults/report.xml";

        var script = Environment();
        script.Answer("/usr/bin/realpath -m " + report, report);
        script.Answer("/usr/bin/stat -c %F|%a " + report, "regular file|644");
        script.Answer("/usr/bin/stat -c %s " + report, "9");
        script.Answer("/usr/bin/base64 -w 0 " + report, Base64("<r/>ok"));

        var reader = Reader(script);

        var decision = await reader.ResolveUnderRootAsync(workspace, "TestResults/report.xml", true, default);

        Assert.True(decision.IsAllowed);

        var read = await reader.ReadTextAsync(decision.CanonicalPath!, 1_000_000, default);

        Assert.True(read.Succeeded);
        Assert.Equal("<r/>ok", read.Text);
    }

    /// <summary>L, M. A path that leaves the workspace is refused without asking the machine.</summary>
    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(EvidenceRoot + "/report.xml")]
    public async Task LM_a_report_outside_the_workspace_is_refused(string declared)
    {
        var script = Environment();

        var decision = await Reader(script)
            .ResolveUnderRootAsync(ExecutionRoot + "/verification/result-1", declared, true, default);

        Assert.False(decision.IsAllowed);
    }

    /// <summary>
    /// N. A link whose every character is contained and whose target is not.
    ///
    /// The one the textual check cannot catch, and the reason the physical resolution happens at all.
    /// </summary>
    [Fact]
    public async Task N_a_symlink_out_of_the_workspace_is_refused()
    {
        const string workspace = ExecutionRoot + "/verification/result-1";
        const string report = workspace + "/report.xml";

        var script = Environment();
        script.Answer("/usr/bin/realpath -m " + report, EvidenceRoot + "/blobs/secret");
        script.Answer("/usr/bin/stat -c %F|%a " + EvidenceRoot + "/blobs/secret", "regular file|644");

        var decision = await Reader(script).ResolveUnderRootAsync(workspace, "report.xml", true, default);

        Assert.False(decision.IsAllowed);
        Assert.Contains("physically resolves", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>O. A report that is not there is reported as not there.</summary>
    [Fact]
    public async Task O_a_missing_report_is_an_explicit_failure()
    {
        const string workspace = ExecutionRoot + "/verification/result-1";
        const string report = workspace + "/report.xml";

        var script = Environment();
        script.Answer("/usr/bin/realpath -m " + report, report);
        script.Answer("/usr/bin/stat -c %F|%a " + report, string.Empty, exitCode: 1);

        var decision = await Reader(script).ResolveUnderRootAsync(workspace, "report.xml", true, default);

        Assert.False(decision.IsAllowed);
        Assert.Contains("does not exist", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>P, Q. The frozen ceiling, and no truncation on either side of it.</summary>
    [Theory]
    [InlineData(8L * 1024 * 1024, true)]
    [InlineData((8L * 1024 * 1024) + 1, false)]
    public async Task PQ_the_readback_ceiling_is_absolute(long size, bool accepted)
    {
        const string workspace = ExecutionRoot + "/verification/result-1";
        const string report = workspace + "/report.xml";

        var payload = accepted ? "<r/>" : string.Empty;

        var script = Environment();
        script.Answer("/usr/bin/realpath -m " + report, report);
        script.Answer("/usr/bin/stat -c %s " + report, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        script.Answer("/usr/bin/base64 -w 0 " + report, Base64(payload));

        var read = await Reader(script).ReadTextAsync(report, long.MaxValue, default);

        Assert.Equal(accepted, read.Succeeded);

        if (!accepted)
        {
            // Refused whole rather than trimmed. Half a JUnit document parses into a smaller number
            // of tests rather than into an error, and a run that quietly counted fewer tests than
            // ran is the failure this ceiling exists to prevent.
            Assert.Equal(VerificationReadFailure.TooLarge, read.Failure);
            Assert.Null(read.Text);
        }
    }

    /// <summary>R. Everything the hard reader does, it does inside the environment.</summary>
    [Fact]
    public async Task R_the_hard_reader_never_touches_the_host()
    {
        const string workspace = ExecutionRoot + "/verification/result-1";

        var script = Environment();
        script.Answer("/usr/bin/realpath -m " + workspace + "/report.xml", workspace + "/report.xml");
        script.Answer("/usr/bin/stat -c %F|%a " + workspace + "/report.xml", "regular file|644");

        await Reader(script).ResolveUnderRootAsync(workspace, "report.xml", true, default);

        Assert.NotEmpty(script.Calls);
        Assert.All(script.Calls, call => Assert.StartsWith("/usr/bin/", call[0], StringComparison.Ordinal));

        // As the trusted service identity, never as the agent and never as root: reading a report
        // must not become a reason to widen what the workload can reach.
        Assert.All(script.Identities, identity => Assert.Equal("corex-service", identity));
    }

    /// <summary>S. The host reader is untouched by any of this.</summary>
    [Fact]
    public async Task S_the_logical_report_reader_is_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-report-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);

        try
        {
            var file = Path.Combine(root, "report.xml");
            await File.WriteAllTextAsync(file, "<r/>");

            var reader = new CoreX.Hub.Composition.Verification.FileSystemGuardVerificationFileReader();

            var decision = await reader.ResolveUnderRootAsync(root, "report.xml", true, default);
            Assert.True(decision.IsAllowed);

            var read = await reader.ReadTextAsync(decision.CanonicalPath!, 1_000_000, default);
            Assert.True(read.Succeeded);
            Assert.Equal("<r/>", read.Text);

            var escape = await reader.ResolveUnderRootAsync(root, @"..\outside.xml", false, default);
            Assert.False(escape.IsAllowed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // -- fixtures -----------------------------------------------------------------------------

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static ScriptedExecutionEnvironment Environment() => new();

    private static HardIsolationConfig Config(params (string Name, string Path)[] executables) => new()
    {
        Backend = "Wsl2",
        EnvironmentId = "corex-fixture",
        AgentUser = "corex-agent",
        ServiceUser = "corex-service",
        ExecutionRoot = ExecutionRoot,
        EvidenceRoot = EvidenceRoot,
        ProcessLimit = 64,
        MemoryLimitBytes = 1024L * 1024 * 1024,
        VerificationExecutables = executables.ToDictionary(
            e => e.Name, e => e.Path, StringComparer.OrdinalIgnoreCase),
    };

    private static HardIsolationProfile Profile(params (string Name, string Path)[] executables)
    {
        Assert.True(HardIsolationProfile.TryRead(Config(executables), out var profile, out var problem), problem);

        return profile!;
    }

    private static HardVerificationExecutableResolver Resolver(
        ScriptedExecutionEnvironment script,
        params (string Name, string Path)[] executables)
    {
        var profile = Profile(executables);

        return new HardVerificationExecutableResolver(
            new TrustedGuestFileTransfer(script, profile), profile);
    }

    private static HardVerificationFileReader Reader(ScriptedExecutionEnvironment script)
    {
        var profile = Profile();

        return new HardVerificationFileReader(new TrustedGuestFileTransfer(script, profile));
    }
}
