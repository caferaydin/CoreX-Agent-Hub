using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Agents.Abstractions;

/// <summary>
/// What a runtime is asked to do.
///
/// Deliberately absent: provider identity, access mode, credentials, subscription,
/// billing account, base URL and endpoint. Which runtime handles this request, how it
/// authenticates and who pays for it are decided before RunAsync is called, by runtime
/// resolution in the composition layer. A runtime that needed any of those here would
/// have dragged provider specifics into the shared contract.
/// </summary>
public sealed record AgentRunRequest
{
    /// <summary>The workspace the agent may work in. Nothing outside it is in scope.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Path to the immutable Hub execution contract inside the context pack.</summary>
    public required string SystemPromptFile { get; init; }

    /// <summary>Path to the project rules that layer on top of it, when there are any.</summary>
    public string? AppendSystemPromptFile { get; init; }

    public required string UserPrompt { get; init; }

    public required AgentPermissionProfile PermissionProfile { get; init; }

    /// <summary>A hint, not an instruction: a runtime that owns its model ignores it.</summary>
    public string? ModelHint { get; init; }

    public string? EffortHint { get; init; }

    /// <summary>
    /// Only meaningful to a runtime that declares SupportsStructuredSchema. A runtime that
    /// does not must leave it alone rather than pretend: the caller is entitled to
    /// Unsupported instead of an unvalidated document.
    /// </summary>
    public string? StructuredOutputSchema { get; init; }

    /// <summary>Where a structured result should be written, when one is requested.</summary>
    public string? StructuredOutputPath { get; init; }

    /// <summary>Environment variable names the child may inherit. Everything else is invisible.</summary>
    public IReadOnlyList<string> EnvironmentAllowlist { get; init; } = [];

    public TimeSpan? SoftTimeout { get; init; }

    public TimeSpan? IdleTimeout { get; init; }

    public required TimeSpan HardTimeout { get; init; }

    public required string CorrelationId { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Only meaningful when the runtime declares SupportsSessionResume.</summary>
    public string? ResumeSessionId { get; init; }
}

/// <summary>How a run ended, as machine truth about the runtime.</summary>
public enum AgentRunOutcome
{
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Crashed,
}

/// <summary>Bounded counters a runtime may report. All optional: many runtimes know none of them.</summary>
public sealed record AgentUsageMetadata
{
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    /// <summary>Only when the runtime genuinely knows a cost. Never inferred.</summary>
    public decimal? CostAmount { get; init; }

    public string? CostCurrency { get; init; }

    public static AgentUsageMetadata Empty { get; } = new();
}

/// <summary>
/// Machine truth about one agent run.
///
/// This is evidence and input to orchestration, never an execution outcome: whether the
/// task passed is decided later by the gates and the acceptance rules, from Hub observed
/// facts rather than from anything the agent says about itself.
/// </summary>
public sealed record AgentRunResult
{
    public required AgentRunOutcome Outcome { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Set only by a runtime whose provider exposes a resumable session.</summary>
    public string? ProviderSessionId { get; init; }

    /// <summary>The structured result the agent produced, when one was requested and written.</summary>
    public string? StructuredOutput { get; init; }

    /// <summary>Set by composition once the output has been stored. Runtimes leave it null.</summary>
    public string? RawStdoutEvidenceRef { get; init; }

    public string? RawStderrEvidenceRef { get; init; }

    /// <summary>Bounded captured output, for the caller to redact and store. Never persisted here.</summary>
    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public AgentUsageMetadata Usage { get; init; } = AgentUsageMetadata.Empty;

    public required TimeSpan Duration { get; init; }

    /// <summary>Why the run ended the way it did, when the outcome alone is not enough.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>Whether a runtime is usable right now. Deterministic and offline where possible.</summary>
public sealed record HealthResult
{
    public required bool IsHealthy { get; init; }

    public required string Detail { get; init; }

    public static HealthResult Healthy(string detail) => new() { IsHealthy = true, Detail = detail };

    public static HealthResult Unhealthy(string detail) => new() { IsHealthy = false, Detail = detail };
}
