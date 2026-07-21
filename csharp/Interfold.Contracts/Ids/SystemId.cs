using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Strongly-typed wrapper around the 7-character alphanumeric system ID used to
/// identify an Interfold user/system on the wire.
///
/// <para>
/// The type is a <see cref="readonly record struct"/>: it costs nothing at runtime
/// compared to <see cref="string"/> and has value-based equality, so it works as a
/// dictionary key or set element.
/// </para>
///
/// <para>
/// <b>Wire compatibility.</b> The JSON representation is the raw underlying string
/// (see <see cref="SystemIdJsonConverter"/>). CQL / SQL parameter binding passes
/// <see cref="Value"/> explicitly. ASP.NET Core route binding is supported via
/// <see cref="IParsable{TSelf}"/> so <c>[FromRoute] SystemId systemId</c> works
/// unchanged.
/// </para>
///
/// <para>
/// <b>Migration story.</b> The initial rollout introduces this type as the
/// foundation for a codebase-wide sweep. Repositories and services currently accept
/// <see cref="string"/> parameters; call sites pass <see cref="Value"/> explicitly
/// to keep the type-safety win at the boundary while the deep threading through the
/// three persistence backends (InMemory / Postgres / Scylla) lands as a follow-up.
/// </para>
/// </summary>
[JsonConverter(typeof(SystemIdJsonConverter))]
public readonly record struct SystemId : IParsable<SystemId>
{
    public string Value { get; }

    public SystemId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(SystemId)raw</c> instead of the
    /// <c>new SystemId(raw)</c> ceremony. Explicit (not implicit) preserves the "raw and
    /// typed cannot silently mix" guarantee this type exists for.
    /// </summary>
    public static explicit operator SystemId(string value) => new(value);

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/> — reads as "get the wire value" and
    /// removes the <c>.Value</c> ceremony at every <c>string</c>-typed sink. Safe because
    /// <c>ToString()</c> already returns the raw value verbatim.
    /// <br/>
    /// <b>Hazard:</b> <c>default(SystemId)</c> bypasses the ctor's null-guard, so widening a
    /// default slot returns <see langword="null"/>. Identical hazard to reading
    /// <see cref="Value"/> on a <c>default</c>; do not widen where a default might sneak in.
    /// </summary>
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
