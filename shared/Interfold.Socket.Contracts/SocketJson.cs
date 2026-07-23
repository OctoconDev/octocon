using System.Text.Json;

namespace Interfold.Contracts;

public static class SocketJson
{    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new UtcDateTimeConverter(),
            new UtcDateTimeOffsetConverter()
        }
    };
}

