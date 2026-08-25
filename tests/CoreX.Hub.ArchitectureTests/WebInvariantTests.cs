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
    public void The_ui_can_change_exactly_the_eight_things_it_is_allowed_to()
    {
        // Phase 9B forbade every verb but GET; Phase 9C replaced that with a list of four.
        // The property is the same one and the list has grown: the UI changes these eight
        // things and no others. Every request goes through one client, so the list stays
        // checkable rather than merely intended.
        var client = File.ReadAllText(Path.Combine(WebRoot, "src", "api", "client.ts"));

        Assert.Equal(
            [
                "acceptTask", "cancelTask", "createTask", "markTaskReady",
                "overrideReviewBlocked", "rejectTask", "requestChanges", "runTask",
            ],
            MutationMethods(client));

        // POST is the only verb the Hub's mutation surface uses, so anything else here would
        // be a shape the API does not have.
        Assert.DoesNotMatch(
            new Regex(@"method\s*:\s*['QQ'](PUT|PATCH|DELETE)['QQ']", RegexOptions.IgnoreCase),
            client);

        // Non vacuous: it really is the client, and it really does call the Hub.
        Assert.Contains("/api/v1", client, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?<![\w.])fetch\s*\("), client);

        // And no component reaches around it with its own request.
        foreach (var file in WebSources().Where(f => !f.EndsWith("client.ts", StringComparison.Ordinal)))
        {
            if (file.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                // Tests stub fetch on purpose; that is how they prove the client's behaviour.
                continue;
            }

            // The global, not any identifier that happens to end in it - `refetch()` is this
            // UI reloading a query, which is the opposite of reaching around the client.
            Assert.DoesNotMatch(new Regex(@"(?<![\w.])fetch\s*\("), File.ReadAllText(file));
        }
    }

    [Fact]
    public void The_four_human_decisions_are_four_calls_and_never_one_generic_one()
    {
        // Accept, override, request-changes and reject have different prerequisites and leave
        // different records. One call that took the verb as a parameter would make them look
        // interchangeable to every caller, and the day one was passed the wrong string the UI
        // would have recorded a decision nobody made.
        var client = CodeOnly(File.ReadAllText(Path.Combine(WebRoot, "src", "api", "client.ts")));

        // Each route is built in exactly one place. The trailing backtick is what keeps these
        // apart: /accept is a prefix of /accept-override, and /acceptance is the read route.
        foreach (var route in new[] { "/accept`", "/accept-override`", "/request-changes`", "/reject`" })
        {
            Assert.Equal(1, Occurrences(client, route));
        }

        // No verb-as-parameter, and no state-shaped endpoint to send one to. The words
        // rather than the call shapes: a generic method would be declared
        // `transition: (path, body) =>`, which a search for `transition(` would miss.
        foreach (var generic in new[]
                 {
                     "transition", "/transition", "/decision", "/state",
                     "applyDecision", "patchState", "decide",
                 })
        {
            Assert.False(client.Contains(generic, StringComparison.Ordinal), generic);
        }
    }

    [Fact]
    public void An_override_carries_a_reason_or_it_cannot_be_built()
    {
        // Structural rather than a matter of remembering: an ordinary accept has nowhere to
        // put an override reason, and an override cannot be constructed without one.
        var contracts = File.ReadAllText(Path.Combine(WebRoot, "src", "api", "contracts.ts"));

        Assert.Matches(
            new Regex(@"interface OverrideRequest \{[^}]*readonly reason: string;", RegexOptions.Singleline),
            contracts);

        Assert.DoesNotMatch(
            new Regex(@"interface AcceptRequest \{[^}]*reason", RegexOptions.Singleline),
            contracts);

        // And the two go to two different routes, which the call-site guard above pins down.
        Assert.Contains("OverrideRequest",
            File.ReadAllText(Path.Combine(WebRoot, "src", "api", "client.ts")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Which_actions_the_ui_offers_is_not_a_second_state_machine()
    {
        // The affordance rules look at what the server already said - a task's state, and for
        // the override an execution's outcome - and at nothing else. A frontend that
        // reproduced the real conditions would be a second copy of the state machine, and the
        // day the two disagreed the UI would be confidently wrong.
        var rules = CodeOnly(File.ReadAllText(Path.Combine(WebRoot, "src", "lib", "actions.ts")));

        // Read off the signatures rather than from a word list, because the words that would
        // need forbidding - "outcome", "override" - are also the names of two things these
        // rules legitimately are. What they may take is two enums the server sent.
        Assert.Equal(
            ["ExecutionOutcome", "TaskState", "boolean"],
            Regex
                .Matches(rules, @":\s*(?<type>[A-Za-z]+)")
                .Select(m => m.Groups["type"].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

        // So nothing here can reach the data behind those two words.
        foreach (var forbidden in new[]
                 {
                     "readyGate", "attempts", "criteria", "verification",
                     "findings", "machineTruth", "Threshold", "prerequisite",
                 })
        {
            Assert.False(rules.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }

        // Four lifecycle affordances, four governance ones, and one question about whether a
        // task is finished with. Nothing else.
        Assert.Equal(
            [
                "canAccept", "canCancel", "canMarkReady", "canOfferOverride",
                "canReject", "canRequestChanges", "canRun", "isTerminalTask",
            ],
            Exported(rules));
    }

    [Fact]
    public void The_ui_keeps_no_copy_of_the_acceptance_rules()
    {
        // AcceptanceService decides whether an attempt may be accepted and ReviewFindingGate
        // decides what blocks. Both are single authorities. A React form that re-derived
        // either would be a second one, and the day they disagreed an operator would be shown
        // a decision the Hub never made.
        var governance = Governance();

        foreach (var forbidden in new[]
                 {
                     "majorThreshold", "MajorThreshold", "blocks", "inScopeCountsBySeverity",
                     "prerequisite", "canBeAccepted", "isAcceptable", "wouldBeRefused",
                 })
        {
            Assert.False(governance.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }

        // What it does instead: it sends, and it shows whatever came back.
        Assert.Contains("acceptTask", governance, StringComparison.Ordinal);
        Assert.Contains("ActionError", governance, StringComparison.Ordinal);
    }

    [Fact]
    public void Machine_truth_is_read_and_a_finding_is_never_edited()
    {
        // The boundary this panel exists to hold. A person supplies verdicts on Human criteria
        // and, for an override, a reason. Everything the machine established - verification
        // verdicts, findings, severities, the Hub's own scope computation - is shown and
        // cannot be touched from here.
        var governance = Governance();

        foreach (var forbidden in new[]
                 {
                     "dismissFinding", "falsePositive", "setSeverity", "setFindings",
                     "outOfScope =", "severity =", "editRationale", "setVerificationStatus",
                 })
        {
            Assert.False(governance.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }

        // The two read-only sections have no control in them at all.
        foreach (var section in new[]
                 {
                     Between(governance, "function MachineCriteria(", "function HumanCriteria("),
                     Between(governance, "function Findings(", "function OverrideReason("),
                 })
        {
            foreach (var control in new[] { "<input", "<select", "<textarea", "<button", "onChange" })
            {
                Assert.False(section.Contains(control, StringComparison.Ordinal), control);
            }
        }
    }

    [Fact]
    public void A_human_verdict_is_never_answered_in_advance()
    {
        // A form that arrived pre-answered would decide for the operator and then record their
        // name against it. Every criterion starts unanswered, and there is no stock wording for
        // an override reason either - a prefilled "false positive" is a justification the Hub
        // would attribute to someone who never wrote it.
        var governance = Governance();

        Assert.Contains("useState<ReadonlyMap<string, HumanVerdict>>(new Map())", governance,
            StringComparison.Ordinal);
        Assert.Contains("useState('')", governance, StringComparison.Ordinal);

        foreach (var prefill in new[]
                 {
                     "defaultVerdict", "?? 'Met'", "|| 'Met'", "useState('Met')",
                     "Approved", "False positive", "Known issue", "defaultValue",
                 })
        {
            Assert.False(governance.Contains(prefill, StringComparison.Ordinal), prefill);
        }
    }

    [Fact]
    public void A_governance_decision_is_never_sent_twice_by_the_ui_itself()
    {
        // A mutation that failed for an unknown reason may or may not have happened. Sending it
        // again is how one click becomes two decisions, so nothing on this path retries and the
        // operator is the one who decides whether to try again.
        foreach (var name in new[] { "useMutation.ts", "client.ts", "TaskGovernance.tsx" })
        {
            var code = CodeOnly(File.ReadAllText(WebSources().Single(f =>
                f.EndsWith(Path.DirectorySeparatorChar + name, StringComparison.Ordinal))));

            foreach (var forbidden in new[] { "retry", "setTimeout", "setInterval", "backoff" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void A_decision_never_becomes_a_second_live_channel()
    {
        // Governance is request and answer. There is no acceptance stream, and no way for one
        // browser tab to tell another that a decision was made - the Hub is the only thing
        // that knows, and the page asks it.
        var governance = Governance();

        foreach (var forbidden in new[]
                 {
                     "EventSource", "BroadcastChannel", "WebSocket", "useExecutionStream",
                     "localStorage", "sessionStorage",
                 })
        {
            Assert.False(governance.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }
    }

    [Fact]
    public void An_action_never_names_a_provider_or_a_runtime_setting()
    {
        // Every action works the same whichever provider is configured, and that now includes
        // deciding. The operator names a task and, at run time, an implementer profile - never
        // a runtime, a permission profile or a sandbox, and never when accepting.
        foreach (var file in WebSources().Where(f =>
                     f.EndsWith("TaskActions.tsx", StringComparison.Ordinal)
                     || f.EndsWith("TaskGovernance.tsx", StringComparison.Ordinal)
                     || f.EndsWith("CreateTaskPage.tsx", StringComparison.Ordinal)))
        {
            var code = CodeOnly(File.ReadAllText(file));

            foreach (var forbidden in new[]
                     {
                         "Claude", "Codex", "runtimeKey", "permissionProfile",
                         "sandbox", "executablePath", "reviewerProfile",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{Name(file)}: {forbidden}");
            }
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

    /// <summary>The names of the client's state-changing methods, in order.</summary>
    private static IReadOnlyList<string> MutationMethods(string client) =>
    [
        .. Regex
            .Matches(client, @"(?<name>[a-zA-Z]+):\s*\([^)]*\)\s*=>\s*send<")
            .Select(m => m.Groups["name"].Value)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Every exported binding in a module, in order.</summary>
    private static IReadOnlyList<string> Exported(string module) =>
    [
        .. Regex
            .Matches(module, @"export const (?<name>[a-zA-Z]+)")
            .Select(m => m.Groups["name"].Value)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The governance panel's code, with its commentary removed.</summary>
    private static string Governance() =>
        CodeOnly(File.ReadAllText(Path.Combine(WebRoot, "src", "routes", "TaskGovernance.tsx")));

    /// <summary>How many times a literal appears.</summary>
    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>One named region of a module, so a guard can read a single function's body.</summary>
    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        var end = text.IndexOf(to, StringComparison.Ordinal);

        Assert.True(start >= 0, from);
        Assert.True(end > start, to);

        return text[start..end];
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

    private static string Name(string path) => Path.GetFileName(path);
}
