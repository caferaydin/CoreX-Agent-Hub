using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Catalog;

/// <summary>
/// One machine verification command.
/// ADR-0006: an executable plus an argument vector, never a shell command line.
/// </summary>
public sealed record VerificationStep
{
    public VerificationStep(
        string name,
        string executable,
        IReadOnlyList<string> args,
        int timeoutSeconds,
        bool required,
        VerificationResultParser resultParser)
    {
        Name = Guard.NotBlankMax(name, nameof(name), 100);
        Executable = Guard.NotBlankMax(executable, nameof(executable), 200);
        Args = [.. (args ?? []).Select(a => a ?? string.Empty)];
        TimeoutSeconds = Guard.Positive(timeoutSeconds, nameof(timeoutSeconds));
        Required = required;
        ResultParser = resultParser;

        if (Executable.Contains('|', StringComparison.Ordinal)
            || Executable.Contains('&', StringComparison.Ordinal)
            || Executable.Contains('>', StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Verification step '{Name}' executable must be a plain executable, not a shell command line (ADR-0006).");
        }
    }

    public string Name { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Args { get; }

    public int TimeoutSeconds { get; }

    /// <summary>When true, a failure of this step makes the whole verification fail.</summary>
    public bool Required { get; }

    public VerificationResultParser ResultParser { get; }

    // Records compare reference-type members with the default comparer, which would make
    // two structurally identical steps unequal because Args is a different list instance.
    // Structural equality is what the idempotent config seed relies on.
    public bool Equals(VerificationStep? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Executable, other.Executable, StringComparison.Ordinal)
        && TimeoutSeconds == other.TimeoutSeconds
        && Required == other.Required
        && ResultParser == other.ResultParser
        && Args.SequenceEqual(other.Args, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Executable, StringComparer.Ordinal);
        hash.Add(TimeoutSeconds);
        hash.Add(Required);
        hash.Add(ResultParser);

        foreach (var arg in Args)
        {
            hash.Add(arg, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
