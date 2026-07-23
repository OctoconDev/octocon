using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Interfold.Contracts;

// JSON converters that normalize DateTime/DateTimeOffset to UTC when writing
// and tolerate malformed incoming strings like "2026-05-15T19:32:58.8619339+01:00Z" when reading.
public sealed partial class UtcDateTimeConverter : JsonConverter<DateTime>
{
    internal static readonly Regex OffsetWithTrailingZ = OffsetWithTrailingZRegex();

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected string token for DateTime");

        var s = reader.GetString() ?? string.Empty;
        // Normalize malformed offsets like "+01:00Z" -> "+01:00"
        s = OffsetWithTrailingZ.Replace(s, "$1");

        // Use DateTimeOffset to preserve offset information if present, then convert to UTC
        if (DateTimeOffset.TryParse(s, out var dto))
        {
            return dto.UtcDateTime;
        }

        // Fallback to DateTime.Parse which will throw as appropriate
        return DateTime.Parse(s).ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // Always write as UTC with the 'o' format which for UTC DateTime includes a trailing 'Z'
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("o"));
    }

    [GeneratedRegex(@"([+-]\d{2}:\d{2})Z$", RegexOptions.Compiled)]
    private static partial Regex OffsetWithTrailingZRegex();
}

public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected string token for DateTimeOffset");

        var s = reader.GetString() ?? string.Empty;
        s = UtcDateTimeConverter.OffsetWithTrailingZ.Replace(s, "$1");

        if (DateTimeOffset.TryParse(s, out var dto))
        {
            return dto.ToUniversalTime();
        }

        // Last resort - allow DateTime parse and wrap into DateTimeOffset (UTC)
        var dt = DateTime.Parse(s).ToUniversalTime();
        return new DateTimeOffset(dt);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        // Write as UTC with 'Z' by converting to UTC DateTime and formatting with "o"
        var utc = value.ToUniversalTime().UtcDateTime;
        writer.WriteStringValue(utc.ToString("o"));
    }
}