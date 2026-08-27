using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The readiness model itself: what "ready" is allowed to mean.
///
/// Every test here is a way of arriving at a pass that should not be one. That is the whole
/// value of the type - a machine that has not been examined must not be indistinguishable from
/// one that has been examined and found correct.
/// </summary>
public sealed class HardIsolationReadinessTests
{
    [Fact]
    public void Every_required_check_passing_is_ready()
    {
        var readiness = HardIsolationFixture.AllPassing("org");

        Assert.True(readiness.IsReady);
        Assert.Empty(readiness.UnmetCheckIds);
        Assert.Equal(HardIsolationChecks.Required.Count, readiness.Checks.Count);
    }

    [Fact]
    public void A_single_failure_is_not_ready()
    {
        var readiness = HardIsolationReadiness.From(
            "org",
            HardIsolationChecks.Required.Select(id => id == HardIsolationChecks.AgentUserNotRoot
                ? ReadinessCheck.Failed(id, "The agent is root.")
                : ReadinessCheck.Passed(id, "Fine.")));

        Assert.False(readiness.IsReady);
        Assert.Equal([HardIsolationChecks.AgentUserNotRoot], readiness.UnmetCheckIds);
    }

    [Fact]
    public void An_unknown_check_is_not_ready()
    {
        var readiness = HardIsolationReadiness.From(
            "org",
            HardIsolationChecks.Required.Select(id => id == HardIsolationChecks.CgroupV2Available
                ? ReadinessCheck.Unknown(id, "Could not be asked.")
                : ReadinessCheck.Passed(id, "Fine.")));

        Assert.False(readiness.IsReady);
        Assert.Equal([HardIsolationChecks.CgroupV2Available], readiness.UnmetCheckIds);
    }

    /// <summary>
    /// The property the design leans on hardest: silence is not a pass.
    ///
    /// A probe that gained a new question and forgot to answer it, or one that returned early,
    /// must make the environment not ready. Completing the set here rather than trusting the
    /// caller is what makes that true by construction instead of by review.
    /// </summary>
    [Fact]
    public void A_check_nobody_answered_is_unknown_rather_than_absent()
    {
        var readiness = HardIsolationReadiness.From(
            "org",
            HardIsolationChecks.Required
                .Where(id => id != HardIsolationChecks.HostInteropDisabled)
                .Select(id => ReadinessCheck.Passed(id, "Fine.")));

        Assert.False(readiness.IsReady);
        Assert.Equal(HardIsolationChecks.Required.Count, readiness.Checks.Count);

        var missing = readiness.Checks.Single(c => c.Id == HardIsolationChecks.HostInteropDisabled);
        Assert.Equal(ReadinessStatus.Unknown, missing.Status);
    }

    [Fact]
    public void An_empty_answer_set_is_every_check_unknown()
    {
        var readiness = HardIsolationReadiness.From("org", []);

        Assert.False(readiness.IsReady);
        Assert.Equal(HardIsolationChecks.Required.Count, readiness.UnmetCheckIds.Count);
        Assert.All(readiness.Checks, c => Assert.Equal(ReadinessStatus.Unknown, c.Status));
    }

    /// <summary>A later answer must not be able to upgrade an earlier failure.</summary>
    [Fact]
    public void A_duplicated_check_keeps_its_first_answer()
    {
        var readiness = HardIsolationReadiness.From(
            "org",
            [
                .. HardIsolationChecks.Required.Select(id => id == HardIsolationChecks.AgentUserExists
                    ? ReadinessCheck.Failed(id, "No such account.")
                    : ReadinessCheck.Passed(id, "Fine.")),
                ReadinessCheck.Passed(HardIsolationChecks.AgentUserExists, "Second opinion."),
            ]);

        Assert.False(readiness.IsReady);
        Assert.Equal(
            ReadinessStatus.Fail,
            readiness.Checks.Single(c => c.Id == HardIsolationChecks.AgentUserExists).Status);
    }

    [Fact]
    public void An_unreachable_environment_answers_nothing()
    {
        var readiness = HardIsolationReadiness.Unavailable("org", "WSL is not installed.");

        Assert.False(readiness.IsReady);
        Assert.All(readiness.Checks, c => Assert.Equal(ReadinessStatus.Unknown, c.Status));
        Assert.All(readiness.Checks, c => Assert.Contains("WSL is not installed.", c.Detail));
    }

    /// <summary>The canonical order is part of the contract: a diagnostic is read by a person.</summary>
    [Fact]
    public void Checks_come_back_in_the_declared_order()
    {
        var readiness = HardIsolationReadiness.From(
            "org", HardIsolationChecks.Required.Reverse().Select(id => ReadinessCheck.Passed(id, "Fine.")));

        Assert.Equal(HardIsolationChecks.Required, readiness.Checks.Select(c => c.Id));
    }

    [Fact]
    public void The_required_set_has_no_duplicates()
    {
        Assert.Equal(
            HardIsolationChecks.Required.Count,
            HardIsolationChecks.Required.Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>
/// Reading a hard isolation profile out of configuration.
///
/// The point of separating this from readiness is that these are all things an operator can be
/// told about without touching a machine, and none of them is worth discovering halfway through
/// a probe.
/// </summary>
public sealed class HardIsolationProfileTests
{
    [Fact]
    public void A_complete_profile_is_read()
    {
        Assert.True(HardIsolationProfile.TryRead(HardIsolationFixture.Config(), out var profile, out var problem));
        Assert.Null(problem);

        Assert.Equal(HardIsolationBackend.Wsl2, profile!.Backend);
        Assert.Equal("corex-fixture", profile.EnvironmentId);
        Assert.Equal("/srv/corex/exec", profile.ExecutionRoot);
        Assert.Equal(64, profile.ProcessLimit);
    }

    [Fact]
    public void No_configuration_at_all_is_refused()
    {
        Assert.False(HardIsolationProfile.TryRead(null, out var profile, out var problem));
        Assert.Null(profile);
        Assert.Contains("no hardIsolation configuration", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("docker")]
    [InlineData("hyperv")]
    public void An_unsupported_backend_is_refused(string backend)
    {
        var config = HardIsolationFixture.Config();
        config.Backend = backend;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("not supported", problem);
    }

    /// <summary>
    /// The agent may not own the evidence store it is being observed into.
    ///
    /// One identity playing both parts is the whole failure in miniature: the result would be
    /// written by, and editable by, the thing the result is about.
    /// </summary>
    [Fact]
    public void One_identity_for_the_agent_and_the_service_is_refused()
    {
        var config = HardIsolationFixture.Config();
        config.ServiceUser = config.AgentUser;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("must differ", problem);
    }

    [Theory]
    [InlineData(@"C:\corex\exec")]
    [InlineData("srv/corex/exec")]
    [InlineData("/")]
    public void A_root_that_is_not_an_absolute_linux_directory_is_refused(string root)
    {
        var config = HardIsolationFixture.Config();
        config.ExecutionRoot = root;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("executionRoot", problem);
    }

    [Fact]
    public void One_root_for_both_purposes_is_refused()
    {
        var config = HardIsolationFixture.Config();
        config.EvidenceRoot = config.ExecutionRoot;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("different directories", problem);
    }

    /// <summary>
    /// A ceiling has to be stated. There is no product wide default to fall back on, because
    /// the same runner drives verification and git and an invented number would become theirs.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_missing_process_ceiling_is_refused(int limit)
    {
        var config = HardIsolationFixture.Config();
        config.ProcessLimit = limit;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("processLimit", problem);
    }

    [Fact]
    public void A_missing_memory_ceiling_is_refused()
    {
        var config = HardIsolationFixture.Config();
        config.MemoryLimitBytes = 0;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("memoryLimitBytes", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unnamed_environment_is_refused(string environmentId)
    {
        var config = HardIsolationFixture.Config();
        config.EnvironmentId = environmentId;

        Assert.False(HardIsolationProfile.TryRead(config, out _, out var problem));
        Assert.Contains("environmentId", problem);
    }
}
