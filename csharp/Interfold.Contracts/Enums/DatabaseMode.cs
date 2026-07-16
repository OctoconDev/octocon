using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Database stack the bootstrapper deploys. Translates into the orthogonal AppHost parameters
/// <c>include-scylla</c> / <c>include-cassandra</c> / <c>scylla-topology</c> via
/// <c>PublishPhase.TranslateDatabaseMode</c>.
/// </summary>
/// <remarks>
/// JSON wire values (lowercase enum name):
/// <list type="bullet">
///   <item><see cref="Single"/> — one Scylla node.</item>
///   <item><see cref="Multi"/> — 7-region regional Scylla cluster.</item>
///   <item><see cref="Cassandra"/> — one Cassandra 5 node, no Scylla.</item>
/// </list>
/// </remarks>
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
