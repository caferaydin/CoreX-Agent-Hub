using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Write time redaction. Every value here is synthetic and exists only so a test can prove
/// it never reaches persisted evidence.
/// </summary>
public sealed class RedactionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "corex-redact-" + Guid.NewGuid().ToString("N")[..12]);

    private EvidenceBlobStore Store() => new(_root);

    public static TheoryData<string, string, string> Families() => new()
    {
        { "OPENAI_TOKEN", "token=" + SyntheticSecrets.OpenAiToken, SyntheticSecrets.OpenAiToken },
        { "GITHUB_TOKEN", "token=" + SyntheticSecrets.GitHubToken, SyntheticSecrets.GitHubToken },
        { "AWS_ACCESS_KEY", "key=" + SyntheticSecrets.AwsAccessKey, SyntheticSecrets.AwsAccessKey },
        { "JWT", "bearer " + SyntheticSecrets.Jwt, SyntheticSecrets.Jwt },
        { "PASSWORD", SyntheticSecrets.ConnectionString, SyntheticSecrets.PasswordValue },
        {
            "AUTHORIZATION",
            "Authorization: " + SyntheticSecrets.AuthorizationHeaderValue,
            SyntheticSecrets.AuthorizationHeaderValue
        },
        { "PRIVATE_KEY", SyntheticSecrets.PrivateKeyBlock(), SyntheticSecrets.PrivateKeyBody },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_mvp_secret_family_is_detected_and_replaced(string family, string input, string secret)
    {
        var result = new EvidenceRedactor().Redact(input);

        Assert.True(result.RedactionCount >= 1, $"{family} was not redacted");
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:" + family + "]", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_catalog_family_is_covered_by_the_theory_data()
    {
        var covered = Families().Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);

        // A new pattern added to the catalog without a test vector fails here.
        Assert.Equal(SecretPatternCatalog.Names.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_repeated_secret_is_counted_every_time()
    {
        var text = string.Join("\n", Enumerable.Repeat("token=" + SyntheticSecrets.OpenAiToken, 4));

        var result = new EvidenceRedactor().Redact(text);

        Assert.Equal(4, result.RedactionCount);
        Assert.DoesNotContain(SyntheticSecrets.OpenAiToken, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_different_secrets_in_one_document_are_all_replaced()
    {
        var result = new EvidenceRedactor().Redact(SyntheticSecrets.Payload());

        Assert.True(result.RedactionCount >= 7, $"only {result.RedactionCount} redactions were applied");

        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_multiline_pem_block_is_replaced_as_a_whole()
    {
        var text = "before\n" + SyntheticSecrets.PrivateKeyBlock() + "\nafter";

        var result = new EvidenceRedactor().Redact(text);

        Assert.Equal(1, result.RedactionCount);
        Assert.Equal("before\n[REDACTED:PRIVATE_KEY]\nafter", result.Text);
    }

    [Fact]
    public void Ordinary_text_that_merely_resembles_a_secret_is_left_alone()
    {
        foreach (var line in SyntheticSecrets.NearMisses)
        {
            var result = new EvidenceRedactor().Redact(line);

            Assert.Equal(0, result.RedactionCount);
            Assert.Equal(line, result.Text);
        }
    }

    [Fact]
    public void Ordinary_build_output_is_not_rewritten()
    {
        const string buildOutput = """
            Determining projects to restore...
            All projects are up-to-date for restore.
            CoreX.Hub.Domain -> D:\build\CoreX.Hub.Domain.dll
            Build succeeded with 0 warnings.
            Passed!  - Failed: 0, Passed: 343, Skipped: 0
            """;

        var result = new EvidenceRedactor().Redact(buildOutput);

        Assert.Equal(0, result.RedactionCount);
        Assert.Equal(buildOutput, result.Text);
    }

    [Fact]
    public async Task The_stored_blob_is_the_redacted_content_and_the_count_is_metadata()
    {
        var store = Store();

        var descriptor = await store.WriteTextAsync(
            SyntheticSecrets.Payload(), EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);

        Assert.True(descriptor.RedactionCount >= 7);

        var stored = await store.ReadTextAsync(descriptor.Sha256, CancellationToken.None);

        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        }

        // The descriptor carries a count, never the matched value.
        var metadata = string.Join(
            "|", descriptor.Sha256, descriptor.MediaType, descriptor.Kind.ToString(),
            descriptor.RedactionCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var secret in SyntheticSecrets.AllSecretValues())
        {
            Assert.DoesNotContain(secret, metadata, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_hash_covers_the_redacted_bytes_so_the_raw_text_is_never_addressable()
    {
        var store = Store();
        var raw = "token=" + SyntheticSecrets.OpenAiToken;

        var descriptor = await store.WriteTextAsync(
            raw, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);

        var rawHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));

        Assert.NotEqual(rawHash, descriptor.Sha256);
        Assert.False(store.Exists(rawHash));

        var redactedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("token=[REDACTED:OPENAI_TOKEN]")));

        Assert.Equal(redactedHash, descriptor.Sha256);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
