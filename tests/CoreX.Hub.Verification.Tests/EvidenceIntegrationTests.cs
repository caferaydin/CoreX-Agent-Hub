using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// The whole path: a real process writes secret bearing output and a secret bearing
/// report, and nothing unredacted survives into the evidence tree.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EvidenceIntegrationTests
{
    [Fact]
    public async Task Each_step_produces_stdout_stderr_and_report_evidence_references()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["report", "results.xml", "junit-fail", "0"],
                parser: VerificationResultParser.JUnit,
                resultPath: "results.xml"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);
        var step = run.Steps[0];

        Assert.Equal(VerificationStepStatus.Passed, step.Status);
        Assert.NotNull(step.StdoutEvidence);
        Assert.Equal(EvidenceKind.CommandStdout, step.StdoutEvidence!.Kind);
        Assert.NotNull(step.TestReportEvidence);
        Assert.Equal(EvidenceKind.TestReport, step.TestReportEvidence!.Kind);

        // The result carries references, not the payload.
        Assert.True(fixture.EvidenceStore.Exists(step.StdoutEvidence.Sha256));
        Assert.True(fixture.EvidenceStore.Exists(step.TestReportEvidence.Sha256));

        // Counters are structured, and they came from the report rather than the console.
        Assert.Equal(8, step.TestSummary!.Total);
        Assert.Equal(2, step.TestSummary.Failed);
        Assert.Equal(1, step.TestSummary.Errors);
        Assert.Equal(1, step.TestSummary.Skipped);
        Assert.Equal(4, step.TestSummary.Passed);
    }

    [Fact]
    public async Task A_step_writing_to_stderr_produces_stderr_evidence()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("noisy", ["both", "on stdout", "on stderr"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);
        var step = run.Steps[0];

        Assert.NotNull(step.StderrEvidence);
        Assert.Equal(EvidenceKind.CommandStderr, step.StderrEvidence!.Kind);

        var stored = await fixture.EvidenceStore.ReadTextAsync(
            step.StderrEvidence.Sha256, CancellationToken.None);
        Assert.Contains("on stderr", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secrets_on_stdout_and_stderr_never_reach_the_blob_tree()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("leaky", ["secrets", "both"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);
        var step = run.Steps[0];

        Assert.Equal(VerificationStepStatus.Passed, step.Status);
        Assert.True(step.StdoutEvidence!.RedactionCount >= 7);
        Assert.True(step.StderrEvidence!.RedactionCount >= 7);

        // The whole blob tree is scanned, so a secret cannot hide in an unexpected file.
        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            Assert.False(
                EvidenceText.BlobTreeContains(fixture, secret),
                "the persisted evidence still contains a synthetic secret");
        }

        var stdout = await fixture.EvidenceStore.ReadTextAsync(
            step.StdoutEvidence.Sha256, CancellationToken.None);

        Assert.Contains("[REDACTED:OPENAI_TOKEN]", stdout, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:PRIVATE_KEY]", stdout, StringComparison.Ordinal);

        // Ordinary lines around the secrets survived.
        Assert.Contains("sketch-book was updated", stdout, StringComparison.Ordinal);
        Assert.Contains("Authorized: yes", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_is_parsed_before_it_is_redacted_and_only_the_redacted_copy_is_stored()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["report", "results.xml", "junit-secret", "0"],
                parser: VerificationResultParser.JUnit,
                resultPath: "results.xml"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);
        var step = run.Steps[0];

        // The counters are correct, so parsing saw the report as the runner wrote it.
        Assert.Equal(VerificationStepStatus.Passed, step.Status);
        Assert.Equal(2, step.TestSummary!.Total);
        Assert.Equal(2, step.TestSummary.Passed);

        // The report on disk still has the secret - the engine does not rewrite the
        // workspace - but the stored evidence does not.
        var onDisk = await File.ReadAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "results.xml"));
        Assert.Contains(SyntheticSecrets.OpenAiToken, onDisk, StringComparison.Ordinal);

        var stored = await fixture.EvidenceStore.ReadTextAsync(
            step.TestReportEvidence!.Sha256, CancellationToken.None);
        Assert.DoesNotContain(SyntheticSecrets.OpenAiToken, stored, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:OPENAI_TOKEN]", stored, StringComparison.Ordinal);
        Assert.True(step.TestReportEvidence.RedactionCount >= 2);

        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            Assert.False(EvidenceText.BlobTreeContains(fixture, secret));
        }
    }

    [Fact]
    public async Task An_evidence_reference_can_populate_the_accepted_evidence_artifact_row()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["stdout", "compiling"]));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);
        var reference = run.Steps[0].StdoutEvidence!;

        // Phase 5 owns the execution association. What Phase 4 must prove is that the
        // descriptor fits the accepted Phase 1 shape without translation or a new table.
        var artifact = new EvidenceArtifact(
            Guid.NewGuid(),
            ProfileBuilder.OrganizationId,
            Guid.NewGuid(),
            null,
            reference.Kind,
            reference.Sha256,
            reference.SizeBytes,
            reference.MediaType,
            reference.RedactionCount,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(reference.Sha256, artifact.Sha256);
        Assert.Equal(reference.SizeBytes, artifact.SizeBytes);
        Assert.Equal(reference.RedactionCount, artifact.RedactionCount);
        Assert.Equal(EvidenceKind.CommandStdout, artifact.Kind);
    }

    [Fact]
    public async Task Evidence_lives_outside_every_working_tree()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["stdout", "compiling"]));

        await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.NotEmpty(fixture.BlobFiles());

        foreach (var blob in fixture.BlobFiles())
        {
            Assert.False(
                blob.StartsWith(fixture.WorkspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "an evidence blob was written inside the working tree");
        }

        Assert.StartsWith(fixture.DataDirectory, fixture.EvidenceStore.Root, StringComparison.OrdinalIgnoreCase);
    }
}
