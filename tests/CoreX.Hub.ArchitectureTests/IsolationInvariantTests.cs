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
    /// The execution plane really is absent, not merely declared absent.
    ///
    /// A half-built one would be worse than none: it would make the honest refusal look like a
    /// switch somebody forgot to flip, and the first person to flip it would be running an agent
    /// through a code path nothing has measured.
    /// </summary>
    [Theory]
    [InlineData("systemd-run")]
    [InlineData("MemoryMax")]
    [InlineData("TasksMax")]
    [InlineData("KillMode")]
    public void No_execution_plane_is_half_built(string construct)
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains(construct, StringComparison.Ordinal))
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
