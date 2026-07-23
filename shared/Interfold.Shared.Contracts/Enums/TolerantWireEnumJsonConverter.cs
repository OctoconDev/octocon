using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>Nullable-enum JSON converter: unknown wire spellings (or non-string tokens)
/// return null instead of throwing. Opt-in per-property; write emits the canonical
/// <see cref="EnumWire{TEnum}.ToWire"/> spelling. Used at boundary payloads where a stray
/// value on one field must not poison sibling fields.</summary>
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
                return EnumWire<TEnum>.TryParse(reader.GetString(), out var value) ? value : null;
            default:
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
