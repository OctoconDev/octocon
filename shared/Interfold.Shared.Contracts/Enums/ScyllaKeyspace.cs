using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>Per-instance region / keyspace identity. Wire values (lowercase name) also
/// appear as CQL keyspace names and as the region prefix on externally-shared system IDs
/// (e.g. <c>nam:abc1234</c>) — the mapping must never drift.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScyllaKeyspace>))]
public enum ScyllaKeyspace
{
    [JsonStringEnumMemberName("nam")]
    Nam,

    [JsonStringEnumMemberName("eur")]
    Eur,

    [JsonStringEnumMemberName("sam")]
    Sam,

    [JsonStringEnumMemberName("sas")]
    Sas,

    [JsonStringEnumMemberName("eas")]
    Eas,

    [JsonStringEnumMemberName("ocn")]
    Ocn,

    [JsonStringEnumMemberName("gdpr")]
    Gdpr,
}
