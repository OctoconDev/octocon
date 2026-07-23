using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Interfold.Api.Models;

/// <summary>Validated Unix-seconds anchor for fronting history endpoints. Combines
/// long parse + <see cref="DateTimeOffset.FromUnixTimeSeconds"/> range check.
/// Culture-invariant (Unix timestamps are integers), so the supplied
/// <see cref="IFormatProvider"/> is ignored.</summary>
public readonly record struct UnixSeconds(long Value) : IParsable<UnixSeconds>
{
    public DateTimeOffset ToDateTimeOffset() => DateTimeOffset.FromUnixTimeSeconds(Value);

    public static UnixSeconds Parse(string s, IFormatProvider? provider)
        => TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"Value '{s}' is not a valid Unix timestamp.");

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out UnixSeconds result)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            result = new UnixSeconds(seconds);
            return true;
        }

        result = default;
        return false;
    }
}
