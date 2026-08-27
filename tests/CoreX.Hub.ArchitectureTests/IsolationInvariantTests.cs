using System.Reflection;
using System.Text.RegularExpressions;
using CoreX.Hub.Application.Isolation;

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
    /// The read-only probe answers questions and changes nothing, and its closed tool surface is
    /// what makes that checkable. Teaching it <c>systemd-run</c> or <c>systemctl stop</c> because
    /// execution happens to need them would hand a component with no business changing anything a
    /// general purpose way to start and stop services - so the two live in different files and
    /// only one of them may name those tools.
    /// </summary>
    [Theory]
    [InlineData("systemd-run")]
    [InlineData("\"stop\"")]
    [InlineData("\"kill\"")]
    [InlineData("\"start\"")]
    [InlineData("\"restart\"")]
    [InlineData("reset-failed")]
    [InlineData("--property=")]
    [InlineData("--uid=")]
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

        Assert.Equal([SystemdCommandFileName], offenders);
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

        Assert.Equal([SystemdCommandFileName, ProbeFileName], offenders);
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
    /// No network boundary was implemented, and none was claimed.
    ///
    /// The network is the next slice, and the failure mode this guards against is a half-built
    /// one: a firewall rule that exists, looks enforced, and has never been measured.
    /// </summary>
    [Theory]
    [InlineData("nftables")]
    [InlineData("iptables")]
    [InlineData("netns")]
    [InlineData("ip netns")]
    [InlineData("firewall")]
    [InlineData("proxy")]
    public void No_network_boundary_was_implemented(string construct)
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.OrdinalIgnoreCase))
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
