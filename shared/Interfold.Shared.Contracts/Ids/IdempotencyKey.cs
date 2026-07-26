using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>Strongly-typed wrapper around the client-supplied (or server-minted) idempotency
/// key that scopes command deduplication.</summary>
[JsonConverter(typeof(IdempotencyKeyJsonConverter))]
public readonly record struct IdempotencyKey : IParsable<IdempotencyKey>
{
    public string Value { get; }

    public IdempotencyKey(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator IdempotencyKey(string value) => new(value);

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

internal sealed class IdempotencyKeyJsonConverter : StringBackedJsonConverter<IdempotencyKey>
{
    protected override IdempotencyKey Create(string value) => new(value);
    protected override string GetValue(IdempotencyKey value) => value.Value;
}
