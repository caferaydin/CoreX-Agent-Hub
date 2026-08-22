using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// What a verification step is allowed to start, see and touch.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationSafetyTests
{
    [Fact]
    public async Task An_executable_that_is_not_allowlisted_is_rejected_before_anything_starts()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("rogue", ["exit", "0"], executable: "npm"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        var step = run.Steps[0];
        Assert.Equal(VerificationStepStatus.StartFailed, step.Status);
        Assert.Null(step.ResolvedExecutable);
        Assert.Null(step.ExitCode);
        Assert.Null(step.StartedAtUtc);
        Assert.Contains("not an allowlisted verification executable", step.Diagnostic!, StringComparison.Ordinal);

        // Nothing ran, so nothing produced evidence.
        Assert.Empty(fixture.BlobFiles());
        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
    }

    [Fact]
    public void The_allowlist_refuses_a_relative_or_empty_mapping()
    {
        var allowlist = ExecutableAllowlist.Create(
            new Dictionary<string, string>
            {
                ["relative"] = "dotnet.exe",
                ["blank"] = "   ",
                ["good"] = VerificationFixture.FixtureExecutable,
            },
            out var problems);

        Assert.Equal(2, problems.Count);
        Assert.False(allowlist.Resolve("relative").IsAllowed);
        Assert.False(allowlist.Resolve("blank").IsAllowed);
        Assert.True(allowlist.Resolve("good").IsAllowed);
    }

    [Fact]
    public async Task An_allowlisted_name_resolves_to_the_absolute_approved_path()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("run", ["exit", "0"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        var resolved = run.Steps[0].ResolvedExecutable;
        Assert.NotNull(resolved);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(VerificationFixture.FixtureExecutable, resolved);
    }

    [Fact]
    public async Task A_shell_mapped_into_the_allowlist_is_still_refused_by_the_process_runner()
    {
        using var fixture = VerificationFixture.Create();

        var comspec = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Assert.True(File.Exists(comspec), "the test needs a real cmd.exe to prove the runtime policy");

        // Deliberately mis-configured: the allowlist would let it through, and the Phase 2
        // runtime no-shell policy refuses it anyway.
        var allowlist = ExecutableAllowlist.Create(
            new Dictionary<string, string> { ["shell"] = comspec }, out var problems);
        Assert.Empty(problems);

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("shell-step", ["/c", "echo hi"], executable: "shell"));

        var run = await fixture.CreateEngine(allowlist).RunAsync(
            profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.StartFailed, run.Steps[0].Status);
        Assert.Null(run.Steps[0].ExitCode);
        Assert.Contains("shell", run.Steps[0].Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Parent_process_secrets_are_not_inherited_by_a_verification_step()
    {
        using var fixture = VerificationFixture.Create();

        string[] secretNames =
        [
            "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "AWS_SECRET_ACCESS_KEY",
            "AZURE_CLIENT_SECRET", "COREX_TEST_DB_PASSWORD",
        ];

        foreach (var name in secretNames)
        {
            Environment.SetEnvironmentVariable(name, "parent-value-" + name);
        }

        try
        {
            var profile = ProfileBuilder.Profile(
                ".",
                ProfileBuilder.Step("env", ["print-env"]));

            var run = await fixture.CreateEngine().RunAsync(
                profile, fixture.Request(), CancellationToken.None);

            Assert.Equal(VerificationStepStatus.Passed, run.Steps[0].Status);

            var observed = EvidenceText.ReadAll(fixture);
            Assert.NotEmpty(observed);

            foreach (var name in secretNames)
            {
                Assert.DoesNotContain(name, observed, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("parent-value-", observed, StringComparison.Ordinal);
            }

            // The explicitly allowlisted names are the only inherited ones.
            Assert.Contains("SystemRoot", observed, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var name in secretNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }
}
