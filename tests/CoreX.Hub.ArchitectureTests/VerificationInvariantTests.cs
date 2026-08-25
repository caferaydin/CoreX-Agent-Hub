using System.Reflection;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Verification;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Phase 4 structural guards. They complement the behavioural tests in
/// CoreX.Hub.Verification.Tests rather than replacing them.
/// </summary>
public sealed class VerificationInvariantTests
{
    private static readonly Assembly VerificationModule = typeof(VerificationEngine).Assembly;

    [Fact]
    public void Verification_depends_only_on_the_domain()
    {
        // E.1 #4: the Phase 4 module reaches processes, managed paths, evidence and the
        // baseline cache through its own ports, which the composition layer implements.
        var references = VerificationModule.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.Contains("CoreX.Hub.Domain", references);

        foreach (var forbidden in new[]
                 {
                     "CoreX.Hub.Execution", "CoreX.Hub.Workspace",
                     "CoreX.Hub.Infrastructure", "CoreX.Hub.Application", "CoreX.Hub.Composition",
                 })
        {
            Assert.DoesNotContain(references, r => r.StartsWith(forbidden, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
    }

    [Fact]
    public void The_lower_modules_still_do_not_know_verification()
    {
        foreach (var assembly in new[]
                 {
                     typeof(Execution.FileSystem.FileSystemGuard).Assembly,
                     typeof(Workspace.WorkspaceManager).Assembly,
                     typeof(Domain.Catalog.Organization).Assembly,
                     typeof(Application.Abstractions.IHubStore).Assembly,
                     typeof(Infrastructure.Persistence.HubDbContext).Assembly,
                 })
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty),
                r => r.StartsWith("CoreX.Hub.Verification", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Only_the_composition_layer_joins_verification_to_execution()
    {
        // The composition root, not the CLI. Phase 9 gave the system a second adapter, and
        // the property being asserted was never about the console - it is that exactly one
        // project is allowed to know both modules at once.
        var composition = typeof(global::CoreX.Hub.Composition.HubSession).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.Contains("CoreX.Hub.Verification", composition);
        Assert.Contains("CoreX.Hub.Execution", composition);

        // And the adapters reach neither directly, so neither of them can join the two.
        foreach (var adapter in new[]
                 {
                     typeof(Cli.HubCli).Assembly,
                     typeof(global::CoreX.Hub.Api.HubApi).Assembly,
                 })
        {
            var references = adapter.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

            Assert.DoesNotContain("CoreX.Hub.Verification", references);
            Assert.DoesNotContain("CoreX.Hub.Execution", references);
        }
    }

    [Fact]
    public void The_verification_module_contains_no_process_launch_and_no_shell()
    {
        // The forbidden construct scanner covers src/** as a whole; this asserts the same
        // thing specifically for the new module, so a later refactor cannot quietly move a
        // launch into it.
        foreach (var file in VerificationSourceFiles())
        {
            var text = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            Assert.False(text.Contains("Process.Start", StringComparison.Ordinal), name);
            Assert.False(text.Contains("ProcessStartInfo", StringComparison.Ordinal), name);
            Assert.False(text.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(text.Contains("powershell", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(text.Contains("git.exe", StringComparison.OrdinalIgnoreCase), name);
        }
    }

    [Fact]
    public void The_verification_module_does_not_resolve_tools_from_the_environment()
    {
        // No PATH search, and no external resolver process either.
        foreach (var file in VerificationSourceFiles())
        {
            var text = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            Assert.False(text.Contains("where.exe", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(text.Contains("Get-Command", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(text.Contains("GetEnvironmentVariable", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void Xml_report_parsing_is_configured_without_dtd_or_a_resolver()
    {
        var safeXml = File.ReadAllText(VerificationFile("SafeXml.cs"));

        Assert.Contains("DtdProcessing = DtdProcessing.Prohibit", safeXml, StringComparison.Ordinal);
        Assert.Contains("XmlResolver = null", safeXml, StringComparison.Ordinal);
        Assert.Contains("MaxCharactersFromEntities = 0", safeXml, StringComparison.Ordinal);

        // And every parser goes through it rather than loading XML on its own.
        foreach (var parser in new[] { "TrxReportParser.cs", "JUnitReportParser.cs" })
        {
            var text = File.ReadAllText(VerificationFile(parser));

            Assert.Contains("SafeXml.TryLoad", text, StringComparison.Ordinal);
            Assert.DoesNotContain("XDocument.Parse", text, StringComparison.Ordinal);
            Assert.DoesNotContain("XDocument.Load", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Evidence_metadata_carries_no_payload_column()
    {
        // ADR-0013: the blob never lives in the relational database. The accepted entity
        // has no content property, and no property that could hold one.
        var properties = typeof(EvidenceArtifact).GetProperties();

        Assert.DoesNotContain(properties, p =>
            p.Name.Contains("Content", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Text", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Blob", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(byte[]));
        Assert.Contains(properties, p => p.Name == "Sha256");
        Assert.Contains(properties, p => p.Name == "RedactionCount");
    }

    [Fact]
    public void The_blob_store_writes_through_an_atomic_rename_and_never_deletes_a_blob()
    {
        var text = File.ReadAllText(ProductionFile("EvidenceBlobStore.cs"));

        Assert.Contains("File.Move(pendingPath, finalPath, overwrite: false)", text, StringComparison.Ordinal);
        Assert.Contains("flushToDisk: true", text, StringComparison.Ordinal);

        // The only deletion in the file is the pending temporary, never a stored blob.
        Assert.DoesNotContain("File.Delete(finalPath)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_roots_are_never_placed_under_a_working_tree()
    {
        var composition = CodeOnly(File.ReadAllText(ProductionFile("VerificationComposition.cs")));

        // Both Hub owned roots hang off the data directory, not off a workspace.
        Assert.Contains("Path.Combine(Path.GetFullPath(dataDirectory)", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspacePath", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("worktree", composition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_secret_pattern_catalog_is_the_only_place_secret_shapes_are_defined()
    {
        var catalogFile = ProductionFile("SecretPatternCatalog.cs");

        foreach (var family in new[]
                 {
                     "OPENAI_TOKEN", "GITHUB_TOKEN", "AWS_ACCESS_KEY",
                     "JWT", "PRIVATE_KEY", "PASSWORD", "AUTHORIZATION",
                 })
        {
            Assert.Contains(family, SecretPatternCatalog.Names);
        }

        // No other production file declares a secret shaped expression, so a later
        // SecretGuard has to consume this catalog instead of copying it.
        foreach (var file in RepoLayout.ProductionSourceFiles.Where(f => f != catalogFile))
        {
            var text = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            Assert.False(text.Contains("ghp_", StringComparison.Ordinal), name);
            Assert.False(text.Contains("AKIA", StringComparison.Ordinal), name);
            Assert.False(text.Contains("PRIVATE KEY", StringComparison.Ordinal), name);
        }
    }

    /// <summary>
    /// Strips comments so a guard reads what the code does, not what a comment says about
    /// it. A comment naming a forbidden construct is documentation, not a violation.
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string ProductionFile(string fileName) =>
        RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));

    private static string VerificationFile(string fileName) =>
        VerificationSourceFiles()
            .Single(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));

    private static IEnumerable<string> VerificationSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Verification" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
}
