using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Locates the deterministic fixture executable and builds requests for it.
/// Every run uses a temporary trust root; no real repository is ever touched.
/// </summary>
internal static class Fixture
{
    public static string ExecutablePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "corex-fixture.exe");

    /// <summary>
    /// Variables a framework dependent .NET child needs on Windows. Listing them is the
    /// point: nothing reaches the child that was not asked for by name (ADR-0007).
    /// </summary>
    public static IReadOnlyList<string> DotnetHostAllowlist { get; } =
        ["SystemRoot", "windir", "TEMP", "TMP", "PATH", "PATHEXT", "ProgramFiles", "ProgramFiles(x86)",
         "USERPROFILE", "LOCALAPPDATA", "NUMBER_OF_PROCESSORS", "DOTNET_ROOT"];

    public static ProcessRequest Request(
        TempWorkspace workspace,
        TimeSpan hardTimeout,
        params string[] arguments) =>
        new()
        {
            Executable = ExecutablePath,
            Arguments = arguments,
            WorkingDirectory = workspace.Root,
            EnvironmentAllowlist = DotnetHostAllowlist,
            HardTimeout = hardTimeout,
        };
}

/// <summary>A temporary directory used as a managed execution trust root.</summary>
internal sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string root)
    {
        Root = root;
        Guard = FileSystemGuard.Create(root);
        Runner = new ProcessRunner(Guard);
    }

    public string Root { get; }

    public FileSystemGuard Guard { get; }

    public ProcessRunner Runner { get; }

    public static TempWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-exec-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);

        // Resolve through the same canonicalisation the guard uses so temp folder links
        // (for example a redirected TEMP) do not make assertions ambiguous.
        return new TempWorkspace(new DirectoryInfo(root).ResolveLinkTarget(true)?.FullName ?? root);
    }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string CreateDirectory(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string content = "x")
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temp file must never fail a test run.
        }
    }
}
