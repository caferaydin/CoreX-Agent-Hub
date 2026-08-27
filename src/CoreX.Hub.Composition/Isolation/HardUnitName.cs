using System.Globalization;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// The name of the transient unit one run owns.
///
/// Derived from trusted Hub identity and from nothing else. That is the whole point: a unit name
/// is a control-plane handle, and anything able to influence it - a task objective, a repository
/// name, a provider's output - would be able to name a unit it does not own, or collide with one
/// it should not touch. So the only input is a correlation id the Hub generated, and the shape
/// is fixed.
///
/// The prefix is not decoration either. Every unit this system creates is recognisable at a
/// glance in <c>systemctl</c>, and the cancellation path refuses to act on anything that does
/// not carry it.
/// </summary>
public static class HardUnitName
{
    /// <summary>Marks a unit as Hub-owned. Nothing outside this prefix may be controlled.</summary>
    public const string Prefix = "corex-hub-";

    public const string Suffix = ".service";

    /// <summary>
    /// Builds the unit name for one run.
    ///
    /// The correlation id is formatted as 32 hexadecimal characters, which is inside systemd's
    /// unit-name alphabet by construction - there is nothing to escape, and therefore no
    /// escaping to get wrong.
    /// </summary>
    public static string For(Guid correlationId)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A hard isolated run needs a correlation id; an empty one would name every run the same unit.",
                nameof(correlationId));
        }

        return Prefix + correlationId.ToString("N", CultureInfo.InvariantCulture) + Suffix;
    }

    /// <summary>
    /// Whether a name is one this system may control.
    ///
    /// Used on the cancellation path, where the question is not "is this a valid unit name" but
    /// "did we create this". A name that did not come out of <see cref="For"/> is refused, so a
    /// value that reached the executor from anywhere else cannot be stopped through it.
    /// </summary>
    public static bool IsHubOwned(string? unitName)
    {
        if (unitName is null
            || !unitName.StartsWith(Prefix, StringComparison.Ordinal)
            || !unitName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var identity = unitName[Prefix.Length..^Suffix.Length];

        return identity.Length == 32 && identity.All(char.IsAsciiHexDigitLower);
    }
}
