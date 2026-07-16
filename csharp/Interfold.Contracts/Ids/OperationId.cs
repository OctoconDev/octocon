using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Strongly-typed wrapper around the dotted command/query operation identifier
/// (e.g. <c>"cmd.alter.create"</c>). Carried on <c>CommandEnvelope&lt;T&gt;.OperationId</c>,
/// <c>ConflictResult.OperationId</c>, and the idempotency store keys.
///
/// <para>
/// <b>Wire compatibility.</b> JSON serializes as the raw underlying string. DB parameter
/// binding must pass <see cref="Value"/> explicitly (Npgsql/Cassandra cannot bind the
/// struct). The <c>X-Interfold-OperationId</c> response header uses <see cref="Value"/>.
/// </para>
/// </summary>
[JsonConverter(typeof(OperationIdJsonConverter))]
public readonly record struct OperationId : IParsable<OperationId>
{
    public string Value { get; }

    public OperationId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(OperationId)raw</c> instead of
    /// <c>new OperationId(raw)</c>. See <see cref="SystemId"/> for the wider rationale.
    /// </summary>
    public static explicit operator OperationId(string value) => new(value);

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/> for header / DB boundary use.
    /// </summary>
    public static implicit operator string(OperationId value) => value.Value;

    public override string ToString() => Value;

    public static OperationId Parse(string s, IFormatProvider? provider) => new(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out OperationId result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        result = new OperationId(s);
        return true;
    }
}

internal sealed class OperationIdJsonConverter : JsonConverter<OperationId>
{
    public override OperationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, OperationId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
