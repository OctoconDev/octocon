using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// An optimistic-concurrency version stamp carried by the journal mutation requests
/// (<c>ExpectedVersion</c>). Currently accepted from clients but not yet enforced
/// server-side; the type reserves the concept (and pairs with
/// <c>ConflictCode.ConflictStaleVersion</c>) so a future LWT check plugs into typed
/// plumbing rather than a bare <c>long</c>. JSON serializes as the raw number.
/// </summary>
[JsonConverter(typeof(EntityVersionJsonConverter))]
public readonly record struct EntityVersion(long Value)
{
    /// <summary>
    /// Explicit narrow so call sites can write <c>(EntityVersion)v</c> instead of
    /// <c>new EntityVersion(v)</c>. See <see cref="SystemId"/> for the wider rationale.
    /// </summary>
    public static explicit operator EntityVersion(long value) => new(value);

    /// <summary>
    /// Implicit widen to the underlying <see cref="long"/> for wire / DB boundary use.
    /// </summary>
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
