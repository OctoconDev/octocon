namespace Interfold.Shared.Contracts.Configuration;

/// <summary>
/// Docker Compose network names the AppHost graph emits. Operator-frozen alongside
/// <see cref="ComposeServices"/>: existing deployments reference them (e.g.
/// <c>docker network inspect</c> runbooks), and renaming would orphan the old networks
/// on update.
/// </summary>
public static class ComposeNetworks
{
    public const string Scylla = "scylla";
    public const string Postgres = "postgres";
    public const string Api = "api";
}

/// <summary>
/// Docker Compose named volumes the AppHost graph emits. Operator-frozen: renaming a
/// volume silently detaches existing data on the next <c>docker compose up</c>.
/// </summary>
public static class ComposeVolumes
{
    public const string PostgresData = "msg_pgdata";
    public const string ScyllaData = "scylla_data";
    public const string CassandraData = "cassandra_data";

    /// <summary>Per-region Scylla volume for multi-node topologies (<c>scylla_{region}_data</c>).</summary>
    public static string ScyllaRegionData(string regionWireValue) => $"scylla_{regionWireValue}_data";
}
