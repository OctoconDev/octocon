using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Strongly-typed wrapper around the client-supplied (or server-minted) idempotency key
/// that scopes command deduplication. Carried on <c>CommandEnvelope&lt;T&gt;.IdempotencyKey</c>,
/// the idempotency store keys, and <c>ImportOperationSnapshot.IdempotencyKey</c>.
///
/// <para>
/// <b>Wire compatibility.</b> JSON serializes as the raw underlying string. DB parameter
/// binding must pass <see cref="Value"/> explicitly (Npgsql/Cassandra cannot bind the struct).
/// </para>
/// </summary>
[JsonConverter(typeof(IdempotencyKeyJsonConverter))]
public readonly record struct IdempotencyKey : IParsable<IdempotencyKey>
{
    public string Value { get; }

    public IdempotencyKey(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(IdempotencyKey)raw</c> instead of
    /// <c>new IdempotencyKey(raw)</c>. See <see cref="SystemId"/>'s conversion operators for
    /// the wider rationale on explicit-narrow / implicit-widen.
    /// </summary>
    public static explicit operator IdempotencyKey(string value) => new(value);

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/> for DB / HTTP boundary use.
    /// </summary>
    public static implicit operator string(IdempotencyKey value) => value.Value;

    public override string ToString() => Value;

    public static IdempotencyKey Parse(string s, IFormatProvider? provider) => new(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out IdempotencyKey result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        result = new IdempotencyKey(s);
        return true;
    }
}

internal sealed class IdempotencyKeyJsonConverter : JsonConverter<IdempotencyKey>
{
    public override IdempotencyKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, IdempotencyKey value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
