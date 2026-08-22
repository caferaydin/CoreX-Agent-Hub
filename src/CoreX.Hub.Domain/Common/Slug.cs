using System.Text.RegularExpressions;

namespace CoreX.Hub.Domain.Common;

/// <summary>
/// Lower-case kebab identifier used for organizations, projects and repositories.
/// Slugs are part of on-disk workspace paths (ADR-0004), so the character set is
/// deliberately restrictive.
/// </summary>
public sealed partial record Slug
{
    public const int MinLength = 2;
    public const int MaxLength = 64;

    private Slug(string value) => Value = value;

    public string Value { get; }

    public static Slug Parse(string? value)
    {
        if (!TryParse(value, out var slug))
        {
            throw new DomainException(
                $"'{value}' is not a valid slug. Expected lower-case kebab-case, {MinLength}-{MaxLength} characters.");
        }

        return slug;
    }

    public static bool TryParse(string? value, out Slug slug)
    {
        slug = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        if (!SlugPattern().IsMatch(candidate))
        {
            return false;
        }

        slug = new Slug(candidate);
        return true;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
