using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Socket.Contracts;

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
    public Shared.Contracts.Ids.SocketToken Token { get; init; } = new(string.Empty);

    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolVersion { get; init; }

    // Join-side platform tolerates unknown wire spellings → null (so a stray value can't
    // fail the whole join). Every other ClientPlatform site keeps the strict converter.
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
