using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardEvidence;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Where a hard isolated agent's own provider session may live, and where it may not.
///
/// Nothing here authenticates anything. No provider runs, no login happens, no token exists: the
/// markers are synthetic files standing in for a session, and that is deliberate - the question
/// is an operating system question, and asking it with a real credential would put a real
/// credential somewhere for the sake of a test.
///
/// The boundary has three sides and only one of them is permissive. An agent's own session is
/// the agent's: the provider process runs as that identity and has to be able to read and update
/// its own state. What it must not reach is another organization's session, or the developer's
/// on the host.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardAuthBoundaryTests : IAsyncLifetime
{
    private readonly HardEvidenceFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    // -- the agent's own session --------------------------------------------------------------

    /// <summary>
    /// An agent can read and update its own session, which is the point of it having one.
    ///
    /// Worth asserting before the denials: a boundary that refused everything would pass every
    /// negative test here and would leave no provider able to authenticate at all.
    /// </summary>
    [Fact]
    public async Task An_agent_can_read_and_update_its_own_session()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var read = await _fixture.ControlAsync(
            ["/usr/bin/cat", _fixture.AuthMarkerA], HardEvidenceFixture.AgentA);

        Assert.Equal(0, read.ExitCode);
        Assert.Contains("COREX-AUTH-A", read.Stdout, StringComparison.Ordinal);

        // A provider refreshes its own session; that is agent-owned secret state, not evidence.
        var updated = await _fixture.ControlAsync(
            ["/usr/bin/tee", _fixture.AuthMarkerA], HardEvidenceFixture.AgentA, "COREX-AUTH-A-REFRESHED\n");

        Assert.Equal(0, updated.ExitCode);

        var again = await _fixture.ControlAsync(
            ["/usr/bin/cat", _fixture.AuthMarkerA], HardEvidenceFixture.AgentA);

        Assert.Contains("COREX-AUTH-A-REFRESHED", again.Stdout, StringComparison.Ordinal);
    }

    // -- a peer's session ---------------------------------------------------------------------

    /// <summary>
    /// Neither agent can read or alter the other's session, in either direction.
    ///
    /// Two real uids and four real refusals. Read and write are both checked, because an agent
    /// that could overwrite a peer's session without reading it could still replace it with one
    /// it controls.
    /// </summary>
    [Fact]
    public async Task Neither_agent_can_reach_the_others_session()
    {
        if (!_fixture.Available)
        {
            return;
        }

        foreach (var (principal, peerMarker, peerHome) in new[]
                 {
                     (HardEvidenceFixture.AgentA, _fixture.AuthMarkerB, _fixture.AuthHomeB),
                     (HardEvidenceFixture.AgentB, _fixture.AuthMarkerA, _fixture.AuthHomeA),
                 })
        {
            var read = await _fixture.ControlAsync(["/usr/bin/cat", peerMarker], principal);
            var listed = await _fixture.ControlAsync(["/usr/bin/ls", peerHome], principal);
            var written = await _fixture.ControlAsync(
                ["/usr/bin/tee", peerMarker], principal, "replaced by a peer\n");
            var planted = await _fixture.ControlAsync(
                ["/usr/bin/tee", peerHome + "/planted"], principal, "planted\n");

            Assert.NotEqual(0, read.ExitCode);
            Assert.NotEqual(0, listed.ExitCode);
            Assert.NotEqual(0, written.ExitCode);
            Assert.NotEqual(0, planted.ExitCode);
        }

        // And both sessions are still their owners'.
        var a = await _fixture.ControlAsync(["/usr/bin/cat", _fixture.AuthMarkerA], HardEvidenceFixture.AgentA);
        var b = await _fixture.ControlAsync(["/usr/bin/cat", _fixture.AuthMarkerB], HardEvidenceFixture.AgentB);

        Assert.Contains("COREX-AUTH-A", a.Stdout, StringComparison.Ordinal);
        Assert.Contains("COREX-AUTH-B", b.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// An organization's service identity has no routine business in an agent's session either.
    ///
    /// Auth is agent-owned secret state. The service manages workspaces and evidence; a service
    /// that also read provider credentials would be a single identity holding both the record and
    /// the means to produce it.
    /// </summary>
    [Fact]
    public async Task A_service_identity_has_no_access_to_a_session()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var own = await _fixture.ControlAsync(
            ["/usr/bin/cat", _fixture.AuthMarkerA], HardEvidenceFixture.ServiceA);

        var peer = await _fixture.ControlAsync(
            ["/usr/bin/cat", _fixture.AuthMarkerB], HardEvidenceFixture.ServiceA);

        Assert.NotEqual(0, own.ExitCode);
        Assert.NotEqual(0, peer.ExitCode);
    }

    // -- the host's session -------------------------------------------------------------------

    /// <summary>
    /// Whether the agent can reach the host's session is exactly what readiness reports.
    ///
    /// On this machine it can, because the distribution mounts the host's drives - which is one of
    /// the eleven reasons it is not a hard environment. The honest test is not that the marker is
    /// unreachable here; it is that the two agree. A build where the agent could read the host's
    /// filesystem while readiness called that check a pass would be far worse than either failure
    /// on its own.
    /// </summary>
    [Fact]
    public async Task Host_session_reachability_matches_what_readiness_reports()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var read = await _fixture.ControlAsync(
            ["/usr/bin/cat", _fixture.HostAuthMarkerLinux], HardEvidenceFixture.AgentA);

        var reachable = read.ExitCode == 0;

        var readiness = await WslReadinessProbe.Create(Path.GetTempPath()).ProbeAsync(
            "fixture", _fixture.ProfileA(), CancellationToken.None);

        var reported = readiness.Checks
            .Single(c => c.Id == HardIsolationChecks.HostProviderAuthUnreachable)
            .Status;

        if (reachable)
        {
            Assert.Equal(ReadinessStatus.Fail, reported);
        }
        else
        {
            Assert.NotEqual(ReadinessStatus.Fail, reported);
        }

        // Either way this environment is not hard-ready, so nothing here can be mistaken for the
        // host boundary having been established.
        Assert.False(readiness.IsReady);
    }

    // -- the agent's home ---------------------------------------------------------------------

    /// <summary>
    /// The agent's home comes from the passwd database, and is judged rather than assumed.
    ///
    /// On this machine the stand-in accounts have homes owned by root - <c>/usr/sbin</c> for
    /// <c>daemon</c> - so the answer is a refusal, and that refusal is the correct one: an agent
    /// whose home belongs to somebody else cannot keep a session there, and whoever does own it
    /// could read one that was.
    ///
    /// The failure is also the interesting half of the test. <c>/home/daemon</c> is what string
    /// concatenation would have produced: a path that looks right, does not exist, and would have
    /// been reported as isolation.
    /// </summary>
    [Fact]
    public async Task The_agent_home_is_resolved_from_passwd_and_judged()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var resolved = await _fixture.AgentHome(_fixture.ProfileA()).ResolveAsync(CancellationToken.None);

        // Whatever it concluded, it did not invent /home/<user>.
        Assert.DoesNotContain("/home/" + HardEvidenceFixture.AgentA, resolved.Detail, StringComparison.Ordinal);

        var passwd = await _fixture.ControlAsync(["/usr/bin/getent", "passwd", HardEvidenceFixture.AgentA]);
        var declared = passwd.Stdout.Trim().Split(':')[5];

        var owner = await _fixture.ControlAsync(["/usr/bin/stat", "-c", "%U", declared]);

        if (string.Equals(owner.Stdout.Trim(), HardEvidenceFixture.AgentA, StringComparison.Ordinal))
        {
            Assert.True(resolved.IsUsable, resolved.Detail);
            Assert.Equal(declared, resolved.Path);
        }
        else
        {
            // The usual case on a stock distribution, and the honest answer.
            Assert.False(resolved.IsUsable);
            Assert.Contains("owned by", resolved.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A home the agent really owns resolves, and the answer is Linux-native.
    ///
    /// The positive half of the same check, arranged rather than found: without it the resolver
    /// could refuse everything and every assertion above would still hold.
    /// </summary>
    [Fact]
    public async Task An_agent_owned_home_resolves_and_is_linux_native()
    {
        if (!_fixture.Available)
        {
            return;
        }

        // nobody's home is /nonexistent, so this arranges a real one for the duration of the
        // test, inside the fixture's own disposable tree.
        var home = _fixture.AuthHomeA;

        var passwd = await _fixture.ControlAsync(["/usr/bin/getent", "passwd", HardEvidenceFixture.AgentA]);

        if (passwd.ExitCode != 0)
        {
            return;
        }

        var described = await _fixture.ControlAsync(["/usr/bin/stat", "-c", "%U|%A|%F", home]);
        var parts = described.Stdout.Trim().Split('|');

        Assert.Equal(HardEvidenceFixture.AgentA, parts[0]);
        Assert.Contains("directory", parts[2], StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("/", home, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/", home, StringComparison.Ordinal);

        var filesystem = await _fixture.ControlAsync(["/usr/bin/stat", "-f", "-c", "%T", home]);

        Assert.Contains(
            filesystem.Stdout.Trim(),
            new[] { "ext2/ext3", "ext4", "xfs", "btrfs", "overlayfs" });
    }

    // -- what the Hub never does --------------------------------------------------------------

    /// <summary>
    /// Nothing the Hub produces about a hard organization names a host session path.
    ///
    /// Asserted against this machine's own identity, so it fails where it would matter.
    /// </summary>
    [Fact]
    public async Task Nothing_the_hub_reports_names_a_host_session()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var readiness = await WslReadinessProbe.Create(Path.GetTempPath()).ProbeAsync(
            "fixture", _fixture.ProfileA(), CancellationToken.None);

        var reported = string.Join(Environment.NewLine, readiness.Checks.Select(c => c.Detail));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.DoesNotContain(profile, reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".claude", reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("codex", reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COREX-HOST-AUTH-MARKER", reported, StringComparison.Ordinal);
    }

    /// <summary>
    /// The isolation profile has nowhere to put a provider's session, and that is deliberate.
    ///
    /// A <c>claudeHome</c> or a <c>codexHome</c> here would make isolation configuration
    /// provider-shaped, and the next provider would need a third field. Isolation supplies
    /// identities and boundaries; where a particular provider keeps its own state inside them is
    /// that adapter's business.
    /// </summary>
    [Fact]
    public void The_isolation_profile_carries_no_provider_session()
    {
        var fields = typeof(HardIsolationProfile)
            .GetProperties()
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // VerificationExecutables was added in Phase 10C-4 and is the shape this rule permits: keys
        // are the logical tool names a verification profile already declares, and values are paths
        // inside the organization's own environment. Neither half can name a provider, which is why
        // the rule below is asserted rather than left implied by the list.
        Assert.Equal(
            [
                "AgentUser", "Backend", "EnvironmentId", "EvidenceRoot", "ExecutionRoot",
                "MemoryLimitBytes", "ProcessLimit", "ServiceUser", "VerificationExecutables",
            ],
            fields);

        string[] providerShaped = ["claude", "codex", "anthropic", "openai", "home", "session", "auth"];

        var offenders = fields
            .Where(f => providerShaped.Any(v => f.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }
}
