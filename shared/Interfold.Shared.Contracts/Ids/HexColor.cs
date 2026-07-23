using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>Display color for alters/tags/journals — canonically <c>#RRGGBB</c>, also
/// accepts <c>#RGB</c> and <c>#RRGGBBAA</c>. Fully strict: every construction path
/// validates and throws on malformed input. JSON serializes as the raw string;
/// malformed inbound surfaces as 400 via <see cref="JsonException"/>.</summary>
[JsonConverter(typeof(HexColorJsonConverter))]
public readonly record struct HexColor : IParsable<HexColor>
{
    public string Value { get; }

    public HexColor(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsWellFormedString(value))
        {
            throw new FormatException(
                $"'{value}' is not a well-formed HexColor (expected '#RGB', '#RRGGBB', or '#RRGGBBAA').");
        }

        Value = value;
    }

    public static explicit operator HexColor(string value) => new(value);

    public static implicit operator string(HexColor value) => value.Value;

    // Tautology for values built through the public API; retained for defence-in-depth
    // and test-side assertions.
    public bool IsWellFormed => IsWellFormedString(Value);

    public override string ToString() => Value;

    public static HexColor Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new HexColor(s);
    }

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out HexColor result)
    {
        if (s is null || !IsWellFormedString(s))
        {
            result = default;
            return false;
        }

        result = new HexColor(s);
        return true;
    }

    /// <summary>Persistence-boundary rehydration; null → null, well-formed → wrap,
    /// malformed → throw. Legacy rows are normalised by <c>HexColorFixupService</c>
    /// before this boundary is enforced.</summary>
    public static HexColor? FromNullable(string? value) => value is null ? null : new HexColor(value);

    /// <summary>Recover a canonical spelling from noisy input (SP importer /
    /// <c>HexColorFixupService</c>). Returns null when unrecoverable; otherwise the
    /// well-formed <c>#RRGGBB</c> / <c>#RGB</c> / <c>#RRGGBBAA</c> form.</summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (IsWellFormedString(trimmed))
            return trimmed;

        var stripped = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        if (stripped.Length != 6)
            return null;

        foreach (var c in stripped)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        return "#" + stripped;
    }

    // Accepts #RGB, #RRGGBB, or #RRGGBBAA (case-insensitive on hex digits).
    private static bool IsWellFormedString(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value[0] != '#')
            return false;

        var digits = value.Length - 1;
        if (digits is not (3 or 6 or 8))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return false;
        }

        return true;
    }
}

internal sealed class HexColorJsonConverter : JsonConverter<HexColor>
{
    public override HexColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (raw is null || !HexColor.TryParse(raw, provider: null, out var result))
        {
            throw new JsonException(
                $"'{raw}' is not a well-formed HexColor JSON value (expected '#RGB', '#RRGGBB', or '#RRGGBBAA').");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, HexColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
