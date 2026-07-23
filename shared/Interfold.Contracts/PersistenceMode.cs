using System.Text.Json.Serialization;

namespace Interfold.Contracts;

/// <summary>
/// Persistence backend selection sourced from the <c>OCTOCON_PERSISTENCE</c> env var. Wire
/// spellings live on <see cref="JsonStringEnumMemberNameAttribute"/> so a rename to any enum
/// member here does NOT silently shift the env-var / deployment contract. Non-JSON callers
/// round-trip through <see cref="Enums.EnumWire{TEnum}"/> (write side:
/// <see cref="Enums.EnumWireExtensions.ToWire{TEnum}(TEnum)"/>; read side:
/// <see cref="Enums.EnumWireExtensions.ParsePersistenceMode(string?)"/>) — the same single
/// source of truth every other Interfold enum uses.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PersistenceMode>))]
public enum PersistenceMode
{
    [JsonStringEnumMemberName("inmemory")]
    InMemory,

    [JsonStringEnumMemberName("scylla-postgres")]
    ScyllaPostgres,
}
