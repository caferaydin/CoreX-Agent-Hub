using System.Globalization;
using System.Text.RegularExpressions;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tasks;

/// <summary>
/// Human readable task identifier. Approved Phase 0 format decision:
/// <c>&lt;PROJECT&gt;-&lt;3 digit&gt;</c> (for example <c>HUB-001</c>).
/// The prefix is NOT hard-coded here: it belongs to project configuration and is
/// matched against <see cref="Catalog.Project.TaskKeyPrefix"/> at task creation time.
/// </summary>
public sealed partial record TaskKey
{
    public const int MinPrefixLength = 2;
    public const int MaxPrefixLength = 10;

    private TaskKey(string prefix, int number)
    {
        Prefix = prefix;
        Number = number;
    }

    public string Prefix { get; }

    public int Number { get; }

    public string Value => string.Create(
        CultureInfo.InvariantCulture,
        $"{Prefix}-{Number:D3}");

    public static TaskKey Parse(string? value)
    {
        if (!TryParse(value, out var key))
        {
            throw new DomainException(
                $"'{value}' is not a valid task key. Expected <PROJECT>-<3 digit>, for example HUB-001.");
        }

        return key;
    }

    public static bool TryParse(string? value, out TaskKey key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = TaskKeyPattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        key = new TaskKey(match.Groups[1].Value, number);
        return true;
    }

    /// <summary>Validates a project task-key prefix independently of any number.</summary>
    public static bool IsValidPrefix(string? prefix) =>
        !string.IsNullOrWhiteSpace(prefix) && PrefixPattern().IsMatch(prefix.Trim());

    public override string ToString() => Value;

    [GeneratedRegex("^([A-Z][A-Z0-9]{1,9})-([0-9]{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskKeyPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
