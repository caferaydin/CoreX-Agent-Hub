using System.Text;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Cli;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// An isolated Hub installation in a temporary directory. No test ever touches the real
/// data directory, the real hub.config.yaml, or any managed repository.
/// </summary>
internal sealed class TempHub : IDisposable
{
    private readonly List<HubDbContext> _contexts = [];

    private TempHub(string root, string configPath)
    {
        Root = root;
        DataDirectory = Path.Combine(root, "data");
        ConfigPath = configPath;
        Directory.CreateDirectory(DataDirectory);
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public string ConfigPath { get; }

    public string DatabasePath => Path.Combine(DataDirectory, HubDatabase.DatabaseFileName);

    public static TempHub Create(string? configYaml = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-hub-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);

        var configPath = Path.Combine(root, HubPaths.ConfigFileName);
        File.WriteAllText(configPath, configYaml ?? FixtureConfig.Default, new UTF8Encoding(false));

        return new TempHub(root, configPath);
    }

    public HubDbContext NewContext(OrganizationScope? scope = null)
    {
        var context = HubDatabase.CreateContext(DatabasePath, scope ?? new OrganizationScope());
        _contexts.Add(context);
        return context;
    }

    public async Task<HubDbContext> NewMigratedContextAsync(OrganizationScope? scope = null)
    {
        var context = NewContext(scope);
        await HubDatabase.MigrateAsync(context, CancellationToken.None);
        return context;
    }

    /// <summary>Drives the exact CLI dispatch path the shipped executable uses.</summary>
    public async Task<CliResult> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var fullArgs = new List<string>
        {
            "--data-dir", DataDirectory,
            "--config", ConfigPath,
        };
        fullArgs.AddRange(args);

        var exitCode = await HubCli.RunAsync(fullArgs, stdout, stderr, CancellationToken.None);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _contexts.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must never fail a test run.
        }
    }
}

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public string All => StdOut + StdErr;
}
