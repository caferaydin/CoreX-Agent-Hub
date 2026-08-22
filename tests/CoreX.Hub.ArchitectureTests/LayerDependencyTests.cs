using System.Reflection;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Architecture invariant E.1 #4: dependencies point one way only.
/// This is checked against the compiled assemblies, not against project files, so a
/// transitive leak cannot slip through.
/// </summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(Organization).Assembly;
    private static readonly Assembly Application = typeof(IHubStore).Assembly;
    private static readonly Assembly Infrastructure = typeof(HubDbContext).Assembly;
    private static readonly Assembly Execution = typeof(FileSystemGuard).Assembly;
    private static readonly Assembly Cli = typeof(Cli.HubCli).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        var references = ReferencedNames(Domain);

        Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("YamlDotNet", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_depends_on_domain_only()
    {
        var references = ReferencedNames(Application);

        Assert.Contains("CoreX.Hub.Domain", references);
        Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.Cli", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("YamlDotNet", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_presentation_layer()
    {
        var references = ReferencedNames(Infrastructure);

        Assert.Contains("CoreX.Hub.Application", references);
        Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.Cli", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_is_the_only_composition_root()
    {
        var references = ReferencedNames(Cli);

        Assert.Contains("CoreX.Hub.Application", references);
        Assert.Contains("CoreX.Hub.Infrastructure", references);
    }

    [Fact]
    public void Execution_depends_on_nothing_but_the_base_class_library()
    {
        // The Phase 2 execution primitives are a leaf, like the domain: no CoreX project
        // and no third-party package. Later phases consume them, never the other way round.
        var references = ReferencedNames(Execution);

        Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("YamlDotNet", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_depends_on_execution_yet()
    {
        // Phase 2 introduces the primitives; wiring them into the orchestrator is Phase 5.
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Cli })
        {
            Assert.DoesNotContain(
                ReferencedNames(assembly),
                r => r.StartsWith("CoreX.Hub.Execution", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void No_forbidden_infrastructure_packages_are_referenced()
    {
        string[] forbidden =
        [
            "MediatR", "AutoMapper", "MassTransit", "StackExchange.Redis",
            "RabbitMQ.Client", "Confluent.Kafka", "Npgsql", "FluentValidation",
        ];

        foreach (var assembly in new[] { Domain, Application, Infrastructure, Execution, Cli })
        {
            var references = ReferencedNames(assembly);
            foreach (var name in forbidden)
            {
                Assert.DoesNotContain(references, r => r.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static HashSet<string> ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)];
}
