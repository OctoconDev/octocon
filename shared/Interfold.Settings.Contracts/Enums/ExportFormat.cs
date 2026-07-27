using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>Selects the export payload shape. Wire: lowercase name. Placed on the shared
/// spine so <see cref="EnumWire{TEnum}"/> picks it up for filename authority without a
/// second attribute registry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportFormat>))]
public enum ExportFormat : short
{
    [JsonStringEnumMemberName("pk")]
    Pk = 0,

    [JsonStringEnumMemberName("full")]
    Full = 1,
}
