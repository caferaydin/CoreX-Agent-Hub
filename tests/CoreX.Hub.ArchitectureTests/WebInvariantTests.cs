using System.Text.RegularExpressions;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What a frontend is allowed to be, and what it must not have leaked into.
///
/// Two directions worth guarding, and they are different risks.
///
/// Outward: the .NET side must not learn that a browser exists. Phase 9A already put HTTP in
/// one project; a React build must not now put npm into MSBuild or a node_modules tree into a
/// C# project's sources.
///
/// Inward: the UI is read-only in Phase 9B, and "read-only" has to be a fact about the code
/// rather than a promise about the components. The API client is the one place a request can
/// be made from, so a mutation would have to appear there - which makes it checkable.
/// </summary>
public sealed class WebInvariantTests
{
    private static string WebRoot => Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Web");

    [Fact]
    public void The_dotnet_solution_does_not_know_the_frontend_exists()
    {
        // No csproj anywhere, so `dotnet build` neither sees it nor needs node installed.
        Assert.Empty(Directory.EnumerateFiles(WebRoot, "*.csproj", SearchOption.AllDirectories));

        var solution = File.ReadAllText(Path.Combine(RepoLayout.Root, "CoreX.Hub.slnx"));

        Assert.DoesNotContain("CoreX.Hub.Web", solution, StringComparison.Ordinal);

        // And nothing injects npm into the build, which would make a .NET build fail on a
        // machine that has no node and would tie two toolchains together for no gain.
        foreach (var file in Directory.EnumerateFiles(RepoLayout.SourceRoot, "*.csproj", SearchOption.AllDirectories)
                     .Concat([Path.Combine(RepoLayout.Root, "Directory.Build.props")]))
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[] { "npm ", "npx ", "node ", "vite" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{file}: {forbidden}");
            }
        }
    }

    [Fact]
    public void No_backend_project_carries_frontend_sources()
    {
        // A stray .tsx under a C# project would compile into nothing and confuse every tool
        // that walks the tree, including the guards in this file.
        var strays = RepoLayout.ProductionSourceFiles
            .Where(f => f.EndsWith(".tsx", StringComparison.Ordinal) || f.EndsWith(".jsx", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(strays);

        foreach (var assembly in ArchitectureAssemblies.All)
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

            Assert.DoesNotContain(references, r => r.Contains("React", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(references, r => r.Contains("Node", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_ui_cannot_change_hub_state_in_this_phase()
    {
        // Every request the UI makes goes through one client, so a mutation would have to be
        // written here. Asserted as the absence of a verb rather than the presence of a
        // comment: adding one has to break this.
        var client = File.ReadAllText(Path.Combine(WebRoot, "src", "api", "client.ts"));

        // Any verb other than a read. `fetch` defaults to GET, so a mutation has to say so.
        Assert.DoesNotMatch(
            new Regex(@"method\s*:\s*['""](POST|PUT|PATCH|DELETE)['""]", RegexOptions.IgnoreCase),
            client);

        // And the mutation routes by name, matched where a path ends, so that reading
        // `/attempts/1/acceptance` is not mistaken for posting `/attempts/1/accept`.
        Assert.DoesNotMatch(
            new Regex(@"/(cancel|accept|accept-override|reject|runs|ready|request-changes)[`'""]", RegexOptions.CultureInvariant),
            client);

        // Non vacuous: it really is the client, and it really does call the Hub.
        Assert.Contains("/api/v1", client, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?<![\w.])fetch\s*\("), client);

        // And no component reaches around it with its own request.
        foreach (var file in WebSources().Where(f => !f.EndsWith("client.ts", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);

            if (file.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                // Tests stub fetch on purpose; that is how they prove the client's behaviour.
                continue;
            }

            // The global, not any identifier that happens to end in it - `refetch()` is
            // this UI reloading a query, which is the opposite of reaching around the client.
            Assert.DoesNotMatch(new Regex(@"(?<![\w.])fetch\s*\("), text);
        }
    }

    [Fact]
    public void Nothing_in_the_ui_renders_markup_it_was_given()
    {
        // A diff, a finding rationale and a step reason are all content that passed through an
        // agent. Rendered as text, always - React does that by default, and these are the ways
        // that default gets bypassed.
        foreach (var file in WebSources())
        {
            // Comments stripped: the diff panel explains by name that it never does this, and
            // a guard that matched prose would fail on the sentence promising the behaviour it
            // is checking for.
            var code = CodeOnly(File.ReadAllText(file));

            foreach (var forbidden in new[]
                     {
                         "dangerouslySetInnerHTML", "innerHTML", "outerHTML",
                         "eval(", "new Function(", "document.write",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{Name(file)}: {forbidden}");
            }
        }
    }

    [Fact]
    public void No_screen_branches_on_which_provider_ran()
    {
        // The Phase 8 property, carried into the UI: a task and an execution render the same
        // way whichever provider produced them. The word may appear as a value in a test
        // fixture - that is a provider being displayed, which is allowed - but never in a
        // condition in a component.
        var branching = new Regex(
            @"(===|!==|==|!=)\s*['""](ClaudeCode|Codex|Claude|Gemini|Anthropic|OpenAI)['""]"
            + @"|['""](ClaudeCode|Codex|Claude|Gemini|Anthropic|OpenAI)['""]\s*(===|!==|==|!=)",
            RegexOptions.CultureInvariant);

        foreach (var file in WebSources())
        {
            Assert.DoesNotMatch(branching, File.ReadAllText(file));
        }

        // And no runtime implementation type is named anywhere in the UI at all.
        foreach (var file in WebSources().Where(f => !f.Contains("fixtures", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[] { "ClaudeCodeRuntime", "CodexRuntime", "GeminiRuntime" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Name(file)}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_api_host_is_never_hard_coded_to_a_network_address()
    {
        foreach (var file in WebSources())
        {
            var text = File.ReadAllText(file);

            // A LAN address baked into a build would make the UI reach a Hub it does not own,
            // and would survive being copied to another machine.
            Assert.DoesNotMatch(
                new Regex(@"https?://(?!127\.0\.0\.1|localhost)[a-z0-9.\-]+", RegexOptions.IgnoreCase),
                text);
        }

        // The one loopback address that does appear is the dev server's proxy target, which is
        // configuration for a developer's own machine rather than something the app reads.
        var vite = File.ReadAllText(Path.Combine(WebRoot, "vite.config.ts"));

        Assert.Contains("127.0.0.1", vite, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", vite, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ui_stores_no_credential_because_there_is_none_to_store()
    {
        foreach (var file in WebSources())
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[]
                     {
                         "localStorage", "sessionStorage", "document.cookie",
                         "Authorization:", "Bearer ", "apiKey", "access_token",
                     })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Name(file)}: {forbidden}");
            }
        }
    }

    [Fact]
    public void There_is_one_frontend_and_it_is_where_the_convention_puts_it()
    {
        // One app. No workspace, no component package, no second bundler pretending to be a
        // design system.
        var manifests = Directory
            .EnumerateFiles(RepoLayout.Root, "package.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(f => RepoLayout.Relative(f))
            .ToList();

        Assert.Equal([Path.Combine("src", "CoreX.Hub.Web", "package.json")], manifests);

        // The lockfile is committed, so a build is reproducible.
        Assert.True(File.Exists(Path.Combine(WebRoot, "package-lock.json")));
    }

    [Fact]
    public void No_later_phase_work_arrived_with_the_ui()
    {
        foreach (var file in WebSources())
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[] { "RepairLoop", "repair", "WSL", "hardIsolation" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{Name(file)}: {forbidden}");
            }
        }
    }

    /// <summary>Every hand-written frontend source. Never the installed packages or the build.</summary>
    private static IEnumerable<string> WebSources() =>
        Directory
            .EnumerateFiles(Path.Combine(WebRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f =>
                f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

    /// <summary>Source with its commentary removed, so a guard measures code.</summary>
    private static string CodeOnly(string source) =>
        string.Join(
            Environment.NewLine,
            source
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("/*", StringComparison.Ordinal)));

    private static string Name(string path) => Path.GetFileName(path);
}
