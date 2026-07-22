namespace Interfold.AppHostGraph;

/// <summary>Env-var names the AppHost graph sets on third-party containers. Also read
/// inside compose healthcheck / <see cref="DockerExecCqlProbe"/> shell one-liners, so C#
/// registration and in-container <c>$VAR</c> expansion share these constants. Names are
/// dictated by upstream entrypoints — not ours to rename.</summary>
internal static class ContainerEnvNames
{
    // --- Postgres / TimescaleDB (official entrypoint + timescaledb-tune init script) ---
    public const string PostgresUser = "POSTGRES_USER";
    public const string PostgresPassword = "POSTGRES_PASSWORD";
    public const string PostgresInitDbArgs = "POSTGRES_INITDB_ARGS";
    public const string PgData = "PGDATA";
    public const string PgCtlTimeout = "PGCTLTIMEOUT";
    public const string TsTuneMemory = "TS_TUNE_MEMORY";
    public const string TsTuneNumCpus = "TS_TUNE_NUM_CPUS";

    // --- CQL clients (cqlsh reads these inside both Scylla and Cassandra containers) ---
    public const string CqlshUser = "CQLSH_USER";
    public const string CqlshPassword = "CQLSH_PASSWORD";

    // --- Cassandra (official entrypoint) ---
    public const string CassandraClusterName = "CASSANDRA_CLUSTER_NAME";
    public const string CassandraListenAddress = "CASSANDRA_LISTEN_ADDRESS";
    public const string CassandraBroadcastAddress = "CASSANDRA_BROADCAST_ADDRESS";
    public const string CassandraBroadcastRpcAddress = "CASSANDRA_BROADCAST_RPC_ADDRESS";
    public const string CassandraRpcAddress = "CASSANDRA_RPC_ADDRESS";
    public const string CassandraEndpointSnitch = "CASSANDRA_ENDPOINT_SNITCH";
    public const string CassandraNumTokens = "CASSANDRA_NUM_TOKENS";
    public const string CassandraDc = "CASSANDRA_DC";
    public const string CassandraRack = "CASSANDRA_RACK";
    public const string MaxHeapSize = "MAX_HEAP_SIZE";
    public const string HeapNewSize = "HEAP_NEWSIZE";

    // --- Interfold API container (ASP.NET Core hosting + app secrets) ---
    public const string EncryptionPrivateKey = "ENCRYPTION_PRIVATE_KEY";
    public const string AspNetCoreHttpPorts = "ASPNETCORE_HTTP_PORTS";
    public const string AspNetCoreHttpsPorts = "ASPNETCORE_HTTPS_PORTS";
    public const string AspNetCoreKestrelDefaultCertPath = "ASPNETCORE_Kestrel__Certificates__Default__Path";

    // --- Octocon web (official nginx image's envsubst-on-templates entrypoint) ---
    public const string NginxServerName = "NGINX_SERVER_NAME";
    public const string NginxSslCertFile = "NGINX_SSL_CERT_FILE";
    public const string NginxSslKeyFile = "NGINX_SSL_KEY_FILE";
    public const string NginxHttpsPortSuffix = "NGINX_HTTPS_PORT_SUFFIX";
    public const string NginxEnvsubstFilter = "NGINX_ENVSUBST_FILTER";
}
