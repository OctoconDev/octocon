namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Well-known Postgres role names shared by AppHost, bootstrapper, and integration
/// tests. Wire values — all three call sites must agree or first-boot bootstrap fails.</summary>
public static class PostgresRoles
{
    /// <summary>Disposable bootstrap superuser initdb creates on msg-db's first boot.
    /// Consumed once by <c>DatabaseInitPhase</c> / <c>DbInitHelper</c> to mint the real
    /// application roles, then scrambled to a fresh random password.</summary>
    public const string Init = "db_init";
}
