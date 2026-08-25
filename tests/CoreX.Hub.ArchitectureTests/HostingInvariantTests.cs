using System.Text.RegularExpressions;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What serving the UI from the API is allowed to change, which is almost nothing.
///
/// One origin is a packaging decision. It is not a step towards remote hosting, it is not a
/// reason to relax anything the API refuses, and it must not make the two builds into one
/// build - a compile of a C# file that reached a package registry would make an offline build
/// impossible and would tie the backend to the health of a network.
/// </summary>
public sealed class HostingInvariantTests
{
    private static string ApiRoot => Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Api");

    [Fact]
    public void The_ui_branch_never_sees_an_api_request()
    {
        // A route nobody mapped must answer as an API route. A client that asked for JSON and
        // was handed a document parses the document, fails inside its own code, and reports a
        // problem that has nothing to do with the real one.
        var spa = File.ReadAllText(Path.Combine(ApiRoot, "SpaHosting.cs"));

        Assert.Contains("StartsWithSegments(ApiSegment)", spa, StringComparison.Ordinal);
        Assert.Contains("public const string ApiSegment = \"/api\";", spa, StringComparison.Ordinal);

        // The framework's own fallback would catch /api too, because an unmatched API path has
        // no file extension and is therefore a "page" by its rule. So it is not used.
        Assert.DoesNotContain("MapFallbackToFile", spa, StringComparison.Ordinal);
        Assert.DoesNotContain("MapFallback", spa, StringComparison.Ordinal);

        // And the branch is added before any endpoint could answer.
        var host = File.ReadAllText(Path.Combine(ApiRoot, "HubApi.cs"));

        Assert.True(
            host.IndexOf("UseOperatorUi", StringComparison.Ordinal)
            < host.IndexOf("MapGroup(RoutePrefix)", StringComparison.Ordinal),
            "the UI branch must be added before the API group is mapped.");
    }

    [Fact]
    public void A_missing_file_never_becomes_a_page()
    {
        // A browser handed index.html in place of a missing script executes HTML as
        // JavaScript, and the real fault - a stale build, a mispublished asset - stays
        // invisible behind a syntax error somewhere in the middle of a document.
        var spa = File.ReadAllText(Path.Combine(ApiRoot, "SpaHosting.cs"));

        Assert.Contains("WantsAPage", spa, StringComparison.Ordinal);
        Assert.Contains("last.Contains('.')", spa, StringComparison.Ordinal);

        // Unknown types are not served at all rather than served for a browser to sniff.
        Assert.Contains("ServeUnknownFileTypes = false", spa, StringComparison.Ordinal);
    }

    [Fact]
    public void Serving_pages_does_not_move_the_host_off_loopback()
    {
        var options = File.ReadAllText(Path.Combine(ApiRoot, "HubApiOptions.cs"));
        var host = File.ReadAllText(Path.Combine(ApiRoot, "HubApi.cs"));
        var program = File.ReadAllText(Path.Combine(ApiRoot, "Program.cs"));

        // The binding is still checked, and there is still no way to express anything else.
        Assert.Contains("IsLoopback", options, StringComparison.Ordinal);
        Assert.Contains("options.Bind()", host, StringComparison.Ordinal);

        // The port is configurable so a taken default is not a wall. The address is not.
        Assert.Contains("COREX_HUB_LISTEN_URL", program, StringComparison.Ordinal);
        Assert.Contains("HubApiOptions.LoopbackUrl", program, StringComparison.Ordinal);

        foreach (var file in ApiSources())
        {
            // Commentary stripped: HubApiOptions explains in prose that it never binds
            // 0.0.0.0, and a guard that matched the sentence would fail on the promise it is
            // checking for.
            var code = CodeOnly(File.ReadAllText(file));

            foreach (var wildcard in new[] { "0.0.0.0", "http://*", "http://+", "AllowAnyOrigin" })
            {
                Assert.False(
                    code.Contains(wildcard, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}: {wildcard}");
            }
        }
    }

    [Fact]
    public void Same_origin_hosting_did_not_loosen_cross_origin()
    {
        // There is no production CORS requirement once the two share an origin, so the policy
        // that existed for the dev server is untouched rather than widened.
        var host = File.ReadAllText(Path.Combine(ApiRoot, "HubApi.cs"));

        Assert.Contains("WithOrigins", host, StringComparison.Ordinal);
        Assert.Contains("IsDevelopment()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin", host, StringComparison.Ordinal);

        Assert.Empty(new CoreX.Hub.Api.HubApiOptions().DevelopmentCorsOrigins);
    }

    [Fact]
    public void An_ordinary_dotnet_build_never_runs_npm()
    {
        // The two toolchains meet at publish and nowhere else. A restore that reached a package
        // registry every time someone compiled would make an offline build impossible.
        var project = File.ReadAllText(Path.Combine(ApiRoot, "CoreX.Hub.Api.csproj"));

        Assert.DoesNotMatch(
            new Regex(@"<Exec[^>]*Command=""[^""]*npm", RegexOptions.IgnoreCase),
            project);

        Assert.DoesNotContain("BeforeTargets=\"Build\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("AfterTargets=\"Build\"", project, StringComparison.Ordinal);

        // Publish is where it happens, and a publish with no build refuses rather than quietly
        // producing a host that answers every page with 404.
        Assert.Contains("BeforeTargets=\"ComputeFilesToPublish\"", project, StringComparison.Ordinal);
        Assert.Contains("<Error Condition=", project, StringComparison.Ordinal);

        // And no solution project references the frontend or carries its sources.
        foreach (var csproj in Directory.EnumerateFiles(RepoLayout.SourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);

            Assert.DoesNotContain("node_modules", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"<PackageReference[^>]*Include=""[^""]*(React|Node|Vite)"), text);
        }
    }

    [Fact]
    public void Nothing_generated_by_the_frontend_build_is_committed()
    {
        // dist/ and node_modules/ are build output and a package cache. Committing either
        // makes the repository the source of truth for something npm already owns, and makes a
        // stale bundle something a reviewer has to notice rather than something a build fixes.
        var web = Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Web");

        Assert.False(Directory.Exists(Path.Combine(web, "node_modules", ".package-lock.json"))
                     && IsTracked(Path.Combine(web, "node_modules")),
            "node_modules is committed.");

        var ignore = File.ReadAllText(Path.Combine(web, ".gitignore"));

        foreach (var generated in new[] { "node_modules/", "dist/", "e2e/.artifacts/" })
        {
            Assert.Contains(generated, ignore, StringComparison.Ordinal);
        }

        // The lock file is the one npm artifact that belongs here: it is what makes `npm ci`
        // reproduce a build rather than resolve one.
        Assert.True(File.Exists(Path.Combine(web, "package-lock.json")));
    }

    [Fact]
    public void The_browser_acceptance_never_reaches_around_the_ui()
    {
        // The whole point of a browser acceptance is that a person could have done it. A test
        // that posted a decision, wrote a row or shelled out to the CLI would be proving the
        // test harness rather than the product.
        var e2e = Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Web", "e2e");

        foreach (var file in Directory.EnumerateFiles(e2e, "*.spec.ts"))
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[]
                     {
                         "request.post", "request.put", "request.patch", "request.delete",
                         "hub.exe", "hub-api.exe", "HubCli", "sqlite", "hub.db",
                         "execFileSync", "spawnSync", "child_process",
                     })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(file)}: {forbidden}");
            }
        }

        // Only the environment is built outside the browser, and it seeds configuration only.
        var validation = File.ReadAllText(Path.Combine(e2e, "validation-hub.mjs"));

        Assert.Contains("hub.config.yaml", validation, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "INSERT INTO", "TaskState", "AcceptanceDecision", "ExecutionOutcome" })
        {
            Assert.False(
                validation.Contains(forbidden, StringComparison.Ordinal),
                $"validation-hub.mjs: {forbidden}");
        }
    }

    [Fact]
    public void The_validation_environment_names_no_real_provider()
    {
        // Phase 9 accepts the control plane, not provider quality. The validation Hub is
        // configured with the deterministic scripted runtime, and adding a Scripted profile to
        // the shipped configuration to make a UI test pass would be the opposite of that.
        var validation = File.ReadAllText(Path.Combine(
            RepoLayout.SourceRoot, "CoreX.Hub.Web", "e2e", "validation-hub.mjs"));

        Assert.Contains("provider: Scripted", validation, StringComparison.Ordinal);

        foreach (var provider in new[] { "ClaudeCode", "Codex", "claude-code", "codex-reviewer" })
        {
            Assert.False(
                validation.Contains(provider, StringComparison.Ordinal),
                $"validation-hub.mjs: {provider}");
        }

        // And the shipped configuration was not touched to make a UI test pass. The Hub's own
        // repository is still reviewed by the profile Phase 8B switched it to, and the fixture
        // organization's Scripted profiles are the ones that were already there.
        var shipped = File.ReadAllText(Path.Combine(RepoLayout.Root, "hub.config.yaml"));

        Assert.Contains("reviewerProfile: codex-reviewer", shipped, StringComparison.Ordinal);
        Assert.DoesNotContain("hub-e2e", shipped, StringComparison.Ordinal);
        Assert.DoesNotContain("taskKeyPrefix: E2E", shipped, StringComparison.Ordinal);
    }

    /// <summary>Source with its commentary removed, so a guard measures code.</summary>
    private static string CodeOnly(string source) =>
        string.Join(
            Environment.NewLine,
            source
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("/*", StringComparison.Ordinal)));

    /// <summary>Whether git tracks anything under a path.</summary>
    private static bool IsTracked(string path) => Directory.Exists(Path.Combine(path, ".git"));

    private static IEnumerable<string> ApiSources() =>
        Directory.EnumerateFiles(ApiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
