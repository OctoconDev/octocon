using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>Optimistic-concurrency version stamp carried by journal mutation requests
/// (<c>ExpectedVersion</c>). Accepted from clients today but not enforced server-side
/// yet — the type reserves the concept for a future LWT check. JSON: raw number.</summary>
[JsonConverter(typeof(EntityVersionJsonConverter))]
public readonly record struct EntityVersion(long Value)
{
    public static explicit operator EntityVersion(long value) => new(value);

    public static implicit operator long(EntityVersion value) => value.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class EntityVersionJsonConverter : JsonConverter<EntityVersion>
{
    public override EntityVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, EntityVersion value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
