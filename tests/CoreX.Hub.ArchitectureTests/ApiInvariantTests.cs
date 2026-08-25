using System.Reflection;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What an HTTP surface is allowed to be.
///
/// Phase 9 adds a second way to reach the Hub, and the risk it brings is not that a route is
/// wrong - a wrong route is a bug and bugs get fixed. The risk is that the API quietly becomes
/// a second place where things are decided: a Ready condition restated in a handler, a
/// severity threshold recomputed for a response, an acceptance rule relaxed because it was
/// inconvenient over HTTP. Those would work, and they would end the property that the CLI and
/// the browser cannot disagree.
///
/// So the guards below are about authority, not about routing.
/// </summary>
public sealed class ApiInvariantTests
{
    [Fact]
    public void The_layers_below_the_api_have_never_heard_of_http()
    {
        // Not "the API is separate" - "HTTP does not exist down there". An IResult in the
        // orchestrator would compile and would have merged two layers permanently.
        foreach (var assembly in ArchitectureAssemblies.Neutral.Append(ArchitectureAssemblies.Composition))
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

            Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.Api", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_api_reaches_the_system_only_through_the_composition_root()
    {
        var references = ArchitectureAssemblies.Api
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.Contains("CoreX.Hub.Composition", references);

        // It does not wire anything itself, and it does not know the console exists.
        foreach (var forbidden in new[] { "CoreX.Hub.Workspace", "CoreX.Hub.Verification", "CoreX.Hub.Cli" })
        {
            Assert.DoesNotContain(references, r => r.StartsWith(forbidden, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void No_endpoint_decides_what_an_authority_decides()
    {
        // Every mutation route is a call. What it must not do is reach past the service into
        // the model or the database and change something itself.
        foreach (var file in ApiSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[]
                     {
                         // Aggregate transitions belong to the services that own them.
                         ".Accept(", ".AcceptByReviewOverride(", ".Reject(", ".MarkReady(",
                         ".MarkRunning(", ".MarkAwaitingAcceptance(", ".Complete(", ".Advance(",

                         // And persistence is never touched directly.
                         "DbContext", "SaveChangesAsync", "AddExecution", "AddAcceptanceDecision",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_severity_rule_is_asked_and_never_restated()
    {
        // ADR-0022: one severity authority. A response that counted blocking findings for
        // itself could disagree with the execution it was describing.
        foreach (var file in ApiSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[]
                     {
                         "FindingSeverity.Critical", "FindingSeverity.Major", ">= 1",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }

            // The threshold may be carried outward - a client should not hardcode it either -
            // but every value assigned to it has to come from the authority. Declaring the
            // field is fine; giving it a number here would be a second copy that could drift
            // from the one the gate enforces.
            foreach (var line in Lines(code).Where(l => l.Contains("MajorThreshold =", StringComparison.Ordinal)))
            {
                Assert.Contains("ReviewFindingGate.MajorThreshold", line, StringComparison.Ordinal);
            }
        }

        // It does ask, so this is not vacuous.
        Assert.Contains(
            ApiSources().Select(File.ReadAllText),
            s => s.Contains("ReviewFindingGate.HasBlockingFindings", StringComparison.Ordinal));
    }

    [Fact]
    public void Overriding_is_a_route_of_its_own_and_the_ordinary_one_cannot_do_it()
    {
        var acceptance = CodeOnly(File.ReadAllText(Source("Endpoints.Acceptance.cs")));

        // Two routes, not one route with a flag.
        Assert.Contains("/accept\"", acceptance, StringComparison.Ordinal);
        Assert.Contains("/accept-override\"", acceptance, StringComparison.Ordinal);

        // And the ordinary route hardcodes the flag off, so no request body can turn it on.
        Assert.Contains("UseOverride = false", acceptance, StringComparison.Ordinal);

        // The ordinary body has no way to express an override at all.
        Assert.DoesNotContain("UseOverride", Contracts("AcceptBody"), StringComparison.Ordinal);
        Assert.DoesNotContain("Reason", Contracts("AcceptBody"), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_here_starts_work_it_does_not_own()
    {
        foreach (var file in ApiSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            // The fire-and-forget shapes. A run nobody awaits is a run shutdown walks away
            // from, and the reason this phase has a launcher at all.
            foreach (var forbidden in new[] { "Task.Run(", "ThreadPool.QueueUserWorkItem", "new Thread(", "async void" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_api_names_no_provider()
    {
        // The Phase 8 property, extended to the new surface: there is no /claude and no
        // /codex, and no response shape that only makes sense for one of them.
        foreach (var file in ApiSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var word in new[] { "Claude", "Codex", "Gemini", "Anthropic", "OpenAI" })
            {
                Assert.False(code.Contains(word, StringComparison.OrdinalIgnoreCase), $"{name}: {word}");
            }
        }
    }

    [Fact]
    public void The_binding_is_loopback_and_cors_has_no_wildcard()
    {
        var host = CodeOnly(File.ReadAllText(Source("HubApi.cs")));
        var options = CodeOnly(File.ReadAllText(Source("HubApiOptions.cs")));

        Assert.Contains("127.0.0.1", options, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", options, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "AllowAnyOrigin", "SetIsOriginAllowed", "\"*\"" })
        {
            Assert.False(host.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }

        // Named origins, Development only.
        Assert.Contains("WithOrigins", host, StringComparison.Ordinal);
        Assert.Contains("IsDevelopment()", host, StringComparison.Ordinal);
    }

    [Fact]
    public void No_authentication_system_arrived_with_the_api()
    {
        // Local-first, single user. Adding identity here would be a product decision, not an
        // implementation detail, and it has not been made.
        foreach (var file in ApiSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[]
                     {
                         "AddAuthentication", "AddAuthorization", "JwtBearer", "OpenIdConnect",
                         "[Authorize", "ClaimsPrincipal", "RequireRole",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_wire_vocabulary_is_stable_for_a_typed_client()
    {
        var host = CodeOnly(File.ReadAllText(Source("HubApi.cs")));

        // Enums as names and instants as UTC, so a TypeScript client never depends on an
        // ordinal and never has to guess a zone.
        Assert.Contains("JsonStringEnumConverter", host, StringComparison.Ordinal);
        Assert.Contains("UtcInstantConverter", host, StringComparison.Ordinal);

        // No DTO exposes a domain aggregate or an EF type.
        foreach (var contract in typeof(Api.TaskSummaryDto).Assembly.GetTypes()
                     .Where(t => t.Name.EndsWith("Dto", StringComparison.Ordinal)))
        {
            foreach (var property in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var type = property.PropertyType;
                var name = type.FullName ?? type.Name;

                Assert.False(
                    name.StartsWith("CoreX.Hub.Domain", StringComparison.Ordinal)
                    || name.StartsWith("CoreX.Hub.Application", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
                    $"{contract.Name}.{property.Name} exposes {name}");
            }
        }
    }

    [Fact]
    public void No_user_interface_and_no_later_phase_work_arrived()
    {
        foreach (var file in ApiSources())
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[]
                     {
                         "RepairLoop", "RepairEngine", "AddRazorPages", "AddControllersWithViews",
                         "MapRazorPages", "UseStaticFiles", "React", "BillingAccount",
                     })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), forbidden);
            }
        }
    }

    private static IEnumerable<string> ApiSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Api"), "*.cs")
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static string Source(string fileName) =>
        Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Api", fileName);

    /// <summary>One contract record's declaration, so a guard can read what it carries.</summary>
    private static string Contracts(string recordName)
    {
        var text = File.ReadAllText(Source("Contracts.cs"));
        var start = text.IndexOf($"record {recordName}", StringComparison.Ordinal);

        Assert.True(start > 0, $"{recordName} is no longer declared where this guard reads it");

        var end = text.IndexOf("\npublic sealed record", start + 1, StringComparison.Ordinal);

        return end > start ? text[start..end] : text[start..];
    }

    private static IEnumerable<string> Lines(string source) => source.Split('\n');

    private static string CodeOnly(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
