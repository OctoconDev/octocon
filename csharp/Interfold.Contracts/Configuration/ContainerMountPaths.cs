namespace Interfold.Contracts.Configuration;

/// <summary>
/// In-container mount points paired with the <see cref="ComposeVolumes"/> named volumes.
/// Shared by <c>InterfoldAppHost</c> (which creates the mount) and
/// <c>Interfold.Bootstrapper</c> (BackupPhase / RestorePhase, which <c>docker cp</c> in and
/// out at the same path). Changing any value here is a compose-wire change: existing named
/// volumes would silently orphan.
/// </summary>
public static class ContainerMountPaths
{
    /// <summary>Postgres data directory (the volume mount target).</summary>
    public const string PostgresData = "/var/lib/postgresql/data";

    /// <summary>PGDATA subdirectory — the official entrypoint isolates state one level deeper.</summary>
    public const string PostgresPgData = "/var/lib/postgresql/data/pgdata";

    /// <summary>Scylla data directory (single-node and per-region multi-DC nodes).</summary>
    public const string ScyllaData = "/var/lib/scylla";

    /// <summary>Cassandra data directory.</summary>
    public const string CassandraData = "/var/lib/cassandra";

    /// <summary>In-image avatar storage root, pre-created with app:app ownership so the
    /// AppHost-managed named volume inherits the correct owner on first mount.</summary>
    public const string InterfoldAvatars = "/app/data/avatars";
}
