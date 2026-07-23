using System.Reflection;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>Non-JSON callers' view of the same wire vocabulary
/// <see cref="JsonStringEnumConverter{TEnum}"/> exposes on the JSON boundary. Wire spelling
/// per member comes from <see cref="JsonStringEnumMemberNameAttribute"/> (fallback: member
/// name), cached at type init. TryParse is case-insensitive, trims whitespace, and treats
/// null/empty/blank as unknown.</summary>
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

    /// <summary>Wire spelling for <paramref name="value"/>; throws on undeclared members.</summary>
    public static string ToWire(TEnum value)
    {
        if (ToWireMap.TryGetValue(value, out var wire))
        {
            return wire;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value,
            $"Unhandled {typeof(TEnum).Name} value.");
    }

    /// <summary>Case-insensitive parse; trims whitespace. Callers add any
    /// default-on-unknown semantics (see EnumWireExtensions.ParseWithDefault).</summary>
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
