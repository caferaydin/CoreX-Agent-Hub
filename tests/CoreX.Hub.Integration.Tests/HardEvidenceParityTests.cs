using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Composition.HardEvidence;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// One evidence contract, two backends, and the concurrency the contract already promised.
///
/// Two things are established here. The first is that isolation does not change evidence truth:
/// an artifact the Windows store accepts is an artifact the hard store accepts, at the same hash
/// and the same size. A storage ceiling that existed only on one side would mean the same
/// execution held a different record depending on where it ran, and nothing above the port could
/// tell - the writer returns null for "nothing to store" and the orchestrator treats that as an
/// artifact that simply did not exist.
///
/// The second is that the hard store keeps the atomic same-content behaviour the Windows one has:
/// concurrent writers of identical bytes converge on one file rather than racing to overwrite it.
/// That is asserted against the filesystem rather than against the rename's reputation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardEvidenceParityTests : IAsyncLifetime
{
    /// <summary>
    /// Past the ceiling the hard store used to impose, so the parity claim is not vacuous.
    /// </summary>
    private const int PastTheOldHardCeiling = (16 * 1024 * 1024) + 4096;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HardEvidenceFixture _fixture = new();

    private string _logicalRoot = string.Empty;

    public async Task InitializeAsync()
    {
        _logicalRoot = Path.Combine(
            Path.GetTempPath(), "corex-evidence-parity", Guid.CreateVersion7().ToString("N"));

        Directory.CreateDirectory(_logicalRoot);

        await _fixture.InitializeAsync();

        if (_fixture.Available)
        {
            Assert.True(await _fixture.GatewayA().EnsureAsync(CancellationToken.None));
        }
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();

        try
        {
            if (Directory.Exists(_logicalRoot))
            {
                Directory.Delete(_logicalRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));

    /// <summary>Text of a known byte length, made of ASCII so characters and bytes agree.</summary>
    private static string TextOf(int bytes)
    {
        var builder = new StringBuilder(bytes);

        // Varied rather than one repeated character: a store that silently truncated would still
        // produce a plausible looking blob out of a uniform payload.
        for (var i = 0; i < bytes; i++)
        {
            builder.Append((char)('a' + (i % 26)));
        }

        return builder.ToString();
    }

    // -- one semantic authority ---------------------------------------------------------------

    /// <summary>
    /// The same artifact is accepted by both stores, at the same hash and the same size.
    ///
    /// Deliberately larger than the ceiling the hard store used to carry. Before this, the
    /// Windows store took the artifact and the hard store returned null - the orchestrator then
    /// wrote no row at all, so a hard organization's execution would be missing evidence that the
    /// same execution kept on the host, with nothing anywhere saying so.
    /// </summary>
    [Fact]
    public async Task The_same_artifact_is_accepted_by_both_backends()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var text = TextOf(PastTheOldHardCeiling);

        var logical = await new EvidenceGateway(new EvidenceBlobStore(_logicalRoot))
            .WriteAsync(text, EvidenceKind.AgentStdout, "text/plain", CancellationToken.None);

        var hard = await _fixture.GatewayA()
            .WriteAsync(text, EvidenceKind.AgentStdout, "text/plain", CancellationToken.None);

        Assert.NotNull(logical);
        Assert.NotNull(hard);

        // One content identity, one size, one media type: the backend is not part of the answer.
        Assert.Equal(logical.Sha256, hard.Sha256);
        Assert.Equal(HashOf(text), hard.Sha256);
        Assert.Equal(logical.SizeBytes, hard.SizeBytes);
        Assert.Equal(PastTheOldHardCeiling, hard.SizeBytes);
        Assert.Equal(logical.MediaType, hard.MediaType);
        Assert.Equal(logical.RedactionCount, hard.RedactionCount);

        // And both read back the artifact, not a prefix of it.
        Assert.Equal(text, await _fixture.GatewayA().ReadTextAsync(hard.Sha256, CancellationToken.None));

        Assert.Equal(
            text,
            await new EvidenceBlobStore(_logicalRoot).ReadTextAsync(logical.Sha256, CancellationToken.None));
    }

    /// <summary>
    /// Both backends refuse the same thing too, through the same rule.
    ///
    /// Without this the test above would be satisfied by a hard store that accepted everything.
    /// Empty text is the one refusal the port actually has, and it is the port's, not a backend's.
    /// </summary>
    [Fact]
    public async Task Both_backends_refuse_empty_evidence()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var logical = await new EvidenceGateway(new EvidenceBlobStore(_logicalRoot))
            .WriteAsync(string.Empty, EvidenceKind.AgentStdout, "text/plain", CancellationToken.None);

        var hard = await _fixture.GatewayA()
            .WriteAsync(string.Empty, EvidenceKind.AgentStdout, "text/plain", CancellationToken.None);

        Assert.Null(logical);
        Assert.Null(hard);
    }

    // -- concurrent same content --------------------------------------------------------------

    /// <summary>
    /// Eight concurrent writers of identical bytes leave exactly one valid blob.
    ///
    /// Measured against the filesystem rather than asserted from the rename's semantics. What
    /// could go wrong and still look right: two files in the shard, a blob whose content no longer
    /// hashes to its name because a loser overwrote it, or unpublished debris left behind that a
    /// later reader has to be trusted not to mistake for evidence.
    /// </summary>
    [Fact]
    public async Task Eight_concurrent_writers_of_one_content_leave_one_valid_blob()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var text = TextOf(1024 * 1024) + " concurrent";
        var hash = HashOf(text);

        var store = _fixture.StoreA();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => store.WriteTextAsync(text, CancellationToken.None)));

        // Every caller succeeded and every caller resolved the same content identity.
        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Equal(hash, r!.Sha256));
        Assert.All(results, r => Assert.Equal(Utf8.GetByteCount(text), r!.SizeBytes));

        var layout = HardEvidenceLayout.For(_fixture.EvidenceRootA);

        // Exactly one file in the shard, and it is the one named by the hash.
        var shard = await _fixture.ControlAsync(
            ["/usr/bin/ls", "-A", layout.Shard(hash)], HardEvidenceFixture.ServiceA);

        Assert.Equal([hash], Lines(shard.Stdout));

        // The environment agrees the blob's content hashes to its name.
        var measured = await _fixture.ControlAsync(
            ["/usr/bin/sha256sum", layout.Blob(hash)], HardEvidenceFixture.ServiceA);

        Assert.Equal(hash, measured.Stdout.Trim().Split(' ')[0]);

        // Nothing unpublished survived.
        var pending = await _fixture.ControlAsync(
            ["/usr/bin/ls", "-A", layout.PendingRoot], HardEvidenceFixture.ServiceA);

        Assert.Empty(Lines(pending.Stdout));

        // And the artifact reads back whole.
        Assert.Equal(text, await store.ReadTextAsync(hash, CancellationToken.None));
    }

    /// <summary>
    /// Four concurrent writers of four contents leave four blobs.
    ///
    /// The control for the test above: if the fixture were serialising everything, or if the
    /// store were converging on one blob for the wrong reason, that test would still pass and this
    /// one would not.
    /// </summary>
    [Fact]
    public async Task Concurrent_writers_of_different_content_leave_one_blob_each()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var texts = Enumerable.Range(0, 4).Select(i => $"distinct evidence {i}").ToList();

        var store = _fixture.StoreA();

        var results = await Task.WhenAll(texts.Select(t => store.WriteTextAsync(t, CancellationToken.None)));

        Assert.All(results, r => Assert.NotNull(r));

        Assert.Equal(
            texts.Select(HashOf).Order(StringComparer.Ordinal),
            results.Select(r => r!.Sha256).Order(StringComparer.Ordinal));

        // Four distinct hashes, four readable artifacts.
        Assert.Equal(4, results.Select(r => r!.Sha256).Distinct(StringComparer.Ordinal).Count());

        foreach (var text in texts)
        {
            Assert.Equal(text, await store.ReadTextAsync(HashOf(text), CancellationToken.None));
        }
    }

    // -- failure beside success ---------------------------------------------------------------

    /// <summary>
    /// A writer that gives up mid-transfer cannot damage the one that finished.
    ///
    /// The interesting half is not that the cancelled writer stores nothing - that is already
    /// established - but that it cannot reach into the published name on its way out. Its
    /// unpublished file is its own, and the cleanup that follows a failure only ever removes that.
    /// </summary>
    [Fact]
    public async Task A_writer_that_fails_mid_transfer_leaves_the_successful_one_intact()
    {
        if (!_fixture.Available)
        {
            return;
        }

        // Several transport windows, so there is a mid-transfer for the cancellation to land in.
        var text = TextOf((int)(HardEvidenceStore.TransferChunkBytes * 2) + 7);
        var hash = HashOf(text);

        var store = _fixture.StoreA();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var succeeding = store.WriteTextAsync(text, CancellationToken.None);
        var failing = store.WriteTextAsync(text, cancellation.Token);

        var stored = await succeeding;

        try
        {
            await failing;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotNull(stored);
        Assert.Equal(hash, stored.Sha256);

        var layout = HardEvidenceLayout.For(_fixture.EvidenceRootA);

        // The published blob is still exactly what was written.
        var measured = await _fixture.ControlAsync(
            ["/usr/bin/sha256sum", layout.Blob(hash)], HardEvidenceFixture.ServiceA);

        Assert.Equal(hash, measured.Stdout.Trim().Split(' ')[0]);

        Assert.Equal([hash], Lines(
            (await _fixture.ControlAsync(
                ["/usr/bin/ls", "-A", layout.Shard(hash)], HardEvidenceFixture.ServiceA)).Stdout));

        Assert.Equal(text, await store.ReadTextAsync(hash, CancellationToken.None));
    }

    private static IReadOnlyList<string> Lines(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
