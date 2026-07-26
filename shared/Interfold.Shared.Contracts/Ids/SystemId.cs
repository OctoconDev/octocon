using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>Strongly-typed wrapper around the 7-character alphanumeric system ID on the wire.</summary>
[JsonConverter(typeof(SystemIdJsonConverter))]
public readonly record struct SystemId : IParsable<SystemId>
{
    public string Value { get; }

    public SystemId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator SystemId(string value) => new(value);

    // Safe implicit widen — SystemId is a public identifier, ToString() returns Value verbatim.
    // Hazard: default(SystemId).Value is null; do not widen a default slot.
    public static implicit operator string(SystemId value) => value.Value;

    public override string ToString() => Value;

    public static SystemId Parse(string s, IFormatProvider? provider) => new(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out SystemId result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        result = new SystemId(s);
        return true;
    }
}

internal sealed class SystemIdJsonConverter : StringBackedJsonConverter<SystemId>
{
    protected override SystemId Create(string value) => new(value);
    protected override string GetValue(SystemId value) => value.Value;
}
