using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>
/// Strongly-typed wrapper around the conflict entity-reference string carried on
/// <c>ConflictResult.EntityRef</c> (e.g. <c>"poll:title_too_long"</c>) and surfaced to
/// clients verbatim via <c>ErrorResponse.EntityRef</c> on 409/422 bodies. The closed
/// vocabulary lives in <c>Interfold.Shared.Contracts.Operations.EntityRefs</c>; integration tests
/// assert the exact strings, so the wire spellings are frozen.
/// </summary>
[JsonConverter(typeof(EntityRefJsonConverter))]
public readonly record struct EntityRef
{
    public string Value { get; }

    public EntityRef(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator EntityRef(string value) => new(value);

    public static implicit operator string(EntityRef value) => value.Value;

    public override string ToString() => Value;
}

internal sealed class EntityRefJsonConverter : StringBackedJsonConverter<EntityRef>
{
    protected override EntityRef Create(string value) => new(value);
    protected override string GetValue(EntityRef value) => value.Value;
}
