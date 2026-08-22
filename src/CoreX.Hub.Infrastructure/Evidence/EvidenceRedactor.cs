namespace CoreX.Hub.Infrastructure.Evidence;

public sealed record RedactionResult(string Text, int RedactionCount)
{
    public bool AnythingRedacted => RedactionCount > 0;
}

/// <summary>
/// Applies the shared secret catalog to evidence text.
///
/// Used at write time, before bytes reach the blob store, so an unredacted secret is never
/// persisted. Nothing here records what was matched: the count is metadata, the value is
/// not (Phase 4 brief section 32).
/// </summary>
public sealed class EvidenceRedactor
{
    private readonly IReadOnlyList<SecretPattern> _patterns;

    public EvidenceRedactor()
        : this(SecretPatternCatalog.Patterns)
    {
    }

    public EvidenceRedactor(IReadOnlyList<SecretPattern> patterns) =>
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));

    public RedactionResult Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RedactionResult(text ?? string.Empty, 0);
        }

        var current = text;
        var count = 0;

        foreach (var pattern in _patterns)
        {
            current = pattern.Pattern.Replace(current, match =>
            {
                count++;
                return pattern.Replacement;
            });
        }

        return new RedactionResult(current, count);
    }
}
