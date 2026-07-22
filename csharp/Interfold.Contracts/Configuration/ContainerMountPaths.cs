namespace Interfold.Contracts.Configuration;

/// <summary>In-container mount points paired with <see cref="ComposeVolumes"/>. Changing
/// any value is a compose-wire change: existing named volumes would silently orphan.</summary>
public static class ContainerMountPaths
{
    public const string PostgresData = "/var/lib/postgresql/data";

    /// <summary>PGDATA subdirectory — the official entrypoint isolates state one level deeper.</summary>
    public const string PostgresPgData = "/var/lib/postgresql/data/pgdata";

    public const string ScyllaData = "/var/lib/scylla";

    public const string CassandraData = "/var/lib/cassandra";

    /// <summary>Pre-created in-image with app:app ownership so the volume inherits the owner on first mount.</summary>
    public const string InterfoldAvatars = "/app/data/avatars";
}
