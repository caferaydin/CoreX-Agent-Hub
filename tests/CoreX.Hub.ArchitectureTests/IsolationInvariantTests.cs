using System.Reflection;
using System.Text.RegularExpressions;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

// The assembly under test is held in a field called Domain, which shadows the namespace in an
// expression, so the entity is reached through an alias rather than through that name.
using DomainOrganization = CoreX.Hub.Domain.Catalog.Organization;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Source and assembly level guards for hard isolation.
///
/// The properties below are the ones that would be quiet if they broke. A probe that grew a
/// repair step, a second place that decided whether a hard organization may run, a fallback
/// that ran the work on the host when the environment was missing - none of those would fail a
/// unit test, because each one would still produce a plausible answer. They fail here instead.
/// </summary>
public sealed class IsolationInvariantTests
{
    private const string ProbeFileName = "WslReadinessProbe.cs";
    private const string RunnerFileName = "WslCommandRunner.cs";
    private const string AdmissionFileName = "HardIsolationAdmissionService.cs";
    private const string OrchestratorPersistenceFileName = "ExecutionOrchestrator.Persistence.cs";
    private const string SystemdCommandFileName = "SystemdRunCommand.cs";
    private const string ExecutorFileName = "WslHardProcessExecutor.cs";
    private const string GatewayFileName = "WslExecutionGateway.cs";
    private const string HardCommandsFileName = "HardGitCommands.cs";
    private const string HardWorkspaceFileName = "HardWorkspaceGateway.cs";
    private const string HardEvidenceStoreFileName = "HardEvidenceStore.cs";
    private const string HardEvidenceLayoutFileName = "HardEvidenceLayout.cs";
    private const string HardEvidenceCommandsFileName = "HardEvidenceCommands.cs";
    private const string HardEvidenceGatewayFileName = "HardEvidenceGateway.cs";
    private const string NetworkCommandsFileName = "HardNetworkCommands.cs";
    private const string NetworkProbeFileName = "WslHardNetworkProbe.cs";
    private const string RulesetFileName = "NftablesRuleset.cs";
    private const string FabricFileName = "WslHardNetworkFabric.cs";
    private const string IdentityFileName = "HardNetworkIdentity.cs";
    /// <summary>Spelled through a constant so this file is not itself an offender.</summary>
    private static readonly string HardGitExecutable = "/usr/bin/" + "git";

    /// <summary>Everything under the Application layer's isolation namespace.</summary>
    private static readonly Assembly Application = typeof(HardIsolationReadiness).Assembly;

    private static readonly Assembly Domain = typeof(DomainOrganization).Assembly;

    /// <summary>
    /// The Domain layer never learns that WSL exists.
    ///
    /// Isolation is machine truth and the Domain is not allowed any. A distribution name
    /// reaching an entity would be a fact about this laptop persisted as if it were catalogue
    /// truth, which is the exact shape of the row that outlives what it describes.
    /// </summary>
    [Fact]
    public void The_domain_layer_says_nothing_about_wsl()
    {
        var offenders = ProductionFiles("CoreX.Hub.Domain")
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("wsl", StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_domain_assembly_carries_no_isolation_probe()
    {
        Assert.DoesNotContain(
            Domain.GetTypes(),
            t => t.Name.Contains("Wsl", StringComparison.Ordinal)
                 || t.Name.Contains("Readiness", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only the composition layer knows the mechanism.
    ///
    /// The Application layer names WSL2 in exactly one place - the closed set of backends a
    /// profile may declare - because a configuration file has to be able to say which mechanism
    /// it means. What it must not contain is any part of how that mechanism is driven: an
    /// executable, a distribution switch, a Linux path. Those live one layer down, so replacing
    /// the mechanism never touches the policy.
    /// </summary>
    [Fact]
    public void Only_the_composition_layer_knows_how_to_drive_wsl()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}CoreX.Hub.Composition{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The Application layer's only WSL vocabulary is the name of the backend.
    ///
    /// Asserted rather than assumed, because the enum is exactly the kind of small concession
    /// that quietly grows into a distribution name, then a user name, then a command.
    /// </summary>
    [Fact]
    public void The_application_layer_names_wsl_only_as_a_backend()
    {
        var mentions = ProductionFiles("CoreX.Hub.Application")
            .Where(f => WslWord.IsMatch(CodeOnly(File.ReadAllText(f))))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Equal(["HardIsolationProfile.cs"], mentions);

        Assert.Equal(
            [nameof(HardIsolationBackend.Wsl2)],
            Enum.GetNames<HardIsolationBackend>());
    }

    /// <summary>
    /// The probe goes through the one process boundary like everything else.
    ///
    /// A dedicated launcher for WSL would be a second way to create a child process, outside
    /// the job object, the deadlines and the empty environment - and it would be built by the
    /// component whose entire job is to be suspicious of the thing it is talking to.
    /// </summary>
    [Fact]
    public void The_probe_launches_nothing_of_its_own()
    {
        var runner = SourceOf(RunnerFileName);

        Assert.Contains("new ProcessRunner(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", CodeOnly(runner), StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", CodeOnly(runner), StringComparison.Ordinal);

        var probe = CodeOnly(SourceOf(ProbeFileName));

        Assert.DoesNotContain("ProcessRequest", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe never asks a shell to do anything.
    ///
    /// <c>wsl.exe</c> without <c>--exec</c> hands the rest of the command line to the
    /// distribution's login shell, which puts a shell back into a system that has spent a great
    /// deal of effort not having one - and makes every answer depend on a dotfile the agent can
    /// edit.
    /// </summary>
    [Fact]
    public void Every_command_the_probe_runs_bypasses_the_login_shell()
    {
        var probe = SourceOf(ProbeFileName);

        var invocations = Regex.Matches(probe, @"runner\.RunAsync\(\s*\[(?<args>[^\]]*)\]", RegexOptions.Singleline);

        Assert.NotEmpty(invocations);

        foreach (Match invocation in invocations)
        {
            var args = invocation.Groups["args"].Value;

            // The one invocation that names no distribution is the host side listing, which
            // never enters an environment and therefore has no shell to bypass.
            if (!args.Contains("\"-d\"", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains("\"--exec\"", args, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A diagnostic that repaired what it measured would be a diagnostic nobody could trust.
    ///
    /// These are the switches that change the operator's machine rather than describe it.
    /// Provisioning is a decision an operator makes deliberately, and one made on their behalf
    /// by a command they ran to find out what was wrong is not deliberate.
    /// </summary>
    [Theory]
    [InlineData("--import")]
    [InlineData("--install")]
    [InlineData("--unregister")]
    [InlineData("--set-default")]
    [InlineData("--set-version")]
    [InlineData("--shutdown")]
    [InlineData("--terminate")]
    [InlineData("wsl.conf")]
    public void The_probe_never_changes_the_machine(string verb)
    {
        Assert.DoesNotContain(verb, CodeOnly(SourceOf(ProbeFileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// sudo is asked what the agent may do, and is never asked to do any of it.
    ///
    /// The distinction is the whole reason it is allowed in the set below. <c>-n -l</c> lists
    /// the caller's permissions non-interactively and executes nothing; anything followed by a
    /// command would make a read-only diagnostic the most privileged thing on the machine.
    /// </summary>
    [Fact]
    public void Sudo_is_only_ever_asked_to_list()
    {
        var probe = CodeOnly(SourceOf(ProbeFileName));
        var invocations = Regex.Matches(probe, @"\[Sudo(?<args>[^\]]*)\]", RegexOptions.Singleline);

        Assert.NotEmpty(invocations);

        Assert.All(
            invocations.Select(m => m.Groups["args"].Value.Replace(" ", string.Empty, StringComparison.Ordinal)),
            args => Assert.Equal("," + Quoted("-n") + "," + Quoted("-l"), args));
    }

    /// <summary>
    /// The one unit readiness may start is its own boundary probe, and nothing else.
    ///
    /// This is where the power granted above is bounded. The unit runs under the agent's own
    /// account - never root, never a name written here - carries exactly the shipped boundary
    /// rather than a set of properties spelled locally, and is anonymous, so the probe has no way
    /// to address, adopt or collide with a unit that belongs to something else. A readiness pass
    /// obtained against a weaker sandbox than the one workloads get would be the whole check
    /// lying, which is why the properties come from one place.
    /// </summary>
    [Fact]
    public void Readiness_starts_only_its_own_boundary_probe()
    {
        var probe = CodeOnly(SourceOf(ProbeFileName));

        // One launch site, and it is the boundary probe.
        Assert.Single(
            Regex.Matches(probe, Regex.Escape("SystemdRunCommand.Executable")).Cast<Match>());

        // Under the agent, from the profile.
        Assert.Contains("\"--uid=\" + profile.AgentUser", probe, StringComparison.Ordinal);

        // The shipped boundary, not a local copy of it.
        Assert.Contains(
            "HardMountSandbox.Properties(HardFilesystemPolicy.Phase10)", probe, StringComparison.Ordinal);

        // No property is spelled here, so none can drift from the one that ships.
        Assert.DoesNotContain("--property=Private", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("--property=Inaccessible", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("--property=Protect", probe, StringComparison.Ordinal);

        // Anonymous: it cannot name a unit, so it cannot reach one it did not create.
        Assert.DoesNotContain("--unit=", probe, StringComparison.Ordinal);

        // And it is transient in every direction - it waits for its own answer and unloads.
        foreach (var required in new[] { "\"--wait\"", "\"--collect\"", "\"--quiet\"" })
        {
            Assert.Contains(required, probe, StringComparison.Ordinal);
        }
    }

    private static string Quoted(string value) => "\"" + value + "\"";

    /// <summary>
    /// The probe can only run tools that report.
    ///
    /// Stronger than banning a list of dangerous names, and it stays true for names nobody has
    /// thought of yet: the set of programs the probe is able to name is closed, and every member
    /// of it reports rather than changes. Adding <c>mkdir</c> to make a check pass would have to
    /// be done here first, in a test whose subject is that the probe does not do that.
    ///
    /// <c>sudo</c> is the one member that could run something, which is why the shape it is
    /// invoked with is pinned by its own test above.
    /// </summary>
    [Fact]
    public void The_probe_can_only_name_tools_that_report()
    {
        string[] readOnlyTools =
        [
            "/usr/bin/cat", "/usr/bin/id", "/usr/bin/printenv", "/usr/bin/stat", "/usr/bin/sudo",
            "/usr/bin/systemctl", "/usr/bin/test",
        ];

        var named = GuestExecutable.Matches(CodeOnly(SourceOf(ProbeFileName)))
            .Select(m => m.Value.Trim('"'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(readOnlyTools, named);
    }

    /// <summary>
    /// There is one place that decides, and the execution path is where it is consulted.
    ///
    /// Two places would be two rules, and the second one would be discovered by someone whose
    /// hard organization ran when it should not have.
    /// </summary>
    [Fact]
    public void One_admission_point_consults_the_isolation_authority()
    {
        var callers = RepoLayout.ProductionSourceFiles
            .Where(f => File.ReadAllText(f).Contains(".EvaluateAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Equal([OrchestratorPersistenceFileName], callers);
    }

    /// <summary>
    /// There is one implementation of the rule, and it asks its three questions in order.
    ///
    /// The order is not cosmetic. An operator whose distribution does not exist should be told
    /// that, not told about a neighbour, and a second implementation - however well meant -
    /// would be a second answer to "may this organization run" that nobody compares.
    /// </summary>
    [Fact]
    public void One_service_implements_the_rule()
    {
        var implementations = Application.GetTypes()
            .Where(t => typeof(IIsolationAdmission).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => t.Name)
            .ToList();

        Assert.Equal([nameof(HardIsolationAdmissionService)], implementations);

        var source = SourceOf(AdmissionFileName);

        var readiness = source.IndexOf("_probe.ProbeAsync", StringComparison.Ordinal);
        var topology = source.IndexOf("EvaluateTopologyAsync", StringComparison.Ordinal);
        var plane = source.IndexOf("_plane.IsAvailable", StringComparison.Ordinal);

        Assert.True(readiness > 0 && topology > readiness && plane > topology);
    }

    /// <summary>
    /// A Hub with no authority refuses rather than proceeds.
    ///
    /// Asserted at the source, because the failure it prevents is an omission: an adapter that
    /// forgot to pass the service would compile, run, and quietly stop gating anything.
    /// </summary>
    [Fact]
    public void An_absent_authority_is_a_refusal_rather_than_permission()
    {
        var source = SourceOf(OrchestratorPersistenceFileName);

        Assert.Contains("_isolation is null", source, StringComparison.Ordinal);
        Assert.Contains(nameof(IsolationRefusal.NotConfigured), source, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is no path that runs a hard organization's work somewhere else.
    ///
    /// The single most dangerous thing this design could do is treat a missing environment as a
    /// reason to fall back to the host, because it would look like success to everyone except
    /// the organization that asked for the boundary.
    /// </summary>
    [Theory]
    [InlineData("Fallback")]
    [InlineData("RunOnHost")]
    [InlineData("Downgrade")]
    [InlineData("IsolationLevel.Logical")]
    public void Nothing_offers_to_run_the_work_outside_the_environment(string construct)
    {
        var offenders = IsolationSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A parsed profile cannot be mistaken for a measured machine.
    ///
    /// The two types are the whole slice in miniature, and the way they would quietly merge is
    /// small: a <c>Ready</c> or <c>IsValid</c> property on the profile, and from then on every
    /// caller reading it would be asking the configuration file whether the distribution exists.
    ///
    /// So the profile answers nothing about readiness at all - it has no such member, and no
    /// factory anywhere turns one into the other. Producing a readiness result requires asking
    /// the machine, which is what the probe port is for.
    /// </summary>
    [Fact]
    public void A_configuration_profile_cannot_answer_for_the_machine()
    {
        var profileMembers = typeof(HardIsolationProfile)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(profileMembers, n => n.Contains("Ready", StringComparison.Ordinal));

        // Nothing constructs a readiness result out of a profile; the only ways to get one are
        // to answer the checks or to say nobody could.
        var factories = typeof(HardIsolationReadiness)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(HardIsolationReadiness))
            .ToList();

        Assert.NotEmpty(factories);
        Assert.DoesNotContain(
            factories,
            m => m.GetParameters().Any(p => p.ParameterType == typeof(HardIsolationProfile)));
    }

    /// <summary>
    /// The control plane check cannot be passed from a configuration assumption.
    ///
    /// The question it asks is whether a hard agent can reach the trusted Hub, which has no
    /// multi-user authorisation boundary. Answering it needs a network probe this build does
    /// not have - loopback forwarding, the host gateway and mirrored networking are all routes
    /// that leave no trace in a mount table. So the probe may report the filesystem route it
    /// can see as a failure, and must otherwise say it does not know.
    ///
    /// Written as a source guard because the tempting change is a small one: an environment
    /// with nothing mounted looks isolated, and marking it Pass would make a machine "ready"
    /// on evidence that was never about the network at all.
    /// </summary>
    [Fact]
    public void The_control_plane_check_is_never_passed()
    {
        var probe = CodeOnly(SourceOf(ProbeFileName));

        var passes = Regex.Matches(
            probe,
            @"ReadinessCheck\.Passed\(\s*HardIsolationChecks\.(?<id>\w+)",
            RegexOptions.Singleline);

        Assert.DoesNotContain(
            passes.Select(m => m.Groups["id"].Value),
            id => id == nameof(HardIsolationChecks.ControlPlaneUnreachable));
    }

    /// <summary>
    /// Readiness is never optimistic.
    ///
    /// The type completes an unanswered check as Unknown and treats Unknown as not ready, so
    /// there is no expression anywhere that can turn "we could not tell" into a pass.
    /// </summary>
    [Fact]
    public void An_unknown_check_can_never_be_ready()
    {
        var readiness = HardIsolationReadiness.From(
            "org",
            HardIsolationChecks.Required.Select(id => id == HardIsolationChecks.EnvironmentExists
                ? ReadinessCheck.Unknown(id, "Nobody looked.")
                : ReadinessCheck.Passed(id, "Fine.")));

        Assert.False(readiness.IsReady);
    }

    /// <summary>
    /// Hard isolation configuration is not catalogue truth and is not persisted.
    ///
    /// A stored row claiming an environment exists, read on a machine where it does not, is
    /// precisely the failure the readiness checks exist to catch.
    /// </summary>
    [Theory]
    [InlineData("HardIsolationConfig")]
    [InlineData("HardIsolationProfile")]
    [InlineData("HardIsolationReadiness")]
    public void No_hard_isolation_configuration_reaches_the_database(string type)
    {
        var persistence = ProductionFiles("CoreX.Hub.Infrastructure")
            .Where(f => File.ReadAllText(f).Contains(type, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(persistence);
    }

    /// <summary>
    /// The catalogue entity gained nothing.
    ///
    /// <c>RequiresHardIsolation</c> and <c>IsolationLevel</c> are what an operator declared and
    /// have been persisted since Phase 1. Everything this phase added is about a machine, and a
    /// machine fact stored in a catalogue row is a claim that outlives the thing it describes.
    /// </summary>
    [Fact]
    public void The_organization_entity_gained_no_machine_facts()
    {
        var properties = typeof(DomainOrganization)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.Contains(nameof(DomainOrganization.RequiresHardIsolation), properties);
        Assert.DoesNotContain(properties, p => p.Contains("Environment" + "Id", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, p => p.Contains("HardIsolation", StringComparison.Ordinal) && p != nameof(DomainOrganization.RequiresHardIsolation));
    }

    /// <summary>
    /// Isolation is provider neutral.
    ///
    /// Nothing about a boundary depends on which model or vendor runs inside it, and a provider
    /// name appearing here would mean the two had been tangled together.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    public void Isolation_carries_no_provider_or_credential_vocabulary(string term)
    {
        var offenders = IsolationSourceFiles()
            .Where(f => File.ReadAllText(f).Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Readiness and execution are different authorities with different powers.
    ///
    /// The probe answers questions and changes nothing, and a closed surface is what makes that
    /// checkable. It may not stop, start or reset a unit; teaching it those because execution
    /// happens to need them would hand a component with no business changing anything a general
    /// purpose way to control services on the machine.
    ///
    /// What it may now do is start one transient unit of its own, and only because the question it
    /// has to answer cannot be answered any other way. Whether an untrusted workload can reach a
    /// shared mount is a fact about that workload's own mount namespace: reading the environment's
    /// mount table answers a different question - and answered it wrongly for as long as this
    /// probe did so, because every distribution on the machine has <c>/mnt/wsl</c> and no operator
    /// could ever clear the finding. The narrower powers this needs are pinned by the test below,
    /// and the tool surface it runs inside that unit is still the read only one.
    /// </summary>
    [Theory]
    [InlineData("\"stop\"")]
    [InlineData("\"kill\"")]
    [InlineData("\"start\"")]
    [InlineData("\"restart\"")]
    [InlineData("reset-failed")]
    public void The_readiness_probe_never_learns_to_execute(string construct)
    {
        // systemctl itself is not forbidden here: readiness has to ask whether systemd is running,
        // and `is-system-running` answers that without changing anything. What must never appear
        // is a verb that acts.
        Assert.DoesNotContain(construct, CodeOnly(SourceOf(ProbeFileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the hard execution files may drive systemd.
    ///
    /// One file builds the command and one file issues it; nothing else in the system knows that
    /// systemd exists. A second place that could create or stop a unit would be a second
    /// cancellation authority, and the whole point of deriving unit names from Hub identity is
    /// that there is exactly one.
    /// </summary>
    [Fact]
    public void Only_the_hard_execution_command_can_create_a_unit()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("systemd-run", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Two, and they create different kinds of thing. The execution command creates the unit a
        // task's workload runs in; the network command surface creates the unit whose only job is
        // to hold a namespace open. Neither can create the other's, because the shapes below are
        // pinned and each derives its unit name from Hub identity.
        Assert.Equal([NetworkCommandsFileName, SystemdCommandFileName], offenders);

        var commands = CodeOnly(SourceOf(NetworkCommandsFileName));

        // The holder runs one thing and it is not a workload.
        Assert.Contains("--property=PrivateNetwork=yes", commands, StringComparison.Ordinal);
        Assert.Contains("\"/usr/bin/sleep\", \"infinity\"", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("--uid", commands, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two files name systemctl, and they use it for opposite things.
    ///
    /// The probe asks it a question - is systemd running - and the execution command tells it to
    /// do something. Both are legitimate and the difference matters, which is why the verbs are
    /// guarded separately above rather than the executable being banned outright.
    /// </summary>
    [Fact]
    public void Only_two_files_name_systemctl_at_all()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("systemctl", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Three now. The readiness probe asks whether systemd is running, the execution command
        // tells it to stop a workload's unit, and the network command surface asks about a holder
        // and stops one. The verbs are what matter, so they are pinned rather than the executable
        // being banned outright.
        Assert.Equal([NetworkCommandsFileName, SystemdCommandFileName, ProbeFileName], offenders);

        var verbs = Regex.Matches(CodeOnly(SourceOf(NetworkCommandsFileName)), @"[[]Systemctl, ""(?<verb>[a-z]+)""")
            .Select(m => m.Groups["verb"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["show", "stop"], verbs);
    }

    /// <summary>
    /// The generic process infrastructure stays generic.
    ///
    /// <c>CoreX.Hub.Execution</c> is the Windows process boundary and knows nothing of
    /// distributions, systemd units or Linux accounts. If it did, every future backend would have
    /// to be taught there, and the one place in the system allowed to create a process would
    /// start making policy decisions about which environment it belongs to.
    /// </summary>
    [Theory]
    [InlineData("Wsl")]
    [InlineData("WSL")]
    [InlineData("systemd")]
    [InlineData("cgroup")]
    [InlineData("TasksMax")]
    public void The_process_infrastructure_knows_no_environment(string term)
    {
        var offenders = ProductionFiles("CoreX.Hub.Execution")
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(term, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Both WSL gateways inherit exactly one Windows variable, and it is not negotiable.
    ///
    /// <c>SystemRoot</c> is there because it was measured to be necessary - wsl.exe reaches the
    /// WSL service over RPC and that call fails without it - and nothing else is there because
    /// nothing else has been shown to be. The reason this is a source guard rather than a
    /// behavioural test is that widening the list is nearly invisible today: WSL forwards only
    /// what <c>WSLENV</c> names, so extra variables sit on the host side doing nothing until the
    /// day something sets <c>WSLENV</c>, and then they cross all at once.
    /// </summary>
    [Theory]
    [InlineData(RunnerFileName)]
    [InlineData(GatewayFileName)]
    public void A_wsl_gateway_inherits_exactly_one_windows_variable(string fileName)
    {
        var allowlists = Regex.Matches(
            CodeOnly(SourceOf(fileName)), @"EnvironmentAllowlist\s*=\s*\[(?<names>[^\]]*)\]");

        Assert.NotEmpty(allowlists);
        Assert.All(
            allowlists.Select(m => m.Groups["names"].Value.Trim()),
            names => Assert.Equal("SystemRootVariable", names));
    }

    /// <summary>
    /// Nothing in the isolation work reaches for a shell.
    ///
    /// <c>systemd-run</c> takes an executable and an argument vector exactly as ProcessRunner
    /// does, so there is no point anywhere in this path where a command becomes a string. A shell
    /// here would also be the one that mattered most: it would run inside the boundary, as the
    /// agent, with the workload's own arguments.
    /// </summary>
    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("/bin/bash")]
    [InlineData("/usr/bin/bash")]
    [InlineData("bash -c")]
    [InlineData("sh -c")]
    [InlineData("--shell")]
    public void The_isolation_work_reaches_for_no_shell(string construct)
    {
        var offenders = IsolationSourceFiles()
            .Where(f => File.ReadAllText(f).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The executor provisions nothing either.
    ///
    /// The same rule the probe has, for the same reason: a component that creates the directory
    /// it was asked to work in, or the account it was asked to run as, is reporting on its own
    /// work rather than on the machine - and is doing it with root.
    /// </summary>
    [Theory]
    [InlineData("mkdir")]
    [InlineData("chown")]
    [InlineData("chmod")]
    [InlineData("useradd")]
    [InlineData("usermod")]
    [InlineData("groupadd")]
    [InlineData("wsl.conf")]
    [InlineData("--import")]
    [InlineData("--install")]
    [InlineData("--unregister")]
    public void The_hard_executor_never_provisions_anything(string verb)
    {
        var offenders = IsolationSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(verb, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The executor can only name the tools it needs, and one of them can only list.
    ///
    /// Three executables: the two systemd commands, plus the two read-only tools the
    /// preconditions are established with. The workload's own executable is not in this set
    /// because it arrives in the request - which is the point of the primitive.
    /// </summary>
    [Fact]
    public void The_hard_executor_can_only_name_the_tools_it_needs()
    {
        string[] permitted =
            ["/usr/bin/id", "/usr/bin/stat", "/usr/bin/systemctl", "/usr/bin/systemd-run"];

        var named = IsolationSourceFiles()
            .Where(f => Path.GetFileName(f) is ExecutorFileName or SystemdCommandFileName)
            .SelectMany(f => GuestExecutable.Matches(CodeOnly(File.ReadAllText(f))).Select(m => m.Value.Trim('"')))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(permitted, named);
    }

    /// <summary>
    /// Containment is unconditional: every ceiling is stated on every run.
    ///
    /// A property emitted only under some condition is the shape this would most plausibly take
    /// if it broke - a fast path, a small-run exemption - and the result would be a run that looks
    /// entirely normal and is not contained in the dimension that was skipped.
    /// </summary>
    [Theory]
    [InlineData("TasksMax")]
    [InlineData("MemoryMax")]
    [InlineData("MemorySwapMax")]
    [InlineData("KillMode")]
    [InlineData("RuntimeMaxSec")]
    public void Every_ceiling_is_stated_on_every_run(string property)
    {
        var source = SourceOf(SystemdCommandFileName);

        // One emission each, inside the single list the builder always produces.
        Assert.Single(
            Regex.Matches(CodeOnly(source), @"""--property=" + property + "="),
            m => m.Length > 0);
    }

    /// <summary>
    /// The Windows workspace never learned there was another kind.
    ///
    /// This is the shape the phase brief asked for and the one worth guarding: a separate hard
    /// adapter behind the existing neutral port, rather than an <c>if (hard)</c> threaded through
    /// an implementation that already works. The moment the Windows manager branches on isolation
    /// it becomes responsible for both, and the harder of the two decides how the easier behaves.
    /// </summary>
    [Theory]
    [InlineData("HardIsolation")]
    [InlineData("HardWorkspace")]
    [InlineData("HardProcess")]
    [InlineData("Wsl")]
    [InlineData("systemd")]
    [InlineData("IsolationLevel")]
    public void The_windows_workspace_never_branched_on_isolation(string term)
    {
        var offenders = ProductionFiles("CoreX.Hub.Workspace")
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(term, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The hard workspace is Linux-native, and cannot be talked into a host directory.
    ///
    /// A workspace under a mounted host volume would be a Windows directory wearing a Linux name.
    /// The agent would be editing the host's filesystem and every ownership guarantee below it
    /// would be decoration, so the mount roots appear only in the path authority that refuses
    /// them.
    /// </summary>
    [Theory]
    [InlineData("/mnt/c")]
    [InlineData("/mnt/d")]
    [InlineData("drvfs")]
    [InlineData(@"C:\\")]
    public void The_hard_workspace_never_targets_a_host_path(string construct)
    {
        var offenders = HardWorkspaceSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The source repository is never mounted, cloned over a network, or reached from inside.
    ///
    /// What crosses the boundary is a bundle of committed objects, carried on the control
    /// channel. A mount would give the agent the developer's whole repository; a network verb
    /// would give a hard workspace a way to reach something the Hub did not hand it.
    /// </summary>
    [Theory]
    [InlineData("\"clone\"")]
    [InlineData("\"pull\"")]
    [InlineData("\"push\"")]
    [InlineData("\"remote\"")]
    [InlineData("\"submodule\"")]
    [InlineData("mount")]
    [InlineData("\\\\wsl$")]
    public void The_hard_workspace_performs_no_network_git_and_mounts_nothing(string construct)
    {
        var offenders = HardWorkspaceSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// No command in the hard workspace repairs history.
    ///
    /// A reset, an amend or a force update would each be a way to make an unexpected commit stop
    /// existing - which is precisely the evidence the capture step is for.
    /// </summary>
    [Theory]
    [InlineData("\"reset\"")]
    [InlineData("\"--amend\"")]
    [InlineData("\"rebase\"")]
    [InlineData("\"--force\"")]
    [InlineData("\"-f\", \"--force\"")]
    [InlineData("filter-branch")]
    public void The_hard_workspace_never_repairs_history(string construct)
    {
        var offenders = HardWorkspaceSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The hard workspace can only name the tools it needs.
    ///
    /// A closed surface, for the same reason the process executor has one: a general "run this
    /// Linux command" reachable from application or provider code would make the ownership model,
    /// the hardened configuration and the absence of a shell all advisory.
    /// </summary>
    [Fact]
    public void The_hard_workspace_can_only_name_the_tools_it_needs()
    {
        string[] permitted =
        [
            "/usr/bin/chmod", "/usr/bin/chown", "/usr/bin/env", "/usr/bin/git", "/usr/bin/mkdir",
            "/usr/bin/openssl", "/usr/bin/realpath", "/usr/bin/rm", "/usr/bin/stat", "/usr/bin/touch",
        ];

        var named = HardWorkspaceSourceFiles()
            .SelectMany(f => GuestExecutable.Matches(CodeOnly(File.ReadAllText(f))).Select(m => m.Value.Trim('"')))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(permitted, named);
    }

    /// <summary>
    /// The result commit is the Hub's, and the identity is the one it already had.
    ///
    /// Restated as an assertion rather than a comment: two literal copies of an identity stay
    /// equal only until somebody edits one, and a result commit that is not mechanically
    /// attributable to the Hub is not evidence of anything.
    /// </summary>
    [Fact]
    public void The_hard_result_commit_uses_the_hubs_existing_identity()
    {
        var source = CodeOnly(SourceOf(HardCommandsFileName));

        Assert.Contains("GitEnvironment.HubCommitterName", source, StringComparison.Ordinal);
        Assert.Contains("GitEnvironment.HubCommitterEmail", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreX Agent Hub\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("agent@corex.local\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every git command goes through the hardened channel, including the first one.
    ///
    /// This is the first-checkout guarantee expressed where it can be checked. <c>worktree add</c>
    /// performs a checkout, and a repository that configures its own hooks path gets to execute
    /// code during it - so hardening applied afterwards is hardening applied after the thing it
    /// was meant to prevent. A raw git invocation anywhere in the gateway would be exactly that
    /// mistake, and it would look completely ordinary.
    /// </summary>
    [Fact]
    public void No_git_command_bypasses_the_hardened_channel()
    {
        var gateway = CodeOnly(SourceOf(HardWorkspaceFileName));

        // git is named once, in the command builder. The gateway asks the control channel.
        Assert.DoesNotContain(HardGitExecutable, gateway, StringComparison.Ordinal);
        Assert.Contains("_control.GitAsync", gateway, StringComparison.Ordinal);

        var hardened = Regex.Matches(CodeOnly(SourceOf(HardCommandsFileName)), @"command\.Add\(Git\)");
        Assert.NotEmpty(hardened);
    }

    /// <summary>
    /// The transfer is verified on both sides of the boundary, and both checks stay.
    ///
    /// A source guard rather than a behavioural test, and deliberately so: either check alone
    /// refuses a moved branch, which is exactly what defence in depth means and exactly why no
    /// single test can tell that one of them has been removed. The property worth locking is that
    /// there are two - the host verifying what it packaged, and the environment verifying what it
    /// received, which are different claims about different moments.
    /// </summary>
    [Fact]
    public void The_pinned_commit_is_verified_on_both_sides_of_the_transfer()
    {
        var host = CodeOnly(SourceOf("PinnedTransfer.cs"));

        Assert.Contains("Contains(heads.Stdout, pinnedCommitSha)", host, StringComparison.Ordinal);

        var environment = CodeOnly(SourceOf(HardWorkspaceFileName));

        Assert.Contains("ListTransferHeads", environment, StringComparison.Ordinal);
        Assert.Contains("StartsWith(baseCommit", environment, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktree is built from the pinned commit value, never from a ref that could move.
    ///
    /// The two are the same commit almost always, which is exactly what makes the mistake
    /// invisible: a worktree created from <c>refs/heads/main</c> is correct until the day the
    /// branch moves between the preflight and the checkout, and then it is silently an execution
    /// based on a different commit from the one its provenance records.
    /// </summary>
    [Fact]
    public void The_worktree_is_created_from_the_pinned_commit()
    {
        var gateway = CodeOnly(SourceOf(HardWorkspaceFileName));

        var add = Regex.Match(
            gateway, @"""worktree"",\s*""add""(?<rest>[^;]*);", RegexOptions.Singleline);

        Assert.True(add.Success, "the worktree is not created here any more");

        var arguments = add.Groups["rest"].Value;

        Assert.Contains("baseCommit", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/heads", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultBranch", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent never becomes the git control identity.
    ///
    /// Every git command the hard workspace issues goes through the control channel, which enters
    /// as the service user. A command issued as the agent would be a command the untrusted party
    /// gets to run against the repository it is being measured in.
    /// </summary>
    [Fact]
    public void No_git_command_is_ever_issued_as_the_agent()
    {
        var gateway = CodeOnly(SourceOf(HardWorkspaceFileName));

        Assert.DoesNotContain("AgentUser, ", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain("LinuxUser = ", gateway, StringComparison.Ordinal);

        // The agent user appears only where ownership is handed over, never as an identity to
        // run something as.
        var control = CodeOnly(SourceOf("HardWorkspaceControl.cs"));

        Assert.DoesNotContain("AgentUser", control, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every hard organization's evidence root comes from its own profile, and from nothing else.
    ///
    /// The whole per-organization property rests on this one derivation. A shared constant, a
    /// default, or a root taken from a request would put two organizations in one store - and a
    /// content addressed store shared between two tenants answers "do these bytes already exist"
    /// to whoever asks, which is a question about somebody else's content.
    /// </summary>
    [Fact]
    public void A_hard_evidence_root_comes_only_from_the_organizations_own_profile()
    {
        var store = CodeOnly(SourceOf(HardEvidenceStoreFileName));

        Assert.Contains("HardEvidenceLayout.For(profile.EvidenceRoot)", store, StringComparison.Ordinal);

        // No shared root, no default, no fallback: the layout has exactly one way in.
        var layout = CodeOnly(SourceOf(HardEvidenceLayoutFileName));

        Assert.DoesNotContain("Path.GetTempPath", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("shared", layout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Default", layout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The evidence store is reached by content identity, never by a path a caller supplied.
    ///
    /// There is no traversal to defend against because a path cannot be asked for: the one narrow
    /// point takes sixty-four hexadecimal characters or it throws.
    /// </summary>
    [Fact]
    public void Hard_evidence_is_addressed_by_content_and_never_by_a_caller_path()
    {
        var store = CodeOnly(SourceOf(HardEvidenceStoreFileName));

        Assert.DoesNotContain("string path", store, StringComparison.Ordinal);
        Assert.DoesNotContain("relativePath", store, StringComparison.Ordinal);
        Assert.Contains("HardEvidenceLayout.IsHash", store, StringComparison.Ordinal);

        var layout = CodeOnly(SourceOf(HardEvidenceLayoutFileName));

        Assert.Contains("hash.Length != 64", layout, StringComparison.Ordinal);
        Assert.Contains("Uri.IsHexDigit", layout, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blob is published by a rename that refuses to replace, and by nothing else.
    ///
    /// Publication is the one moment a content addressed name comes into existence. A copy, a
    /// write straight to the final name, or a rename that overwrites would each let a reader see
    /// a blob whose bytes are not the ones its name describes.
    /// </summary>
    [Fact]
    public void A_hard_blob_is_published_only_by_a_non_replacing_rename()
    {
        var commands = CodeOnly(SourceOf(HardEvidenceCommandsFileName));

        Assert.Contains("[Mv, \"-n\", pendingPath, blobPath]", commands, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "\"cp\"", "/usr/bin/cp", "[Mv, \"-f\"", "\"install\"" })
        {
            Assert.DoesNotContain(forbidden, commands, StringComparison.Ordinal);
        }

        // And what was received is hashed before it is given a name.
        var store = CodeOnly(SourceOf(HardEvidenceStoreFileName));

        var hashed = store.IndexOf("HashPending", StringComparison.Ordinal);
        var published = store.IndexOf("Publish(pending", StringComparison.Ordinal);

        Assert.True(hashed > 0 && published > hashed);
    }

    /// <summary>
    /// The Windows Hub never reaches the environment's filesystem to store or read evidence.
    ///
    /// A <c>\\wsl$</c> path or a mounted drive would be a second way to the same bytes, outside
    /// the ownership model and outside the control channel - and it would work, which is what
    /// makes it worth a guard rather than a comment.
    /// </summary>
    [Theory]
    [InlineData("\\\\wsl$")]
    [InlineData("\\\\wsl.localhost")]
    [InlineData("/mnt/c")]
    [InlineData("File.ReadAllBytes")]
    [InlineData("File.WriteAllBytes")]
    [InlineData("FileStream")]
    public void Hard_evidence_never_touches_the_environment_filesystem_directly(string construct)
    {
        var offenders = HardEvidenceSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// There is one secret catalogue, and hard evidence uses it.
    ///
    /// A second list for hard organizations would be a second definition of what a secret is, and
    /// the weaker of the two would decide what got written to disk.
    /// </summary>
    [Fact]
    public void Hard_evidence_redacts_through_the_one_shared_catalogue()
    {
        var gateway = CodeOnly(SourceOf(HardEvidenceGatewayFileName));

        Assert.Contains("EvidenceRedactor", gateway, StringComparison.Ordinal);

        // Redaction happens before the bytes are hashed, so the hash covers what is stored.
        var redacted = gateway.IndexOf("_redactor.Redact", StringComparison.Ordinal);
        var written = gateway.IndexOf("_store.WriteTextAsync", StringComparison.Ordinal);

        Assert.True(redacted > 0 && written > redacted);

        // And no second pattern list anywhere in the hard work.
        var offenders = HardEvidenceSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("Regex", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Nothing in the hard work reads a provider session.
    ///
    /// Auth is agent-owned secret state. The workspace and the evidence store are the trusted
    /// control plane, and a control plane that also read credentials would hold both the record
    /// and the means of producing it.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("ANTHROPIC")]
    [InlineData("OPENAI")]
    [InlineData("credentials.json")]
    [InlineData("auth.json")]
    public void The_hard_control_plane_never_reads_a_provider_session(string construct)
    {
        var offenders = HardEvidenceSourceFiles()
            .Concat(HardWorkspaceSourceFiles())
            .Concat(IsolationSourceFiles())
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The one place the host's own profile is named uses it for a drive letter and nothing else.
    ///
    /// Readiness has to answer whether the developer's session is reachable from inside the
    /// environment, and that question cannot be asked without knowing which drive it is on. What
    /// would turn a reachability check into a disclosure is reading anything there, or carrying a
    /// session path into a report - so the derivation is narrowed to a single character here.
    /// </summary>
    [Fact]
    public void The_host_profile_is_used_only_to_derive_a_drive_letter()
    {
        var probe = CodeOnly(SourceOf(ProbeFileName));

        Assert.Matches(
            @"DriveLetterOf\(\s*Environment\.GetFolderPath\(Environment\.SpecialFolder\.UserProfile\)\)",
            probe);

        // One mention, and it is that one.
        Assert.Single(Regex.Matches(probe, "SpecialFolder"));

        // Ordinal for the framework calls, because "profile." contains "file." when case is
        // ignored and the guard would then be answering a different question.
        foreach (var forbidden in new[] { "Directory.", "File." })
        {
            Assert.DoesNotContain(forbidden, probe, StringComparison.Ordinal);
        }

        foreach (var provider in new[] { ".claude", "codex", "anthropic", "openai" })
        {
            Assert.DoesNotContain(provider, probe, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The hard store imposes no size ceiling of its own.
    ///
    /// Isolation must not decide what counts as evidence. A ceiling here that the Windows store
    /// does not have would mean the same execution kept a different record depending on which side
    /// of the boundary it ran on - and silently, because the port returns null for "nothing to
    /// store" and the orchestrator reads that as an artifact that never existed.
    ///
    /// What is bounded is the channel. The window is a transport number and the capture bound is
    /// measured after base64 expands it; neither is ever compared against a raw artifact.
    /// </summary>
    [Fact]
    public void Hard_evidence_imposes_no_storage_ceiling_of_its_own()
    {
        var store = CodeOnly(SourceOf(HardEvidenceStoreFileName));

        foreach (var ceiling in new[]
                 {
                     "MaximumBlobBytes", "MaxBlobBytes", "MaximumEvidenceBytes",
                     "bytes.LongLength >", "bytes.Length >", "text.Length >",
                 })
        {
            Assert.DoesNotContain(ceiling, store, StringComparison.Ordinal);
        }

        // The capture bound comes from the encoded window, never from the artifact.
        Assert.Contains(
            "MaxCapturedCharactersPerStream = EncodedChunkCharacters", store, StringComparison.Ordinal);

        // And the artifact crosses in windows rather than in one call.
        Assert.Contains("TransferChunkBytes", store, StringComparison.Ordinal);
        Assert.Contains("HardEvidenceCommands.AppendPending(", store, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent is never made the owner of anything in the evidence tree.
    ///
    /// Ownership is the whole boundary, so this is the single line that would undo it. Evidence is
    /// what a human decision is made from, and a party that can read or alter the record of its own
    /// run is a party whose record means nothing - which is why the agent has no access even to its
    /// own execution's evidence.
    /// </summary>
    [Fact]
    public void The_agent_is_never_an_owner_inside_the_evidence_tree()
    {
        var store = CodeOnly(SourceOf(HardEvidenceStoreFileName));

        Assert.DoesNotContain("AgentUser", store, StringComparison.Ordinal);

        // The one chown names the service identity, and the mode admits nobody else.
        Assert.Contains("Own(_profile.ServiceUser.Trim()", store, StringComparison.Ordinal);
        Assert.Contains("HardEvidenceCommands.Close(", store, StringComparison.Ordinal);

        var commands = CodeOnly(SourceOf(HardEvidenceCommandsFileName));

        Assert.Contains("[Chmod, \"-R\", \"0700\", path]", commands, StringComparison.Ordinal);

        foreach (var loosened in new[] { "0770", "0750", "0755", "0777", "g+r", "o+r", "a+r" })
        {
            Assert.DoesNotContain(loosened, commands, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// No hard organization is given a group that could span more than one of them.
    ///
    /// A single service group across every hard root would read like tidy administration and would
    /// quietly make every organization's evidence readable to every other - the exact property the
    /// per-organization store exists to prevent.
    /// </summary>
    [Fact]
    public void No_group_is_ever_granted_across_hard_roots()
    {
        var offenders = HardEvidenceSourceFiles()
            .Concat(HardWorkspaceSourceFiles())
            .Where(f => CodeOnly(File.ReadAllText(f)) is var code
                        && (code.Contains("groupadd", StringComparison.Ordinal)
                            || code.Contains("usermod", StringComparison.Ordinal)
                            || code.Contains("chgrp", StringComparison.Ordinal)))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The one place HOME is set points it away from a session rather than at one.
    ///
    /// git reads a global configuration from HOME, and an unset HOME inside the environment would
    /// resolve to whatever the passwd database says - a directory the agent may own. Pinning it to
    /// the service's control root means the transfer's configuration is the Hub's and nobody
    /// else's, which is the reverse of carrying a session in.
    /// </summary>
    [Fact]
    public void Home_is_pinned_to_the_control_root_rather_than_to_a_session()
    {
        var commands = CodeOnly(SourceOf(HardCommandsFileName));

        Assert.Contains("\"HOME=\" + layout.ControlRoot", commands, StringComparison.Ordinal);

        // And nowhere else in the hard adapters is HOME named at all.
        var offenders = HardEvidenceSourceFiles()
            .Concat(HardWorkspaceSourceFiles())
            .Where(f => !f.EndsWith(HardCommandsFileName, StringComparison.Ordinal))
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("HOME", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Nothing carries the host's own session into the environment.
    ///
    /// Copying the developer's credentials in would make every isolation property above cosmetic:
    /// the boundary would hold and the thing it was built to contain would already be inside it.
    /// </summary>
    [Theory]
    [InlineData("USERPROFILE")]
    [InlineData("SpecialFolder")]
    [InlineData("\"cp\"")]
    [InlineData("rsync")]
    [InlineData("scp")]
    public void The_hosts_own_session_is_never_carried_into_the_environment(string construct)
    {
        var offenders = HardEvidenceSourceFiles()
            .Concat(HardWorkspaceSourceFiles())
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The isolation profile has no place to put a provider's session.
    ///
    /// Asserted on the type rather than on the source, so a field cannot be added under a
    /// different spelling. Isolation supplies identities and boundaries; where a provider keeps
    /// its state inside them belongs to that adapter.
    /// </summary>
    [Fact]
    public void The_isolation_profile_stays_provider_neutral()
    {
        Assert.Equal(
            [
                "AgentUser", "Backend", "EnvironmentId", "EvidenceRoot", "ExecutionRoot",
                "MemoryLimitBytes", "ProcessLimit", "ServiceUser",
            ],
            typeof(HardIsolationProfile)
                .GetProperties()
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The network mechanism lives in one place, and it is not above the composition layer.
    ///
    /// The policy is a set of effects - what may be reached, what may not, and why each range is
    /// denied. The mechanism that enforces those effects is a detail of one platform. Keeping the
    /// two apart is what lets the mechanism be replaced without anybody re-deciding what a hard
    /// workload is allowed to reach.
    /// </summary>
    [Theory]
    [InlineData("nft")]
    [InlineData("nftables")]
    [InlineData("iptables")]
    [InlineData("netns")]
    [InlineData("daddr")]
    public void Only_the_composition_layer_knows_how_the_network_is_filtered(string construct)
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}CoreX.Hub.Composition{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The policy names capabilities, never a service.
    ///
    /// A hostname allowlist would make isolation provider-shaped, and it would be weaker than it
    /// looks: the name it filtered on is one the workload itself supplies. So the allowance is a
    /// port and a destination class, and which service a workload then talks to is not isolation's
    /// business.
    /// </summary>
    [Fact]
    public void The_network_policy_names_no_service()
    {
        var policy = File.ReadAllText(
            RepoLayout.ProductionSourceFiles.Single(f => f.EndsWith("HardNetworkPolicy.cs", StringComparison.Ordinal)));

        foreach (var named in new[]
                 {
                     "anthropic", "openai", "claude", "codex", ".com", ".ai", "http://", "https://",
                 })
        {
            Assert.DoesNotContain(named, policy, StringComparison.OrdinalIgnoreCase);
        }

        // Allowances are ports; denials are address ranges. Both are stated, neither is a name.
        Assert.Equal(443, HardNetworkPolicy.Phase10.PublicHttpsPort);
        Assert.Equal(53, HardNetworkPolicy.Phase10.DnsPort);
        Assert.All(HardNetworkPolicy.Phase10.DeniedDestinations, d => Assert.Contains('/', d.Cidr));
        Assert.All(HardNetworkPolicy.Phase10.DeniedDestinations, d => Assert.NotEmpty(d.Reason));
    }

    /// <summary>
    /// The policy is product behaviour, not configuration.
    ///
    /// An organization that could widen its own boundary through configuration would be an
    /// organization whose boundary means whatever its configuration file says.
    /// </summary>
    [Fact]
    public void The_network_policy_cannot_be_widened_by_configuration()
    {
        Assert.Empty(
            typeof(HardNetworkPolicy).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        Assert.DoesNotContain(typeof(HardNetworkPolicy).GetProperties(), p => p.SetMethod is not null);

        // And nothing in configuration can name one.
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Configuration{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Network", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The Hub owns its own tables and never the machine's ruleset.
    ///
    /// An operator's rules are theirs, and on this platform they are literally the same ruleset -
    /// every environment shares one. A boundary that began by flushing it would take the
    /// developer's own network with it, and the authority does not need that: in this mechanism a
    /// drop in one base chain is final whatever another chain accepted.
    /// </summary>
    [Fact]
    public void The_hub_owns_only_its_own_tables()
    {
        var ruleset = CodeOnly(SourceOf(RulesetFileName));

        Assert.Contains("TableName = \"corex_hard\"", ruleset, StringComparison.Ordinal);
        Assert.Contains("NatTableName = \"corex_hard_nat\"", ruleset, StringComparison.Ordinal);

        foreach (var wholesale in new[] { "flush ruleset", "flush table", "list ruleset" })
        {
            Assert.DoesNotContain(wholesale, ruleset, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The invalid per-distribution model is gone rather than left behind.
    ///
    /// It meant "this environment has a root policy of its own", which was measured false: every
    /// environment shares one network namespace and one ruleset. A readiness identity that cannot
    /// be true should not be answerable, and nothing may reintroduce it under the old name.
    /// </summary>
    [Fact]
    public void The_per_distribution_policy_model_is_absent()
    {
        Assert.DoesNotContain("network.policy.enforced", HardIsolationChecks.Required);

        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("NetworkPolicyEnforced", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);

        // The three truths that replaced it are all required, and all distinct.
        foreach (var id in new[]
                 {
                     HardIsolationChecks.NetworkFabricReady,
                     HardIsolationChecks.OrgNetworkNamespaceReady,
                     HardIsolationChecks.OrgNetworkAttachmentReady,
                 })
        {
            Assert.Contains(id, HardIsolationChecks.Required);
        }
    }

    /// <summary>
    /// An organization is classified by the interface its traffic arrives on, never by its address.
    ///
    /// An address is configuration inside a namespace and a workload's neighbours are known to it.
    /// An ingress interface is a fact about where a packet crossed into the trusted plane, and it
    /// is the only one of the two a workload cannot forge.
    /// </summary>
    [Fact]
    public void Organization_identity_is_the_interface_and_never_the_source_address()
    {
        var ruleset = CodeOnly(SourceOf(RulesetFileName));

        Assert.Contains("iifname", ruleset, StringComparison.Ordinal);
        Assert.DoesNotContain("saddr", ruleset.Replace("ip saddr {HardNetworkIdentity.Pool}", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // The one source match that exists is the translation's own pool, which is not a
        // classification: it decides what gets translated, not who anybody is.
        Assert.Contains("ip saddr {HardNetworkIdentity.Pool}", ruleset, StringComparison.Ordinal);
    }

    /// <summary>
    /// No chain the Hub loads denies by default, and every Hub attachment ends in a denial anyway.
    ///
    /// Both halves are load bearing and they pull in opposite directions. The chains sit in the
    /// namespace the operator's own environment uses, so a default drop would take the developer's
    /// network down - and a permissive default means an attachment nobody named is unfiltered,
    /// which is why the final per-interface drop is not optional.
    /// </summary>
    [Fact]
    public void No_chain_denies_by_default_and_every_attachment_still_ends_in_a_denial()
    {
        var ruleset = CodeOnly(SourceOf(RulesetFileName));

        Assert.DoesNotContain("policy drop", ruleset, StringComparison.Ordinal);
        Assert.Contains("policy accept;", ruleset, StringComparison.Ordinal);

        // One closing drop per interface in the forward chain and one in the input chain.
        Assert.Equal(2, Regex.Matches(ruleset, Regex.Escape("{iif}") + @"..? drop").Count);
    }

    /// <summary>
    /// The fabric is ready only when what is attached and what is classified are the same set.
    ///
    /// The measured failure this exists for: the policy survives a restart that destroys every
    /// namespace and interface it names, so a table can list exactly the expected rules while
    /// filtering nothing. Both directions have to be checked, because the other one - an interface
    /// present with no rule naming it - is an unfiltered attachment.
    /// </summary>
    [Fact]
    public void Fabric_readiness_compares_the_attached_set_against_the_classified_set()
    {
        var fabric = CodeOnly(SourceOf(FabricFileName));

        Assert.Contains("named.Except(present", fabric, StringComparison.Ordinal);
        Assert.Contains("present.Except(named", fabric, StringComparison.Ordinal);

        // And a table alone is never enough to pass.
        var pass = fabric.LastIndexOf("HardIsolationChecks.NetworkFabricReady", StringComparison.Ordinal);
        var compared = fabric.IndexOf("uncovered.Count > 0", StringComparison.Ordinal);

        Assert.True(compared > 0 && pass > compared);
    }

    /// <summary>
    /// A workload's namespace is verified by identity, not by the launch having succeeded.
    ///
    /// Measured on this platform: a launch mechanism silently substituted a fresh empty namespace
    /// when the intended one was missing. "The unit started" is evidence about the unit, not about
    /// where it started.
    /// </summary>
    [Fact]
    public void Attachment_readiness_compares_a_namespace_identity()
    {
        var fabric = CodeOnly(SourceOf(FabricFileName));

        Assert.Contains("holder.Identity", fabric, StringComparison.Ordinal);
        Assert.Contains("NetworkNamespacePath=", fabric, StringComparison.Ordinal);

        // The mechanism that was measured to fall back is not the one used.
        Assert.DoesNotContain("JoinsNamespaceOf", fabric, StringComparison.Ordinal);
    }

    /// <summary>
    /// Machine-global state is written under a machine-wide lock, not a lock inside one process.
    ///
    /// The CLI and the API are separate processes, and what they would race over is the machine's:
    /// interfaces on a shared namespace, one ruleset, and a kernel flag.
    /// </summary>
    [Fact]
    public void Global_plane_mutation_is_guarded_by_a_machine_wide_lock()
    {
        var fabric = CodeOnly(SourceOf(FabricFileName));

        Assert.Equal(2, Regex.Matches(fabric, @"MutationLock\.Acquire").Count);

        var lockSource = CodeOnly(SourceOf("MutationLock.cs"));

        Assert.Contains("new Mutex(", lockSource, StringComparison.Ordinal);
        Assert.Contains("AbandonedMutexException", lockSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SemaphoreSlim", lockSource, StringComparison.Ordinal);

        // Preparation and release mutate; verification does not.
        var verify = fabric.IndexOf("public async Task<IReadOnlyList<ReadinessCheck>> VerifyAsync", StringComparison.Ordinal);
        var release = fabric.IndexOf("public async Task ReleaseAsync", StringComparison.Ordinal);

        Assert.True(verify > 0 && release > verify);
        Assert.DoesNotContain("MutationLock", fabric[verify..release], StringComparison.Ordinal);
    }

    /// <summary>
    /// A global value the Hub did not set is not the Hub's to reset.
    ///
    /// Forwarding belongs to the machine. Turning it on when it was off is a change this build made
    /// and should undo; finding it already on is somebody else's decision, and resetting it would
    /// be this build reconfiguring an operator's machine on the way out.
    /// </summary>
    [Fact]
    public void Global_forwarding_is_restored_only_when_the_hub_enabled_it()
    {
        var fabric = CodeOnly(SourceOf(FabricFileName));

        Assert.Contains("File.Exists(ForwardingMarker)", fabric, StringComparison.Ordinal);

        // The note lives on the Hub's side, because environments do not share a filesystem and a
        // note written inside one would be invisible to the next.
        Assert.Contains("Path.Combine(_hostDirectory", fabric, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/corex-hard-forwarding", fabric, StringComparison.Ordinal);
    }

    /// <summary>
    /// Translation covers the Hub's own pool and nothing else on the shared plane.
    ///
    /// A blanket source translation would silently rewrite the developer's own traffic, and would
    /// look identical in every diagnostic short of reading the rule.
    /// </summary>
    [Fact]
    public void Translation_is_scoped_to_the_hubs_own_pool()
    {
        var ruleset = CodeOnly(SourceOf(RulesetFileName));

        Assert.Contains("ip saddr {HardNetworkIdentity.Pool} oifname", ruleset, StringComparison.Ordinal);

        // One translation rule, and it names the Hub's pool.
        Assert.Single(Regex.Matches(ruleset, "masquerade"));
    }

    /// <summary>
    /// Attachment identity is derived from the organization and nothing a caller supplies.
    ///
    /// The attachment is the boundary. One a task, a provider or a repository could name would be a
    /// boundary those things could choose, and choosing it is most of leaving it.
    /// </summary>
    [Fact]
    public void Attachment_identity_is_derived_from_the_organization()
    {
        var identity = CodeOnly(SourceOf(IdentityFileName));

        Assert.Contains("SHA256.HashData", identity, StringComparison.Ordinal);

        foreach (var supplied in new[] { "Task", "objective", "Provider", "Repository", "config" })
        {
            Assert.DoesNotContain(supplied, identity, StringComparison.OrdinalIgnoreCase);
        }

        // Collisions are refused rather than reallocated around.
        Assert.Contains("Collides", identity, StringComparison.Ordinal);
        Assert.Contains("IsNameable", identity, StringComparison.Ordinal);
    }

    /// <summary>
    /// The provider facing process contract learned nothing about the fabric.
    ///
    /// Which namespace a workload runs in is decided from the organization by composition. A field
    /// for it on the request would be a field a provider could set.
    /// </summary>
    [Theory]
    [InlineData("NetworkNamespace")]
    [InlineData("Veth")]
    [InlineData("nftables")]
    [InlineData("Attachment")]
    public void The_process_contract_knows_nothing_about_the_fabric(string construct)
    {
        var process = CodeOnly(SourceOf("HardProcess.cs"));

        Assert.DoesNotContain(construct, process, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Preparation is a trusted step and readiness is not.
    ///
    /// A diagnostic that built what it was measuring could always report success. So the read-only
    /// surface never creates a link, a namespace or a policy, and the one place that does is not
    /// reachable from a probe.
    /// </summary>
    [Fact]
    public void Readiness_measures_the_plane_and_never_builds_it()
    {
        var probe = CodeOnly(SourceOf(NetworkProbeFileName));

        foreach (var mutation in new[] { "StartHolder", "CreateVeth", "LoadPolicy", "SetForwarding", "PrepareAsync" })
        {
            Assert.DoesNotContain(mutation, probe, StringComparison.Ordinal);
        }

        var commands = CodeOnly(SourceOf(NetworkCommandsFileName));

        // And nothing anywhere near the plane can install what it is measuring.
        var provisioners = HardNetworkSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)) is var code
                        && new[] { "apt-get", "apt install", "/dnf", "/yum", "pacman", "zypper", "\"install\"" }
                            .Any(m => code.Contains(m, StringComparison.Ordinal)))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(provisioners);

        var verbs = Regex.Matches(commands, @"[[]Nft, ""(?<verb>[a-z]+)""")
            .Select(m => m.Groups["verb"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Three verbs and no more: list to measure, and the pair that replaces the Hub's own table
        // when the plane is built or given up. Never a flush, and never anybody else's table.
        Assert.Equal(["delete", "list"], verbs);
        Assert.DoesNotContain("flush", commands, StringComparison.Ordinal);
    }

    /// <summary>
    /// The environment applies the policy; the workload never does.
    ///
    /// A workload that installed its own boundary would be a workload that chose it. Nothing in
    /// this work runs a filtering command as the agent, and the one read of the loaded table is
    /// made as root.
    /// </summary>
    [Fact]
    public void The_agent_never_applies_or_reads_the_policy()
    {
        var probe = CodeOnly(SourceOf(NetworkProbeFileName));

        // The table is read as root; the agent is only ever asked to try to reach things.
        // The loaded table is read by the fabric as the service identity; the probe never reads it.
        Assert.DoesNotContain("ActivePolicy", probe, StringComparison.Ordinal);

        Assert.Contains(
            "ServiceAsync(profile, HardNetworkCommands.ActivePolicy()",
            CodeOnly(SourceOf(FabricFileName)),
            StringComparison.Ordinal);

        foreach (Match agentCall in Regex.Matches(probe, @"AgentAsync\(\s*profile,\s*HardNetworkCommands\.(?<c>\\w+)"))
        {
            Assert.Contains(agentCall.Groups["c"].Value, new[] { "Reach", "ProxyEnvironment" });
        }
    }

    /// <summary>
    /// A working network policy does not open hard execution.
    ///
    /// The boundary is one of several a whole task needs, and the peer question cannot even be
    /// asked from a single environment. Admitting an organization because its egress is filtered
    /// would put the rest of the work outside the boundary with nothing saying so.
    /// </summary>
    [Fact]
    public void The_network_policy_does_not_make_hard_execution_available()
    {
        Assert.False(new UnavailableHardExecutionPlane().IsAvailable);

        var composition = CodeOnly(SourceOf("IsolationComposition.cs"));

        Assert.DoesNotContain("HardNetwork", composition, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Hub reads the machine's network configuration and never writes it.
    ///
    /// Self-healing a topology would make readiness a statement about what the Hub had just done
    /// rather than about what the machine is. Drift has to be reported, not repaired.
    /// </summary>
    [Theory]
    [InlineData(".wslconfig")]
    [InlineData("wsl.conf")]
    [InlineData("networkingMode")]
    [InlineData("hostAddressLoopback")]
    [InlineData("New-NetFirewall")]
    [InlineData("Set-NetFirewall")]
    public void The_hub_never_writes_a_network_configuration(string construct)
    {
        // Naming one is allowed and useful - the operator has to be told what to look at. What no
        // file may do is name one and also hold a way to write a file, which is the shape a quiet
        // self-heal would take.
        string[] writes =
        [
            "File.WriteAllText", "File.WriteAllBytes", "File.AppendAll", "StreamWriter",
            "File.Create", "Set-Content", "Out-File",
        ];

        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => File.ReadAllText(f) is var text
                        && text.Contains(construct, StringComparison.OrdinalIgnoreCase)
                        && writes.Any(w => text.Contains(w, StringComparison.Ordinal)))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// No Hub API authentication was introduced, and none is treated as a substitute.
    ///
    /// The boundary this slice builds is an operating system boundary. An authentication layer
    /// would be a second, independent defence and is deliberately not this slice's work - and it
    /// would not have replaced the first one if it were.
    /// </summary>
    [Theory]
    [InlineData("Negotiate")]
    [InlineData("JwtBearer")]
    [InlineData("AddAuthentication")]
    [InlineData("ApiKey")]
    [InlineData("ClientCertificate")]
    public void No_hub_api_authentication_was_introduced(string construct)
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => File.ReadAllText(f).Contains(construct, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The provider runtimes know nothing about isolation, in either direction.
    ///
    /// The companion to the provider-vocabulary guard. Isolation carries no provider vocabulary,
    /// and a provider runtime carries no isolation vocabulary - so which model runs the work and
    /// what boundary it runs inside stay independent questions. Tangling them would mean a new
    /// provider had to be taught about WSL, or a new backend about Claude.
    /// </summary>
    [Theory]
    [InlineData("IsolationLevel")]
    [InlineData("HardIsolation")]
    [InlineData("Readiness")]
    [InlineData("Wsl")]
    public void The_agent_runtimes_know_nothing_about_isolation(string term)
    {
        var offenders = ProductionFiles("CoreX.Hub.Agents")
            .Where(f => File.ReadAllText(f).Contains(term, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The process primitive is not wired into admission, and cannot be reached from it.
    ///
    /// This replaces a Phase 10C-1 guard that asserted no systemd code existed anywhere. That was
    /// the right invariant while there was no process plane and is the wrong one now that there
    /// is: what must stay true is not that the mechanism is absent, but that it is not mistaken
    /// for the whole workflow. So the admission authority is built from a readiness probe, a
    /// profile source and an execution plane - and never from an executor.
    /// </summary>
    [Fact]
    public void The_process_primitive_is_not_wired_into_admission()
    {
        var admission = CodeOnly(SourceOf(AdmissionFileName));

        Assert.DoesNotContain(nameof(IHardProcessExecutor), admission, StringComparison.Ordinal);
        Assert.DoesNotContain("HardProcessRequest", admission, StringComparison.Ordinal);

        var composition = CodeOnly(SourceOf("IsolationComposition.cs"));
        var admissionConstruction = composition[composition.IndexOf(
            "new HardIsolationAdmissionService(", StringComparison.Ordinal)..];

        var end = admissionConstruction.IndexOf(");", StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Executor", admissionConstruction[..end], StringComparison.Ordinal);
    }

    /// <summary>
    /// A workflow surface that belongs to a later slice has not been pulled forward.
    ///
    /// The process primitive is deliberately the only thing that moved. Repository placement,
    /// context, evidence routing, provider authentication and control plane isolation are all
    /// still on the host, and a half-migrated one of them would be the most dangerous possible
    /// state: some of the work inside the boundary and some outside, with nothing saying which.
    /// </summary>
    [Theory]
    [InlineData("Worktree")]
    [InlineData("ContextPack")]
    [InlineData("EvidenceBlobStore")]
    [InlineData("IAgentRuntime")]
    [InlineData("VerificationProfile")]
    [InlineData("ReviewEngine")]
    public void No_later_workflow_surface_was_pulled_forward(string concept)
    {
        var offenders = IsolationSourceFiles()
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(concept, StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// No real company data reached the isolation work (ADR-0017).
    ///
    /// These are the names that would appear if a real onboarding had been started early. Every
    /// fixture in this slice is synthetic on purpose.
    /// </summary>
    [Theory]
    [InlineData("kronos")]
    [InlineData("nakliye360")]
    [InlineData("kariora")]
    public void No_real_company_data_reached_the_isolation_work(string name)
    {
        var offenders = IsolationSourceFiles()
            .Where(f => File.ReadAllText(f).Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The topology question is asked about the machine, not about one organization.
    ///
    /// The Phase 10A finding encoded as a shape: a rule that could be answered from a single
    /// organization's own state would be exactly the rule that cannot see a neighbour. So the
    /// evaluation takes every participant, and the service reads them from the catalogue rather
    /// than from the subject.
    /// </summary>
    [Fact]
    public void The_topology_rule_is_asked_about_every_organization()
    {
        var evaluate = typeof(IsolationTopologyReadiness)
            .GetMethod(nameof(IsolationTopologyReadiness.For), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(evaluate);
        Assert.Contains(
            evaluate!.GetParameters(),
            p => typeof(IEnumerable<IsolationParticipant>).IsAssignableFrom(p.ParameterType));

        Assert.Contains("ListOrganizationsAsync", SourceOf(AdmissionFileName), StringComparison.Ordinal);
    }

    /// <summary>
    /// The required set is closed: a probe cannot widen or narrow the questions.
    ///
    /// Both halves matter. One declaration means a probe cannot answer a different set from the
    /// one the verdict is computed over; every declared identifier being in it means a check can
    /// never be defined and then quietly left out of the requirement.
    /// </summary>
    [Fact]
    public void The_required_check_set_is_declared_once_and_is_complete()
    {
        var declarations = RepoLayout.ProductionSourceFiles
            .Where(f => File.ReadAllText(f).Contains("class HardIsolationChecks", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Equal(["IsolationReadiness.cs"], declarations);

        var declared = typeof(HardIsolationChecks)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType: var t } && t == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(
            declared.Order(StringComparer.Ordinal),
            HardIsolationChecks.Required.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The execution plane is honestly described as absent.
    ///
    /// Phase 10C-1 can measure a hard environment and cannot run a task inside one. The type
    /// that says so ships in the Application layer, so nothing downstream can assume otherwise.
    /// </summary>
    [Fact]
    public void This_build_declares_no_hard_execution_plane()
    {
        var plane = new UnavailableHardExecutionPlane();

        Assert.False(plane.IsAvailable);
        Assert.NotEmpty(plane.UnavailableReason);

        var implementations = Application.GetTypes()
            .Where(t => typeof(IHardExecutionPlane).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => t.Name)
            .ToList();

        Assert.Equal([nameof(UnavailableHardExecutionPlane)], implementations);
    }

    // -- the shared filesystem boundary ------------------------------------------------------

    /// <summary>
    /// The two shared WSL surfaces are denied, and the denial is not optional.
    ///
    /// <c>/mnt/wsl</c> was measured carrying a file from one disposable environment to another, in
    /// both directions and off the same kernel device. It is the reason this policy exists, so it
    /// cannot quietly leave the list; <c>/mnt/wslg</c> is per environment on the measured build and
    /// is denied anyway, because it carries a read only bind of the whole root filesystem and
    /// "shared today" is not worth re-measuring on every WSL update.
    /// </summary>
    [Fact]
    public void The_shared_wsl_surfaces_are_always_denied()
    {
        var denied = HardFilesystemPolicy.Phase10.DeniedSurfaces.Select(d => d.Path).ToList();

        Assert.Contains("/mnt/wsl", denied);
        Assert.Contains("/mnt/wslg", denied);

        // Every entry says why it is there, so the next person can judge it rather than guess.
        Assert.All(
            HardFilesystemPolicy.Phase10.DeniedSurfaces,
            surface => Assert.False(string.IsNullOrWhiteSpace(surface.Reason)));

        // And the boundary really renders each one.
        var properties = HardMountSandbox.Properties(HardFilesystemPolicy.Phase10);

        Assert.All(
            HardFilesystemPolicy.Phase10.DeniedSurfaces,
            surface => Assert.Contains(
                "InaccessiblePaths=" + (surface.Optional ? "-" : string.Empty) + surface.Path, properties));

        // A private mount namespace is what makes any of it a per workload fact.
        Assert.Contains("PrivateMounts=yes", properties);
    }

    /// <summary>
    /// The boundary reaches the launch path, and a vector that lost it is not a run.
    ///
    /// "The unit started" is not evidence of containment. A run missing one of these properties is
    /// not a slightly less isolated run; it is an unisolated one in the dimension left out, which
    /// is why the command re-reads what it built before handing it back.
    /// </summary>
    [Fact]
    public void Every_hard_launch_carries_the_boundary()
    {
        var code = CodeOnly(SourceOf("SystemdRunCommand.cs"));

        Assert.Contains("HardMountSandbox.Properties(HardFilesystemPolicy.Phase10)", code, StringComparison.Ordinal);
        Assert.Contains("HardMountSandbox.IsFullyApplied", code, StringComparison.Ordinal);

        // Not conditional on anything: no branch decides whether a workload gets the boundary.
        Assert.DoesNotContain("if (HardMountSandbox", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing a caller can spell reaches the boundary.
    ///
    /// An agent that could name a path to expose, or ask for a weaker sandbox, would be an agent
    /// choosing its own isolation. The request carries a working directory, an argument vector and
    /// an environment, and none of those is a sandbox property.
    /// </summary>
    [Fact]
    public void A_workload_cannot_ask_for_a_weaker_boundary()
    {
        var request = CodeOnly(SourceOf("HardProcess.cs"));

        foreach (var forbidden in new[]
                 {
                     "InaccessiblePaths", "PrivateMounts", "BindPaths", "BindReadOnlyPaths",
                     "TemporaryFileSystem", "ProtectSystem", "ProtectHome", "MountFlags",
                     "PrivateTmp", "ProtectProc", "Sandbox", "DeniedSurface",
                 })
        {
            Assert.DoesNotContain(forbidden, request, StringComparison.Ordinal);
        }

        // The renderer reads the policy and nothing else - no request, no configuration.
        var sandbox = CodeOnly(SourceOf("HardMountSandbox.cs"));

        foreach (var forbidden in new[]
                 {
                     "HardProcessRequest", "AgentRunRequest", "IHubConfig", "Task",
                     "Environment.GetEnvironmentVariable",
                 })
        {
            Assert.DoesNotContain(forbidden, sandbox, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The boundary is a mount namespace and never a change to the machine.
    ///
    /// Unmounting a shared WSL surface would take it away from the developer's own distributions
    /// and from every other product using them, and it would outlive the run. Nothing in this
    /// build unmounts anything.
    /// </summary>
    [Fact]
    public void Nothing_unmounts_a_shared_surface()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)) is var code
                        && (code.Contains("umount", StringComparison.OrdinalIgnoreCase)
                            || code.Contains("\"mount\"", StringComparison.Ordinal)))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// No production code names the operator's own distributions.
    ///
    /// The environment a hard organization runs in comes from its profile. A build that mentioned
    /// the developer's Ubuntu or the Docker Desktop distribution by name would be a build with a
    /// path to mutating them.
    /// </summary>
    [Fact]
    public void No_production_code_names_the_operators_distributions()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)) is var code
                        && (code.Contains("docker-desktop", StringComparison.OrdinalIgnoreCase)
                            || code.Contains("\"Ubuntu", StringComparison.Ordinal)))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Readiness answers the peer filesystem question by running a workload, not by reading a file.
    ///
    /// This check used to parse the environment's mount table, which could only ever say that
    /// <c>/mnt/wsl</c> exists - it does, in every distribution on the machine - so it failed
    /// permanently for a condition nobody could clear, and it answered a question about the
    /// distribution rather than about the workload. The measurement now runs as the agent, inside
    /// the boundary the agent will actually get.
    /// </summary>
    [Fact]
    public void Peer_filesystem_readiness_is_measured_from_the_workload()
    {
        var code = CodeOnly(SourceOf("WslReadinessProbe.cs"));

        Assert.Contains("HardMountSandbox.Properties(HardFilesystemPolicy.Phase10)", code, StringComparison.Ordinal);
        Assert.Contains("\"--uid=\" + profile.AgentUser", code, StringComparison.Ordinal);

        // Present is not the same as used. The surface loop has to be what runs the workload, or
        // the launch helper becomes dead code beside a verdict somebody decided another way - which
        // is a mutation that leaves every string this guard looks for exactly where it was.
        var loop = code[code.IndexOf("foreach (var surface in policy.DeniedSurfaces)", StringComparison.Ordinal)..];

        Assert.Contains("await InSandboxAsync(runner, profile, [Test, \"-r\", surface.Path], ct)", loop, StringComparison.Ordinal);
        Assert.Contains("await InSandboxAsync(runner, profile, [Test, \"-w\", surface.Path], ct)", loop, StringComparison.Ordinal);

        // And the verdicts are read off what those answered, rather than constructed.
        Assert.Contains("reachable.Add(surface.Path", loop, StringComparison.Ordinal);

        // The mount table no longer answers it, and no path literal stands in for the policy.
        Assert.DoesNotContain("CrossEnvironmentMountPoints", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/mnt/wsl\"", code, StringComparison.Ordinal);

        // Both directions are reported, so a boundary that denied everything is not a pass.
        Assert.Contains("HardIsolationChecks.WorkloadFilesystemUsable", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The filesystem questions are required, and the pair cannot answer for each other.
    ///
    /// Two environments can be perfectly separated on the filesystem and share a subnet, or the
    /// reverse. A single check reporting both would let either answer stand for the other, which
    /// is the same reason the network questions are split.
    /// </summary>
    [Fact]
    public void The_filesystem_questions_are_required_and_distinct()
    {
        foreach (var id in new[]
                 {
                     HardIsolationChecks.SharedFilesystemUnreachable,
                     HardIsolationChecks.OtherHardEnvironmentUnreachable,
                     HardIsolationChecks.WorkloadFilesystemUsable,
                 })
        {
            Assert.Contains(id, HardIsolationChecks.Required);
        }

        Assert.NotEqual(
            HardIsolationChecks.SharedFilesystemUnreachable,
            HardIsolationChecks.PeerNetworkUnreachable);

        Assert.NotEqual(
            HardIsolationChecks.OtherHardEnvironmentUnreachable,
            HardIsolationChecks.PeerNetworkUnreachable);
    }

    /// <summary>
    /// The mount boundary does not decide anything about the network.
    ///
    /// The two boundaries are composed at launch, and the reason to keep the renderers apart is
    /// that solving one by dropping the other is the easy mistake: a mount sandbox that emitted
    /// its own network property could quietly move a workload out of its organization's namespace
    /// and every filesystem test would still pass.
    /// </summary>
    [Fact]
    public void The_mount_boundary_decides_nothing_about_the_network()
    {
        var properties = HardMountSandbox.Properties(HardFilesystemPolicy.Phase10);

        Assert.DoesNotContain(properties, p => p.StartsWith("PrivateNetwork", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, p => p.StartsWith("NetworkNamespacePath", StringComparison.Ordinal));

        var sandbox = CodeOnly(SourceOf("HardMountSandbox.cs"));

        Assert.DoesNotContain("Network", sandbox, StringComparison.Ordinal);
    }

    /// <summary>
    /// The boundary does not replace the temporary directories.
    ///
    /// It did, briefly, and the machine tests caught it. <c>PrivateTmp=yes</c> was there to close
    /// <c>/tmp/.X11-unix</c>, which is bound from the graphical stack's tmpfs; what it also does is
    /// give the unit a private <c>/var/tmp</c>, so an execution root underneath that one stopped
    /// existing the moment the workload started and every run died with a chdir failure before
    /// reaching its first instruction.
    ///
    /// The lesson is the one this phase is scoped by: close the shared surface, not the directory
    /// it happens to live in. The socket is denied by name instead, and a workload keeps the
    /// temporary directories it has always had.
    /// </summary>
    [Fact]
    public void The_boundary_does_not_replace_the_temporary_directories()
    {
        var properties = HardMountSandbox.Properties(HardFilesystemPolicy.Phase10);

        Assert.DoesNotContain(properties, p => p.StartsWith("PrivateTmp", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, p => p.StartsWith("TemporaryFileSystem", StringComparison.Ordinal));

        // Neither temporary root is denied wholesale; the one shared thing inside them is.
        Assert.DoesNotContain("InaccessiblePaths=/tmp", properties);
        Assert.DoesNotContain("InaccessiblePaths=/var/tmp", properties);
        Assert.Contains("InaccessiblePaths=-/tmp/.X11-unix", properties);
    }

    /// <summary>
    /// A surface that must be there and is not refuses the launch; one that may be absent does not.
    ///
    /// Both halves matter. A typo in the denied list must not become a boundary that quietly
    /// closes nothing - systemd refuses the unit, which is the right answer. But demanding a path
    /// that only exists while the graphical stack is running would refuse every workload on a
    /// machine where it is not, and there is nothing to hide when it is absent.
    /// </summary>
    [Fact]
    public void Only_a_surface_that_may_be_absent_is_marked_optional()
    {
        var policy = HardFilesystemPolicy.Phase10;

        // The measured cross environment channel is never optional.
        Assert.False(policy.DeniedSurfaces.Single(s => s.Path == "/mnt/wsl").Optional);
        Assert.False(policy.DeniedSurfaces.Single(s => s.Path == "/mnt/wslg").Optional);

        var properties = HardMountSandbox.Properties(policy);

        foreach (var surface in policy.DeniedSurfaces)
        {
            Assert.Contains(
                "InaccessiblePaths=" + (surface.Optional ? "-" : string.Empty) + surface.Path, properties);
        }
    }

    /// <summary>
    /// Neither the domain nor a provider runtime knows any of this exists.
    ///
    /// A mount property in the domain would make containment a modelling concern; a shared WSL
    /// path in a provider adapter would make it that vendor's business. It is neither.
    /// </summary>
    [Fact]
    public void The_domain_and_the_providers_know_nothing_of_the_boundary()
    {
        foreach (var project in new[] { "CoreX.Hub.Domain", "CoreX.Hub.Agents" })
        {
            var offenders = ProductionFiles(project)
                .Where(f => CodeOnly(File.ReadAllText(f)) is var code
                            && (code.Contains("/mnt/wsl", StringComparison.Ordinal)
                                || code.Contains("InaccessiblePaths", StringComparison.Ordinal)
                                || code.Contains("PrivateMounts", StringComparison.Ordinal)
                                || code.Contains("HardFilesystemPolicy", StringComparison.Ordinal)
                                || code.Contains("HardMountSandbox", StringComparison.Ordinal)))
                .Select(RepoLayout.Relative)
                .ToList();

            Assert.Empty(offenders);
        }
    }

    private static readonly Regex WslWord = new(@"\bWSL\b|\bWsl", RegexOptions.CultureInvariant);

    /// <summary>An absolute path to a program the probe runs inside the environment.</summary>
    private static readonly Regex GuestExecutable =
        new(@"""/(?:usr/)?s?bin/[a-z0-9._-]+""", RegexOptions.CultureInvariant);

    private static IEnumerable<string> IsolationSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(
            f => f.Contains($"{Path.DirectorySeparatorChar}Isolation{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The hard workspace adapter, which is isolation work living in its own folder.</summary>
    private static IEnumerable<string> HardWorkspaceSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(
            f => f.Contains($"{Path.DirectorySeparatorChar}HardWorkspace{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The hard network adapter, likewise.</summary>
    private static IEnumerable<string> HardNetworkSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(
            f => f.Contains($"{Path.DirectorySeparatorChar}HardNetwork{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The hard evidence adapter, likewise.</summary>
    private static IEnumerable<string> HardEvidenceSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(
            f => f.Contains($"{Path.DirectorySeparatorChar}HardEvidence{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> ProductionFiles(string project) =>
        RepoLayout.ProductionSourceFiles.Where(
            f => f.Contains($"{Path.DirectorySeparatorChar}{project}{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string SourceOf(string fileName) =>
        File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(
            f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal)));

    /// <summary>
    /// Strips comments so a guard matches code rather than the prose explaining it.
    ///
    /// Every one of these invariants is worth writing about in the file it governs, and a
    /// scanner that could not tell the two apart would make the codebase harder to explain in
    /// exchange for no additional safety.
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join(
            '\n',
            source.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                               && !line.StartsWith("*", StringComparison.Ordinal)
                               && !line.StartsWith("/*", StringComparison.Ordinal)));
}
