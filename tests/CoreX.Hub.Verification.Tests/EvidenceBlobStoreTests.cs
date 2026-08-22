using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Content addressed storage: naming, deduplication, concurrency and atomic finalisation.
/// </summary>
public sealed class EvidenceBlobStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "corex-blobs-" + Guid.NewGuid().ToString("N")[..12]);

    private const string Text = "verification output that contains no secret at all";

    private EvidenceBlobStore Store() => new(_root);

    [Fact]
    public async Task Text_is_stored_under_the_sha256_of_the_bytes_written()
    {
        var store = Store();

        var descriptor = await store.WriteTextAsync(
            Text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));

        Assert.Equal(expected, descriptor.Sha256);
        Assert.Equal(0, descriptor.RedactionCount);
        Assert.False(descriptor.Deduplicated);

        // blobs/<first two>/<full hash>
        var relative = Path.GetRelativePath(store.BlobsRoot, descriptor.BlobPath).Replace('\\', '/');
        Assert.Equal(expected[..2] + "/" + expected, relative);
    }

    [Fact]
    public async Task The_stored_bytes_hash_to_the_file_name()
    {
        var store = Store();

        var descriptor = await store.WriteTextAsync(
            Text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);

        var onDisk = await File.ReadAllBytesAsync(descriptor.BlobPath);
        Assert.Equal(descriptor.Sha256, Convert.ToHexStringLower(SHA256.HashData(onDisk)));
        Assert.Equal(descriptor.SizeBytes, onDisk.LongLength);
        Assert.Equal(Path.GetFileName(descriptor.BlobPath), descriptor.Sha256);
    }

    [Fact]
    public async Task Identical_content_is_stored_once()
    {
        var store = Store();

        var first = await store.WriteTextAsync(
            Text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);
        var second = await store.WriteTextAsync(
            Text, EvidenceKind.CommandStderr, "text/plain", CancellationToken.None);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(second.Deduplicated);
        Assert.Single(Directory.EnumerateFiles(store.BlobsRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Different_content_gets_a_different_hash_and_a_second_blob()
    {
        var store = Store();

        var first = await store.WriteTextAsync(
            Text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);
        var second = await store.WriteTextAsync(
            Text + " (second run)", EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);

        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Equal(2, Directory.EnumerateFiles(store.BlobsRoot, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Concurrent_writers_of_identical_content_converge_on_one_blob()
    {
        var store = Store();

        var writers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            store.WriteTextAsync(Text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None)));

        var descriptors = await Task.WhenAll(writers);

        Assert.Single(descriptors.Select(d => d.Sha256).Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(store.BlobsRoot, "*", SearchOption.AllDirectories));

        // Exactly one writer created the blob; the rest converged on it.
        Assert.Equal(1, descriptors.Count(d => !d.Deduplicated));

        var stored = await File.ReadAllTextAsync(descriptors[0].BlobPath);
        Assert.Equal(Text, stored);
    }

    [Fact]
    public async Task A_fault_before_finalisation_leaves_no_blob()
    {
        var store = Store();
        store.BeforeFinalizeAsync = _ => throw new IOException("simulated disk failure");

        await Assert.ThrowsAsync<IOException>(() =>
            store.WriteTextAsync(Text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None));

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
        Assert.False(store.Exists(expected));
        Assert.Empty(Directory.EnumerateFiles(store.BlobsRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_before_finalisation_leaves_no_blob_and_no_debris()
    {
        var store = Store();
        using var cancellation = new CancellationTokenSource();

        store.BeforeFinalizeAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteTextAsync(Text, EvidenceKind.CommandStdout, "text/plain", cancellation.Token));

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
        Assert.False(store.Exists(expected));
        Assert.Empty(Directory.EnumerateFiles(store.BlobsRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Nothing_is_ever_deleted_automatically()
    {
        var store = Store();

        var descriptors = new List<EvidenceBlobDescriptor>();
        for (var i = 0; i < 5; i++)
        {
            descriptors.Add(await store.WriteTextAsync(
                Text + i, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None));
        }

        // Writing more evidence never removes older evidence: there is no garbage
        // collection, no retention policy and no reference counting in the MVP.
        foreach (var descriptor in descriptors)
        {
            Assert.True(store.Exists(descriptor.Sha256));
        }

        Assert.Equal(5, Directory.EnumerateFiles(store.BlobsRoot, "*", SearchOption.AllDirectories).Count());

        // And the type exposes no way to remove one.
        Assert.DoesNotContain(
            typeof(EvidenceBlobStore).GetMethods(),
            m => m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                 || m.Name.Contains("Prune", StringComparison.OrdinalIgnoreCase)
                 || m.Name.Contains("Collect", StringComparison.OrdinalIgnoreCase));
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
