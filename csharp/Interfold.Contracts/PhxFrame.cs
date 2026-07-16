using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Contracts;

/// <summary>
/// A Phoenix-protocol WebSocket frame (object format).
/// </summary>
public sealed class PhxFrame<TPayload>
{
    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public TPayload? Payload { get; init; }

    [JsonPropertyName("ref")]
    public string? Ref { get; init; }

    [JsonPropertyName("join_ref")]
    public string? JoinRef { get; init; }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, SocketJson.Options));
}

/// <summary>
/// Payload for <c>phx_join</c> frames.
/// </summary>
public sealed class PhxJoinPayload
{
    [JsonPropertyName("token")]
    public Ids.SocketToken Token { get; init; } = new(string.Empty);

    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolVersion { get; init; }

    // Join-side platform is deliberately tolerant: unknown wire spellings (e.g. a
    // future "wasm" client, or a typo from an older release the server has to
    // interop with) must round-trip to null WITHOUT throwing a JsonException on
    // this property, otherwise a wholesale Deserialize<PhxJoinPayload> failure
    // upstream would take the token and protocolVersion siblings down with it and
    // turn a valid join into a bogus Unauthorized reply. The ClientPlatform
    // docstring pins this contract ("unknown values are handled by callers via
    // EnumWire<T>.TryParse so the legacy invalid-platform responses are
    // preserved"); this converter is the enforcement point for the socket-join
    // boundary specifically — every other JSON caller of ClientPlatform keeps
    // the default strict JsonStringEnumConverter<ClientPlatform>.
    [JsonPropertyName("platform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(TolerantWireEnumJsonConverter<ClientPlatform>))]
    public ClientPlatform? Platform { get; init; }

    [JsonPropertyName("isReconnect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsReconnect { get; init; }

    [JsonPropertyName("forceBatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ForceBatch { get; init; }
}

public sealed class PhxEndpointPayload : PhxEndpointPayload<object>;

/// <summary>
/// Payload for <c>endpoint</c> frames (proxied HTTP calls over WebSocket).
/// </summary>
public class PhxEndpointPayload<T> where T : new()
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("body"), JsonConverter(typeof(BodyJsonConverterFactory))]
    public T Body { get; init; } = new T();
}

public class BodyJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => true;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(BodyJson<>).MakeGenericType(typeToConvert);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}

public class BodyJson<T> : JsonConverter<T>
    where T : new()
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new Exception("Expected string token for JSON body");
        }

        var rawContent = reader.GetString();
        var content = string.IsNullOrWhiteSpace(rawContent)
            ? new T()
            : JsonSerializer.Deserialize<T>(rawContent, options);

        return content;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var format = value == null ? "" : JsonSerializer.Serialize(value, value.GetType(), options);
        writer.WriteStringValue(format);
    }
}

/// <summary>
/// Helper for constructing Phoenix array-format WebSocket frames
/// (used when testing the array-protocol variant).
/// </summary>
public static class PhxArrayFrame
{
    public static byte[] CreateBytes<TPayload>(
        string? joinRef, string? @ref, string topic, string @event, TPayload payload)
    {
        var array = new object?[] { joinRef, @ref, topic, @event, (object?)payload };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(array, SocketJson.Options));
    }
}
