using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>Strongly-typed wrapper around the dotted command/query operation identifier
/// (e.g. <c>"cmd.alter.create"</c>). Carried on <c>CommandEnvelope&lt;T&gt;.OperationId</c>,
/// <c>ConflictResult.OperationId</c>, and idempotency store keys.</summary>
[JsonConverter(typeof(OperationIdJsonConverter))]
public readonly record struct OperationId : IParsable<OperationId>
{
    public string Value { get; }

    public OperationId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator OperationId(string value) => new(value);

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

internal sealed class OperationIdJsonConverter : StringBackedJsonConverter<OperationId>
{
    protected override OperationId Create(string value) => new(value);
    protected override string GetValue(OperationId value) => value.Value;
}
