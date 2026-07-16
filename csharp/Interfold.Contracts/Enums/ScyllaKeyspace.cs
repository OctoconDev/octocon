using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Per-instance region / keyspace identity. Controls both the Scylla session default keyspace
/// and the region used by the API for new-account creation and query routing. In <c>single</c> /
/// <c>cassandra</c> deployments only <see cref="Nam"/> exists on the cluster; in <c>multi</c>
/// deployments the operator picks which regional keyspace this stack's API container serves.
/// </summary>
/// <remarks>
/// JSON wire values (lowercase enum name):
/// <c>"nam"</c>, <c>"eur"</c>, <c>"sam"</c>, <c>"sas"</c>, <c>"eas"</c>, <c>"ocn"</c>, <c>"gdpr"</c>.
/// These strings also appear verbatim as CQL keyspace names and as the region prefix on
/// externally-shared system IDs (e.g. <c>nam:abc1234</c>), so the wire mapping must never drift.
/// </remarks>
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
