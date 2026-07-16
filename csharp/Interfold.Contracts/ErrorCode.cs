using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts;

/// <summary>
/// A client-visible error identifier — the typed form of the frozen wire vocabulary in
/// <see cref="ErrorCodes"/>. Same role <c>EntityRef</c>/<c>EntityRefs</c> play for conflict
/// entity references: the registry is the convention, the struct is the type distinction
/// that stops arbitrary strings from reaching <c>ErrorResponse.Code</c>,
/// <c>InterfoldException.Code</c>, or <c>SocketReasonResponse.Reason</c>. The constructor
/// stays public for the few dynamically-derived codes (e.g. resolution-hint conflicts).
/// JSON serializes as the raw string.
/// </summary>
[JsonConverter(typeof(ErrorCodeJsonConverter))]
public readonly record struct ErrorCode
{
    public string Value { get; }

    public ErrorCode(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;
}

internal sealed class ErrorCodeJsonConverter : JsonConverter<ErrorCode>
{
    public override ErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, ErrorCode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
