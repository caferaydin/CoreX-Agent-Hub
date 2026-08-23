using CoreX.Hub.Application.Gates;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// K3. Added lines only, the one shared catalog, and never the value itself.
/// Every secret below is synthetic and exists only so a test can prove it is caught.
/// </summary>
public sealed class SecretGuardTests
{
    private static GateVerdict Evaluate(params string[] addedLines) =>
        GateFixtures.SecretGuard.Evaluate(
            GateFixtures.Changes(
                changedPaths: ["src/config.cs"],
                addedLines: GateFixtures.Added("src/config.cs", addedLines)));

    [Fact]
    public void Ordinary_added_code_passes()
    {
        var verdict = Evaluate(
            "public sealed class Order",
            "    public string Reference { get; init; }",
            "// the user requested a password reset");

        Assert.Equal(GateStatus.Passed, verdict.Status);
        Assert.Empty(verdict.SecretHits);
    }

    public static TheoryData<string, string> Families() => new()
    {
        { "OPENAI_TOKEN", "var key = \"" + SyntheticSecrets.OpenAiToken + "\";" },
        { "GITHUB_TOKEN", "var key = \"" + SyntheticSecrets.GitHubToken + "\";" },
        { "AWS_ACCESS_KEY", "var key = \"" + SyntheticSecrets.AwsAccessKey + "\";" },
        { "JWT", "var bearer = \"" + SyntheticSecrets.Jwt + "\";" },
        { "PASSWORD", "var cs = \"" + SyntheticSecrets.ConnectionString + "\";" },
        { "AUTHORIZATION", "Authorization: " + SyntheticSecrets.AuthorizationHeaderValue },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_secret_family_blocks_when_it_is_added(string family, string addedLine)
    {
        var verdict = Evaluate(addedLine);

        Assert.True(verdict.Blocked, $"{family} was not caught");
        Assert.Contains(SecretGuard.SecretGuardBlocked, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(verdict.SecretHits, h => h.PatternName == family);
    }

    [Fact]
    public void A_private_key_block_spanning_several_added_lines_blocks()
    {
        // The reason the scanner works on a block rather than a line: this shape has no
        // single line that matches, and a line at a time scanner would miss it entirely.
        var verdict = Evaluate(SyntheticSecrets.PrivateKeyBlock().Split('\n'));

        Assert.True(verdict.Blocked);
        Assert.Contains(verdict.SecretHits, h => h.PatternName == "PRIVATE_KEY");
    }

    [Fact]
    public void A_secret_only_on_a_deleted_line_passes()
    {
        // Deleted lines never reach the gate: the capture reports added content, and K3
        // asks what this execution is about to introduce.
        var verdict = GateFixtures.SecretGuard.Evaluate(
            GateFixtures.Changes(changedPaths: ["src/config.cs"], addedLines: []));

        Assert.Equal(GateStatus.Passed, verdict.Status);
    }

    [Fact]
    public void A_pre_existing_secret_that_this_change_did_not_touch_passes()
    {
        // The file changed, but the secret is not among the added lines, so it is not this
        // execution's doing. K3 is not a repository audit.
        var verdict = GateFixtures.SecretGuard.Evaluate(
            GateFixtures.Changes(
                changedPaths: ["src/config.cs"],
                addedLines: GateFixtures.Added("src/config.cs", "// unrelated comment added by the agent")));

        Assert.Equal(GateStatus.Passed, verdict.Status);
    }

    [Fact]
    public void A_hit_names_the_family_the_path_and_the_line_but_never_the_value()
    {
        var verdict = Evaluate(
            "// nothing here",
            "var key = \"" + SyntheticSecrets.OpenAiToken + "\";");

        var hit = Assert.Single(verdict.SecretHits);

        Assert.Equal("OPENAI_TOKEN", hit.PatternName);
        Assert.Equal("src/config.cs", hit.RelativePath);
        Assert.Equal(2, hit.LineNumber);

        // Nothing anywhere in the verdict carries the secret itself.
        var rendered = string.Join(
            "|", verdict.Reason, verdict.Describe(), hit.Describe(), hit.PatternName, hit.RelativePath);

        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_gate_and_evidence_redaction_read_the_same_catalog()
    {
        // Not "the same families by coincidence": the same source object.
        Assert.Equal(SecretPatternCatalog.Names, GateFixtures.SecretGuard.PatternNames);

        var redactor = new EvidenceRedactor();

        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            var line = "value=" + secret;

            var blocked = Evaluate(line).Blocked;
            var redacted = redactor.Redact(line).AnythingRedacted;

            Assert.True(
                blocked == redacted,
                $"the gate and the redactor disagreed about a value: gate={blocked}, redactor={redacted}");
        }
    }

    [Fact]
    public void Detection_uses_patterns_only_with_no_entropy_model_or_network()
    {
        // A long high entropy string that matches no declared shape is not a secret here.
        var verdict = Evaluate("var noise = \"Zq7Z1x4mVb8Kd2Ln9Rt3Wy6Pc0Ae5Hj1Fs4Gu8Iv2Ox\";");

        Assert.Equal(GateStatus.Passed, verdict.Status);
        Assert.Equal(SecretPatternCatalog.Names.Count, GateFixtures.SecretGuard.PatternNames.Count);
    }
}
