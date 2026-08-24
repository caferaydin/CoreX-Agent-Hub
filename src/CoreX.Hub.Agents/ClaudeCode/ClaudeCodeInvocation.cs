using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Agents.ClaudeCode;

/// <summary>
/// One fully decided child invocation: the argument vector, the names the child may
/// inherit from this process, and the prompt that goes down its stdin.
///
/// It is a value rather than a method on the runtime so that the entire command surface
/// can be asserted without starting anything. What the tests read is what the operating
/// system gets.
/// </summary>
public sealed record ClaudeCodeInvocation
{
    public required IReadOnlyList<string> Arguments { get; init; }

    public required IReadOnlyList<string> EnvironmentAllowlist { get; init; }

    /// <summary>The session identifier that was requested, and that the stream must echo.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The task prompt. It travels on stdin, never in <see cref="Arguments"/>: a command
    /// line is readable by any process on the machine, and the whole task description would
    /// otherwise be in it.
    /// </summary>
    public required string StandardInputText { get; init; }
}

/// <summary>
/// Either an invocation or the reason there is not one.
///
/// A refusal is produced before anything is launched. That is the point: a request asking
/// for something this adapter does not implement has to be answered honestly and cheaply,
/// not by starting a run that quietly does something else.
/// </summary>
public sealed record ClaudeCodeInvocationPlan
{
    private ClaudeCodeInvocationPlan()
    {
    }

    public ClaudeCodeInvocation? Invocation { get; private init; }

    public string? Refusal { get; private init; }

    public bool IsRefused => Refusal is not null;

    public static ClaudeCodeInvocationPlan Ready(ClaudeCodeInvocation invocation) =>
        new() { Invocation = invocation };

    public static ClaudeCodeInvocationPlan Refused(string reason) => new() { Refusal = reason };
}

/// <summary>
/// Turns an <see cref="Abstractions.AgentRunRequest"/> into the exact command this adapter
/// runs, or refuses it.
///
/// Everything here is a fixed decision rather than a setting. The tool surface is frozen
/// by ADR-0020 and has no configuration path at all; the permission mode is stated
/// explicitly because the user settings this run deliberately loads carry a default that
/// would otherwise decide it; session persistence is off because resume is not
/// implemented, and saying so is cheaper than half implementing it.
///
/// Model and effort are the opposite: they are catalog facts and are never defaulted here.
/// A run whose profile does not say which model, or says an effort this tool has no exact
/// word for, is refused rather than quietly run as something else.
/// </summary>
public static class ClaudeCodeInvocationBuilder
{
    /// <summary>
    /// The frozen native Windows tool surface (ADR-0020).
    ///
    /// There is no shell here and there is no way to configure one in. Building and running
    /// tests is not the agent's job - deterministic verification already does it, with
    /// better evidence - so removing the shell removes a capability that was already
    /// available somewhere safer.
    ///
    /// This list is a constant on purpose. A configurable tool list would let a
    /// deployment put the shell back through a string in a file, which is exactly the
    /// decision ADR-0020 took away.
    /// </summary>
    public static IReadOnlyList<string> Tools { get; } = ["Read", "Edit", "Write", "Glob", "Grep"];

    /// <summary>
    /// The reviewer tool surface: reading only, and no way to change anything.
    ///
    /// A reviewer that could edit could launder its own verdict, so the restriction is not a
    /// convenience. It is defence in depth rather than the boundary - the boundary is that
    /// the snapshot's tree is compared before and after - but a capability that does not
    /// exist cannot be misused, and this list is a constant for the same reason the
    /// implementer's is.
    /// </summary>
    public static IReadOnlyList<string> ReviewerTools { get; } = ["Read", "Glob", "Grep"];

    /// <summary>
    /// The tool the provider adds by itself when a schema is requested.
    ///
    /// Measured, not assumed: with --json-schema the init event reports this alongside the
    /// tools that were asked for. It is how structured output is returned at all, so a
    /// runtime that treated an unrequested tool as a breach would refuse every structured
    /// review; and one that accepted any unrequested tool would not notice a shell.
    /// </summary>
    public const string StructuredOutputTool = "StructuredOutput";

    /// <summary>The permission mode this adapter states for an implementer run.</summary>
    public const string PermissionMode = "acceptEdits";

    /// <summary>
    /// The permission mode a read only run states.
    ///
    /// Plan mode is what the provider offers for "look, do not act". It is stated explicitly
    /// for exactly the reason the implementer's mode is: the user settings this run loads
    /// carry a default, and a mode nobody stated is a mode somebody else chose.
    /// </summary>
    public const string ReviewerPermissionMode = "plan";

    /// <summary>The only setting source this adapter loads. Project and local are never read.</summary>
    public const string SettingSources = "user";

    public const string OutputFormat = "stream-json";

    /// <summary>
    /// The widest environment this runtime will let a child inherit.
    ///
    /// The child environment starts empty (ADR-0007) and the organization says what may go
    /// back into it. That statement covers every managed child, not just this one - the same
    /// list has to be enough for a .NET verification toolchain - so this is the adapter's own
    /// ceiling within it, and a name the organization grants that is not here is simply not
    /// passed on. The child ends up with the intersection, which is never more than either
    /// side allowed and is usually less.
    ///
    /// Widening what an agent child can see stays an adapter decision: adding to this list is
    /// a code change, and no configuration can reach past it.
    /// </summary>
    public static IReadOnlyList<string> PermittedEnvironmentNames { get; } =
        ["SystemRoot", "windir", "SystemDrive", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE"];

    /// <summary>
    /// The names without which this runtime cannot work at all.
    ///
    /// Windows needs its own root, the tool needs somewhere temporary to write, and the
    /// workstation session it authenticates with lives under the user profile. A run that
    /// does not grant these is refused now rather than failing later as an authentication
    /// problem nobody configured.
    /// </summary>
    public static IReadOnlyList<string> RequiredEnvironmentNames { get; } =
        ["SystemRoot", "TEMP", "TMP", "USERPROFILE"];

    /// <summary>
    /// Name prefixes the child must never inherit a parent value for.
    ///
    /// None of them can reach the child through the allowlist above, which is a closed set
    /// that contains none of them. This is the second, explicit statement of the same fact:
    /// a request that asks for one of these is refused by name, so the intent is recorded
    /// as a refusal rather than silently having no effect.
    ///
    /// Two families and two reasons. Provider credentials and endpoints would change who
    /// pays for the run and where it goes; a parent tool session would make the child a
    /// continuation of this process instead of an independent run.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenEnvironmentPrefixes { get; } =
        ["ANTHROPIC_", "CLAUDE_", "CLAUDECODE", "AWS_", "GOOGLE_", "AZURE_", "GH_", "GITHUB_"];

    /// <summary>Credential helper hooks, which are neutralised rather than inherited.</summary>
    public static IReadOnlyList<string> ForbiddenEnvironmentNames { get; } = ["SSH_ASKPASS", "GIT_ASKPASS"];

    /// <summary>
    /// The exact effort words this tool accepts. There is no nearest match: an effort the
    /// catalog states that is not one of these is refused, because guessing which one was
    /// meant would make the run something nobody asked for.
    /// </summary>
    public static IReadOnlyList<string> EffortLevels { get; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Which of the two shapes a request asks for.
    ///
    /// The permission profile decides it, and nothing else does. There is no mode flag and
    /// no second runtime: an implementer is a run that may write, a reviewer is a run that
    /// may not, and the catalog already says which one this is.
    /// </summary>
    public static bool IsReviewerRequest(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.ReadOnly;

    /// <summary>The tools a request asks the provider for, before the provider adds its own.</summary>
    public static IReadOnlyList<string> RequestedToolsFor(AgentPermissionProfile profile) =>
        IsReviewerRequest(profile) ? ReviewerTools : Tools;

    public static string PermissionModeFor(AgentPermissionProfile profile) =>
        IsReviewerRequest(profile) ? ReviewerPermissionMode : PermissionMode;

    /// <summary>
    /// The tools the provider is expected to report once it has started.
    ///
    /// It is the requested set plus, in schema mode only, the tool the provider adds to
    /// return structured output. Anything else - and in particular anything that can write
    /// or execute - means the run is not the run that was asked for.
    /// </summary>
    public static IReadOnlyList<string> ExpectedEffectiveTools(
        AgentPermissionProfile profile,
        bool structuredSchemaRequested)
    {
        var requested = RequestedToolsFor(profile);

        return structuredSchemaRequested ? [.. requested, StructuredOutputTool] : requested;
    }

    public static bool IsForbiddenEnvironmentName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && (ForbiddenEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || ForbiddenEnvironmentPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Decides the invocation, or says why there is not one.
    ///
    /// Pure: it touches no filesystem and starts nothing, so the whole command surface is
    /// assertable and every refusal below happens before a process could exist.
    /// </summary>
    public static ClaudeCodeInvocationPlan Plan(Abstractions.AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Refuse(request) is { } refusal)
        {
            return ClaudeCodeInvocationPlan.Refused(refusal);
        }

        var effort = EffortLevels.First(l => string.Equals(l, request.EffortHint!.Trim(), StringComparison.OrdinalIgnoreCase));
        var sessionId = request.SessionId!.Trim();

        List<string> arguments =
        [
            "-p",
            "--output-format", OutputFormat,

            // 2.1.241 refuses --print with stream-json unless this is present.
            "--verbose",

            "--session-id", sessionId,
            "--no-session-persistence",
            "--safe-mode",
            "--setting-sources", SettingSources,
            "--strict-mcp-config",
            "--tools", string.Join(',', RequestedToolsFor(request.PermissionProfile)),
            "--permission-mode", PermissionModeFor(request.PermissionProfile),
            "--model", request.ModelHint!.Trim(),
            "--effort", effort,
            "--system-prompt-file", request.SystemPromptFile,
        ];

        if (!string.IsNullOrWhiteSpace(request.AppendSystemPromptFile))
        {
            arguments.Add("--append-system-prompt-file");
            arguments.Add(request.AppendSystemPromptFile);
        }

        if (!string.IsNullOrWhiteSpace(request.StructuredOutputSchema))
        {
            arguments.Add("--json-schema");
            arguments.Add(request.StructuredOutputSchema);
        }

        return ClaudeCodeInvocationPlan.Ready(new ClaudeCodeInvocation
        {
            Arguments = arguments,

            // What the organization granted, narrowed to what this runtime is willing to
            // pass on. Never more than the caller allowed and never more than the adapter
            // permits, so a name that only the verification toolchain needs stops here.
            EnvironmentAllowlist =
            [
                .. request.EnvironmentAllowlist
                    .Where(n => PermittedEnvironmentNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ],
            SessionId = sessionId,
            StandardInputText = request.UserPrompt,
        });
    }

    /// <summary>
    /// Everything this adapter will not do, checked in one place and before anything runs.
    ///
    /// Each of these has an honest alternative to being ignored. Silently dropping a resume
    /// identifier would produce a fresh session the caller believed was a continuation;
    /// silently dropping a schema would produce a document nothing validated; silently
    /// running a read only profile with edits enabled would be the opposite of what was
    /// asked for.
    /// </summary>
    private static string? Refuse(Abstractions.AgentRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            return "unsupported: this runtime runs without session persistence and implements no resume, "
                   + "so a resume identifier cannot be honoured.";
        }

        // A schema is supported; writing the result to a path of the caller's choosing is
        // not. The provider returns structured output in its own result, so honouring a path
        // would mean this runtime inventing a file - and inventing it inside the very tree a
        // review is measuring. Declaring schema support does not imply arbitrary output-path
        // support, so the narrower feature is refused by name.
        if (!string.IsNullOrWhiteSpace(request.StructuredOutputPath))
        {
            return "unsupported: this runtime returns structured output on the provider result and writes "
                   + "no structured document, so nothing would ever be written to the requested path.";
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return "the run supplied no session identifier; this runtime correlates the provider session "
                   + "with the execution and will not invent one.";
        }

        if (!Guid.TryParseExact(request.SessionId.Trim(), "D", out _))
        {
            return $"the session identifier '{request.SessionId}' is not a canonical uuid, which is the only "
                   + "form the provider accepts.";
        }

        if (string.IsNullOrWhiteSpace(request.ModelHint))
        {
            return "the agent profile names no model; this runtime states the model explicitly and never "
                   + "accepts a provider default.";
        }

        if (string.IsNullOrWhiteSpace(request.EffortHint))
        {
            return "the agent profile names no effort level; this runtime states it explicitly.";
        }

        if (!EffortLevels.Contains(request.EffortHint.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return $"the agent profile names effort '{request.EffortHint}', which is not one of: "
                   + string.Join(", ", EffortLevels);
        }

        if (string.IsNullOrWhiteSpace(request.SystemPromptFile))
        {
            return "the run supplied no system prompt file; the Hub execution contract is not optional.";
        }

        if (string.IsNullOrWhiteSpace(request.UserPrompt))
        {
            return "the run supplied no prompt.";
        }

        foreach (var name in request.EnvironmentAllowlist)
        {
            if (IsForbiddenEnvironmentName(name))
            {
                return $"the run asks the child to inherit '{name}', which would hand it provider "
                       + "credentials, a provider endpoint or this process's own session.";
            }

            // A name this runtime has no use for is not an error. The organization's list
            // covers every managed child, including a verification toolchain with needs of
            // its own, and one it does not share is narrowed away above rather than
            // refused - the child sees less, which is the safe direction.
        }

        var missing = RequiredEnvironmentNames
            .Where(n => !request.EnvironmentAllowlist.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
        {
            return $"the run does not grant the child {string.Join(", ", missing)}, without which this "
                   + "runtime cannot start or cannot reach its workstation session.";
        }

        return null;
    }
}
