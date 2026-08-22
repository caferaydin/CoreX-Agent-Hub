namespace CoreX.Hub.Domain.Common;

/// <summary>
/// Minimal invariant helpers. Deliberately not a validation framework
/// (architecture section W: no custom validation framework).
/// </summary>
public static class Guard
{
    public static string NotBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} must not be blank.");
        }

        return value.Trim();
    }

    public static string NotBlankMax(string? value, string name, int maxLength)
    {
        var trimmed = NotBlank(value, name);
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? OptionalMax(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static Guid NotEmpty(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException($"{name} must not be an empty GUID.");
        }

        return value;
    }

    public static int Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw new DomainException($"{name} must be greater than zero.");
        }

        return value;
    }

    public static T NotNull<T>(T? value, string name) where T : class
    {
        return value ?? throw new DomainException($"{name} must not be null.");
    }
}
