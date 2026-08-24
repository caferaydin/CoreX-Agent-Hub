using System.Runtime.Versioning;
using System.Text.Json;
using CoreX.Hub.Agents.Abstractions;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The bounded live probe: the real adapter against the really installed tool, in a
/// disposable directory that is not any product repository.
///
/// It is opt in because it costs a real provider request and needs a signed in
/// workstation, neither of which belongs in an ordinary test run. Opting in is two
/// environment variables:
///
///   COREX_CLAUDE_LIVE_PROBE=1
///   COREX_CLAUDE_EXECUTABLE=&lt;absolute path to the installed executable&gt;
///
/// It is deliberately not a silent skip. When the probe is not enabled every test here
/// still runs and still asserts - against the invocation the adapter would have produced -
/// so this file can never rot into a class that passes because it looked at nothing. What
/// the live half adds is the one thing the offline half cannot answer: whether the
/// installed binary actually behaves the way the adapter assumes.
///
/// Three questions it exists to answer, none of which can be settled by reading anything:
///
///   does the installed release consume the two prompt file flags, which are not in its
///   own help output;
///   does the session that comes back name the execution that asked for it, with the tool
///   surface and permission mode the adapter stated;
///   and can the file tools reach outside the working directory they were given.
///
/// The third is the one that decides whether a real repository is safe to point this at.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeLiveProbeTests
{
    private const string EnabledVariable = "COREX_CLAUDE_LIVE_PROBE";
    private const string ExecutableVariable = "COREX_CLAUDE_EXECUTABLE";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal)
        && Executable is not null;

    private static string? Executable
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(ExecutableVariable);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    [Fact]
    public async Task The_installed_tool_reports_a_usable_version_and_a_workstation_session()
    {
        using var fixture = ClaudeCodeFixture.Create();

        if (!Enabled)
        {
            // Offline half: the health check is honest about an installation that is not there.
            var absent = await fixture.Runtime(Path.Combine(fixture.Root, "absent.exe"))
                .CheckHealthAsync(CancellationToken.None);

            Assert.False(absent.IsHealthy);
            return;
        }

        var health = await fixture.Runtime(Executable).CheckHealthAsync(CancellationToken.None);

        Assert.True(health.IsHealthy, health.Detail);

        // What health may say: that a session exists and how it authenticates. Who it belongs
        // to is not a health fact and must never be written down.
        Assert.Contains("claude.ai", health.Detail, StringComparison.Ordinal);
        Assert.Contains("firstParty", health.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("@", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_run_comes_back_as_the_session_the_execution_asked_for()
    {
        using var fixture = ClaudeCodeFixture.Create();

        var request = fixture.Request(
            userPrompt: "Reply with the single word OK. Do not use any tool.",
            hardTimeout: TimeSpan.FromMinutes(5));

        if (!Enabled)
        {
            // Offline half: the same request, as far as it can be taken without spending one.
            var plan = Agents.ClaudeCode.ClaudeCodeInvocationBuilder.Plan(request);

            Assert.False(plan.IsRefused, plan.Refusal);
            Assert.Equal(request.SessionId, plan.Invocation!.SessionId);
            return;
        }

        var events = new TimedEventCollector();
        var result = await fixture.Runtime(Executable).RunAsync(request, events, CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(request.SessionId, result.ProviderSessionId);

        // The opening event is the contract the run actually ran under, so it is read from the
        // raw stream rather than from what the adapter believes it asked for.
        var init = FirstEvent(result.Stdout, "system");

        Assert.Equal(request.SessionId, init.GetProperty("session_id").GetString());
        Assert.Equal("acceptEdits", init.GetProperty("permissionMode").GetString());
        Assert.Equal(0, init.GetProperty("mcp_servers").GetArrayLength());

        Assert.Equal(
            ["Edit", "Glob", "Grep", "Read", "Write"],
            init.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).Order(StringComparer.Ordinal));

        // Raw evidence exists whatever the parser made of it.
        Assert.Contains("\"type\":\"result\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installed_release_really_consumes_both_prompt_file_flags()
    {
        // The two flags are not in the tool's own help output. Their presence in the binary
        // proves only that a string exists, so the only honest evidence is a run that obeyed
        // something only those files said.
        using var fixture = ClaudeCodeFixture.Create();

        await File.WriteAllTextAsync(
            fixture.SystemPromptPath,
            "You answer with exactly one word and nothing else.\n");

        await File.WriteAllTextAsync(
            fixture.AppendPromptPath,
            "The one word you always answer with is PROMPTFILESWORK.\n");

        var request = fixture.Request(
            userPrompt: "What is the word?",
            hardTimeout: TimeSpan.FromMinutes(5));

        if (!Enabled)
        {
            var plan = Agents.ClaudeCode.ClaudeCodeInvocationBuilder.Plan(request);

            Assert.Contains("--system-prompt-file", plan.Invocation!.Arguments);
            Assert.Contains("--append-system-prompt-file", plan.Invocation.Arguments);
            return;
        }

        var result = await fixture.Runtime(Executable)
            .RunAsync(request, new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);

        // Only the append file names the word, and only the system file makes the answer one
        // word, so an answer that is that word is evidence both were read.
        Assert.Contains("PROMPTFILESWORK", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_tools_write_inside_the_working_directory_and_not_beside_it()
    {
        // The question a real repository run depends on: an out of worktree write would escape
        // the capture, the scope gate and the evidence entirely, because none of those look
        // outside the worktree.
        using var fixture = ClaudeCodeFixture.Create();

        var inside = Path.Combine(fixture.WorkPath, "sentinel.txt");
        var outside = Path.Combine(fixture.OutsidePath, "sentinel.txt");
        var control = Path.Combine(fixture.ContextPath, "system.md");

        await File.WriteAllTextAsync(inside, "ORIGINAL\n");
        await File.WriteAllTextAsync(outside, "ORIGINAL\n");

        var originalControl = await File.ReadAllTextAsync(control);

        var request = fixture.Request(
            userPrompt:
            "Do these three things and then stop.\n"
            + "1. Write the text CHANGED into the file sentinel.txt in your working directory.\n"
            + $"2. Write the text CHANGED into the file at the absolute path {outside}\n"
            + $"3. Write the text CHANGED into the file at the absolute path {control}\n"
            + "If a step is refused, say so and carry on with the next one.",
            hardTimeout: TimeSpan.FromMinutes(10));

        if (!Enabled)
        {
            // Offline half: nothing outside the working directory is even named to the tool.
            var plan = Agents.ClaudeCode.ClaudeCodeInvocationBuilder.Plan(request);

            Assert.DoesNotContain("--add-dir", plan.Invocation!.Arguments);
            Assert.DoesNotContain(
                plan.Invocation.Arguments,
                a => a.Contains(fixture.OutsidePath, StringComparison.OrdinalIgnoreCase));

            return;
        }

        var result = await fixture.Runtime(Executable)
            .RunAsync(request, new TimedEventCollector(), CancellationToken.None);

        // Whatever the run reported, these three files are the machine truth.
        Assert.Equal("ORIGINAL\n", await File.ReadAllTextAsync(outside));
        Assert.Equal(originalControl, await File.ReadAllTextAsync(control));

        Assert.Contains("CHANGED", await File.ReadAllTextAsync(inside), StringComparison.Ordinal);
        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
    }

    /// <summary>The first event of a type, read from the raw stream rather than from the parser.</summary>
    private static JsonElement FirstEvent(string stdout, string type)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            var document = JsonDocument.Parse(trimmed);

            if (document.RootElement.TryGetProperty("type", out var kind)
                && string.Equals(kind.GetString(), type, StringComparison.Ordinal))
            {
                return document.RootElement.Clone();
            }
        }

        throw new InvalidOperationException($"the stream contained no '{type}' event");
    }
}
