namespace Interfold.Contracts.Configuration;

/// <summary>
/// Well-known Postgres role names baked into the AppHost's compose graph. These are
/// wire values shared between the AppHost (which sets them via <c>POSTGRES_USER</c>),
/// the bootstrapper's <c>DatabaseInitPhase</c> (which authenticates as this role to
/// mint the real app + admin roles before scrambling this role's password
/// in-cluster), and the integration-test fixtures (which run the same seed step
/// against the driver in-process).
/// </summary>
/// <remarks>
/// The AppHost writes <see cref="Init"/> into <c>msg-db</c>'s <c>POSTGRES_USER</c>
/// env var; the bootstrapper and the integration-test <c>DbInitHelper</c> then
/// authenticate as that role for the one-shot seed. All three call sites must agree
/// or first-boot bootstrap fails to authenticate.
/// </remarks>
public static class PostgresRoles
{
    /// <summary>The disposable bootstrap superuser <c>initdb</c> creates on <c>msg-db</c>'s
    /// first boot (OID 10, cluster owner). Consumed exactly once by
    /// <c>DatabaseInitPhase</c> / <c>DbInitHelper</c> to mint the application roles,
    /// then scrambled to a fresh random password so the compose <c>.env</c> value is
    /// stale by the time the API starts.</summary>
    public const string Init = "db_init";
}
