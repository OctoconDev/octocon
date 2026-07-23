using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>Database stack the bootstrapper deploys. Wire values: single (one Scylla node),
/// multi (7-region Scylla cluster), cassandra (single Cassandra 5 node). Translated to
/// AppHost <c>include-scylla</c>/<c>include-cassandra</c>/<c>scylla-topology</c> params.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseMode>))]
public enum DatabaseMode
{
    [JsonStringEnumMemberName("single")]
    Single,

    [JsonStringEnumMemberName("multi")]
    Multi,

    [JsonStringEnumMemberName("cassandra")]
    Cassandra,
}
