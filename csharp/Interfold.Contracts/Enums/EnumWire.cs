using System.Reflection;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Non-JSON callers' view of the same wire vocabulary that
/// <see cref="JsonStringEnumConverter{TEnum}"/> exposes on the JSON boundary. The wire
/// spelling for each enum member is sourced from its <see cref="JsonStringEnumMemberNameAttribute"/>
/// (or the member's own name when the attribute is absent — matching the built-in converter's
/// fallback), then cached at type initialisation. This keeps every enum's wire vocabulary in
/// exactly one place: the attribute.
/// </summary>
/// <remarks>
/// <see cref="TryParse"/> is case-insensitive (matching <see cref="JsonStringEnumConverter{TEnum}"/>'s
/// default read behaviour), treats <see langword="null"/> / empty / whitespace-only input as
/// unknown (returns <see langword="false"/> with <c>default</c>), and trims surrounding
/// whitespace before lookup — so callers reading route segments, env values, header values,
/// or JSON reader strings don't need to layer their own <c>IsNullOrWhiteSpace</c> + <c>.Trim()</c>
/// dance on top.
/// </remarks>
public static class EnumWire<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToWireMap;
    private static readonly Dictionary<string, TEnum> FromWireMap;

    static EnumWire()
    {
        var fields = typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static);
        ToWireMap = new Dictionary<TEnum, string>(fields.Length);
        FromWireMap = new Dictionary<string, TEnum>(fields.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            var value = (TEnum)field.GetValue(null)!;
            var wire = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name;

            ToWireMap[value] = wire;
            FromWireMap[wire] = value;
        }
    }

    /// <summary>
    /// Returns the wire spelling for <paramref name="value"/>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for values not declared on the enum, matching the
    /// legacy hand-rolled <c>switch</c> expressions' behaviour on unhandled members.
    /// </summary>
    public static string ToWire(TEnum value)
    {
        if (ToWireMap.TryGetValue(value, out var wire))
        {
            return wire;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value,
            $"Unhandled {typeof(TEnum).Name} value.");
    }

    /// <summary>
    /// Case-insensitive parse of a wire spelling. Returns <see langword="false"/> for
    /// <see langword="null"/> / empty / whitespace-only input and for unknown non-empty
    /// values; trims surrounding whitespace before lookup so operator-typed env values and
    /// header values don't need pre-cleaning. Callers layer any default-on-unknown semantics
    /// on top (see <c>ParseNodeGroup</c> / <c>ParseScyllaKeyspace</c> for the fallback-then-throw
    /// shape).
    /// </summary>
    public static bool TryParse(string? raw, out TEnum value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = default;
            return false;
        }

        return FromWireMap.TryGetValue(raw.Trim(), out value);
    }
}
