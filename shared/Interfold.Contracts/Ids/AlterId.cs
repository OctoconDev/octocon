using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>Strongly-typed wrapper around the per-system alter id. Backed by <c>short</c>
/// to match every smallint Scylla column that stores it; wire form is a JSON number, so
/// emission is byte-identical to the pre-narrowing shape. Range is enforced at the JSON
/// reader (<c>GetInt16</c> throws → 400) so no downstream code sees an out-of-range value.</summary>
[JsonConverter(typeof(AlterIdJsonConverter))]
public readonly record struct AlterId : IParsable<AlterId>
{
    public short Value { get; }

    public AlterId(short value)
    {
        Value = value;
    }

    public static explicit operator AlterId(short value) => new(value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static AlterId Parse(string s, IFormatProvider? provider)
        => new(short.Parse(s, System.Globalization.NumberStyles.Integer, provider ?? System.Globalization.CultureInfo.InvariantCulture));

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out AlterId result)
    {
        if (short.TryParse(s, System.Globalization.NumberStyles.Integer, provider ?? System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            result = new AlterId(value);
            return true;
        }

        result = default;
        return false;
    }
}

internal sealed class AlterIdJsonConverter : JsonConverter<AlterId>
{
    public override AlterId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt16());

    public override void Write(Utf8JsonWriter writer, AlterId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
