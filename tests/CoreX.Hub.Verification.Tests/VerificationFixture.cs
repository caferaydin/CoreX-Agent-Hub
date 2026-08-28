using System.Runtime.Versioning;
using CoreX.Hub.Composition.Verification;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// A disposable sandbox for one verification test: a workspace root, a Hub owned data
/// directory holding the evidence blobs and the baseline cache, and a wired engine.
///
/// Nothing here touches a real repository, a real remote or the developer environment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationFixture : IDisposable
{
    public const string FixtureExecutableName = "corex-fixture.exe";

    private VerificationFixture(string root)
    {
        Root = root;
        WorkspaceRoot = Path.Combine(root, "workspace");
        DataDirectory = Path.Combine(root, "data");

        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(DataDirectory);

        EvidenceStore = VerificationComposition.CreateEvidenceStore(DataDirectory);
        BaselineCache = VerificationComposition.CreateBaselineCache(DataDirectory);
        Files = new FileSystemGuardVerificationFileReader();
        Executor = new ProcessVerificationCommandExecutor(WorkspaceRoot);
        Sink = new BlobEvidenceSink(EvidenceStore);
    }

    public string Root { get; }

    public string WorkspaceRoot { get; }

    public string DataDirectory { get; }

    public EvidenceBlobStore EvidenceStore { get; }

    public FileVerificationBaselineCache BaselineCache { get; }

    public FileSystemGuardVerificationFileReader Files { get; }

    public ProcessVerificationCommandExecutor Executor { get; }

    public BlobEvidenceSink Sink { get; }

    /// <summary>Absolute path of the deterministic .NET fixture executable.</summary>
    public static string FixtureExecutable { get; } = ResolveFixtureExecutable();

    /// <summary>The allowlist a normal test uses: one logical name, one absolute path.</summary>
    public static ExecutableAllowlist DefaultAllowlist { get; } = BuildAllowlist();

    public static VerificationFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-verify-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return new VerificationFixture(root);
    }

    public VerificationEngine CreateEngine(ExecutableAllowlist? allowlist = null) =>
        new(Executor, Files, Sink, new HostVerificationExecutableResolver(allowlist ?? DefaultAllowlist));

    public VerificationRequest Request(bool collectEvidence = true) => new()
    {
        WorkspaceRoot = WorkspaceRoot,
        EnvironmentAllowlist = ["SystemRoot", "TEMP", "TMP"],
        Label = "fixture",
        CollectEvidence = collectEvidence,
    };

    /// <summary>
    /// A second, independent working tree with its own engine. Phase 4 collects a baseline
    /// from a directory the caller supplies: the verification module never creates a git
    /// worktree and does not know how one would be obtained.
    /// </summary>
    public (VerificationEngine Engine, VerificationRequest Request) CreateTree(string name)
    {
        var root = Path.Combine(Root, name);
        Directory.CreateDirectory(root);

        var engine = new VerificationEngine(
            new ProcessVerificationCommandExecutor(root),
            Files,
            Sink,
            new HostVerificationExecutableResolver(DefaultAllowlist));

        var request = new VerificationRequest
        {
            WorkspaceRoot = root,
            EnvironmentAllowlist = ["SystemRoot", "TEMP", "TMP"],
            Label = name,
        };

        return (engine, request);
    }

    /// <summary>Creates a directory under the workspace root and returns its full path.</summary>
    public string CreateWorkspaceDirectory(string relative)
    {
        var full = Path.Combine(WorkspaceRoot, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void WriteWorkspaceFile(string relative, string content)
    {
        var full = Path.Combine(WorkspaceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Every regular file currently stored under the evidence blob root.</summary>
    public IReadOnlyList<string> BlobFiles() =>
        Directory.Exists(EvidenceStore.BlobsRoot)
            ? [.. Directory.EnumerateFiles(EvidenceStore.BlobsRoot, "*", SearchOption.AllDirectories)]
            : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A test sandbox left behind is noise, never a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ExecutableAllowlist BuildAllowlist()
    {
        var allowlist = ExecutableAllowlist.Create(
            new Dictionary<string, string> { ["fixture"] = FixtureExecutable },
            out var problems);

        Assert.Empty(problems);
        return allowlist;
    }

    private static string ResolveFixtureExecutable()
    {
        var directory = AppContext.BaseDirectory;
        var candidate = Path.Combine(directory, FixtureExecutableName);

        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            $"The deterministic fixture executable was not found next to the tests ({candidate}).");
    }
}
