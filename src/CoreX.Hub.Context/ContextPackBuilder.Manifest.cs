using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreX.Hub.Context;

public sealed partial class ContextPackBuilder
{
    /// <summary>
    /// Writes the semantic files and returns the inventory in canonical order.
    ///
    /// The inventory comes from what was rendered, never from enumerating the filesystem:
    /// directory order is not a contract.
    /// </summary>
    private static async Task<IReadOnlyList<ContextPackFile>> MaterializeAsync(
        string pendingPath,
        IReadOnlyList<RenderedFile> files,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(pendingPath);

        var inventory = new List<ContextPackFile>(files.Count);

        foreach (var file in files)
        {
            var absolute = Path.Combine(pendingPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

            await File.WriteAllBytesAsync(absolute, file.Content, cancellationToken);

            inventory.Add(new ContextPackFile(
                file.RelativePath,
                file.Content.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(file.Content))));
        }

        return [.. inventory.OrderBy(f => f.RelativePath, StringComparer.Ordinal)];
    }

    /// <summary>
    /// ContextPackHash is the SHA-256 of a canonical payload built from the ordered
    /// semantic inventory alone:
    ///
    ///   line 1        the format version
    ///   then, per file, in ordinal order of the relative path:
    ///                 &lt;relative path&gt; LF &lt;size&gt; LF &lt;sha256&gt; LF
    ///
    /// manifest.json is deliberately absent from the payload, which is what makes the hash
    /// computable before the manifest exists and keeps it free of self reference. Absolute
    /// paths, timestamps, the machine and the user never enter it.
    /// </summary>
    public static string ComputePackHash(IReadOnlyList<ContextPackFile> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var builder = new StringBuilder();
        builder.Append(ManifestFormat).Append('\n');

        foreach (var file in inventory.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('\n');
            builder.Append(file.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(file.Sha256).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(builder.ToString())));
    }

    private static async Task WriteManifestAsync(
        string pendingPath,
        ContextBuildRequest request,
        IReadOnlyList<ContextPackFile> inventory,
        string packHash,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var manifest = new ManifestPayload
        {
            Format = ManifestFormat,
            SystemContractVersion = HubSystemContract.Version,
            ExecutionId = request.ExecutionId.ToString("D"),
            TaskKey = request.TaskKey,
            AttemptNo = request.AttemptNo,
            ContextPackHash = packHash,
            TotalBytes = totalBytes,
            BudgetBytes = MaxPackBytes,
            Files =
            [
                .. inventory.Select(f => new ManifestFile
                {
                    Path = f.RelativePath,
                    SizeBytes = f.SizeBytes,
                    Sha256 = f.Sha256,
                }),
            ],
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        await File.WriteAllBytesAsync(
            Path.Combine(pendingPath, ManifestFileName),
            Utf8NoBom.GetBytes(json + "\n"),
            cancellationToken);
    }

    private sealed class ManifestPayload
    {
        public string Format { get; init; } = string.Empty;

        public string SystemContractVersion { get; init; } = string.Empty;

        public string ExecutionId { get; init; } = string.Empty;

        public string TaskKey { get; init; } = string.Empty;

        public int AttemptNo { get; init; }

        public string ContextPackHash { get; init; } = string.Empty;

        public long TotalBytes { get; init; }

        public int BudgetBytes { get; init; }

        public ManifestFile[] Files { get; init; } = [];
    }

    private sealed class ManifestFile
    {
        public string Path { get; init; } = string.Empty;

        public long SizeBytes { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }
}
