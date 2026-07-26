using System.Text.Json;
using Interfold.Shared.Contracts;

namespace Interfold.Socket.Contracts;

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

