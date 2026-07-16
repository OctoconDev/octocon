using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// JSON converter for a nullable enum that returns <see langword="null"/> for unknown
/// wire spellings (or non-string tokens) instead of throwing a <see cref="JsonException"/>.
/// Read side delegates to <see cref="EnumWire{TEnum}.TryParse"/> — same case-insensitive
/// lookup, same <see cref="JsonStringEnumMemberNameAttribute"/> source of truth — so the
/// wire vocabulary stays defined by the enum, not the converter. Write side emits the
/// canonical wire spelling via <see cref="EnumWire{TEnum}.ToWire"/> (and <c>null</c> for
/// <c>null</c>).
/// </summary>
/// <remarks>
/// <para>
/// Intended for boundary payloads whose contract explicitly promises tolerance —
/// see the ClientPlatform join member, whose docstring pins "unknown values are
/// handled by callers via <c>EnumWire&lt;T&gt;.TryParse</c>" — so a stray value on
/// ONE field cannot poison sibling fields via a wholesale
/// <see cref="JsonException"/>-then-defaults recovery upstream. For every other
/// consumer of the enum, the default strict <see cref="JsonStringEnumConverter{TEnum}"/>
/// still applies (this converter is opt-in via <see cref="JsonConverterAttribute"/>
/// on the specific property, not the enum type).
/// </para>
/// <para>
/// The nullability is required: without it, an unknown wire spelling would have
/// to be represented as some in-vocabulary sentinel, which would silently
/// misclassify (e.g. a mystery future platform becoming "web"). Tolerance
/// belongs at the join site — the handler already treats <c>Platform == null</c>
/// as "unknown / not iOS", which is the exact legacy semantic.
/// </para>
/// </remarks>
public sealed class TolerantWireEnumJsonConverter<TEnum> : JsonConverter<TEnum?>
    where TEnum : struct, Enum
{
    public override TEnum? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                // EnumWire<T>.TryParse already handles null/empty/whitespace and trims
                // surrounding whitespace, so we can hand the reader string straight in
                // without pre-cleaning — matches the tolerance shape documented on
                // EnumWire itself.
                return EnumWire<TEnum>.TryParse(reader.GetString(), out var value) ? value : null;
            default:
                // Numbers / bools / objects / arrays: tolerant read is "unknown → null",
                // matching how the legacy per-field probing simply skipped members it
                // couldn't decode rather than failing the whole payload.
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(EnumWire<TEnum>.ToWire(value.Value));
    }
}
