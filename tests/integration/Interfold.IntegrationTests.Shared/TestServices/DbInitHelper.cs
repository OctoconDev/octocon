using Interfold.DatabaseBootstrap;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Thin test-side convenience shim around <see cref="InProcessSeedWaits"/> plus
/// the shared seeders. Holds test-only constants (default DB name, cassandra default creds)
/// so fixtures can call one line per phase without repeating the wait+seed plumbing.</summary>
public static class DbInitHelper
{
    /// <summary>Hard-coded by msg-db's <c>POSTGRES_USER</c> in the AppHost.</summary>
    public const string PostgresInitUser = PostgresRoles.Init;

    /// <summary>Deliberately non-default name so a regressed hardcoded literal fails a test.</summary>
    public const string DefaultPostgresDb = "test_pg_db";

    /// <summary>Scylla / Cassandra built-in superuser before lockdown.</summary>
    public const string ScyllaDefaultUser = InProcessSeedWaits.ScyllaDefaultUser;

    /// <summary>Default password for <see cref="ScyllaDefaultUser"/>.</summary>
    public const string ScyllaDefaultPassword = InProcessSeedWaits.ScyllaDefaultPassword;

    /// <summary>Delegates to <see cref="InProcessSeedWaits.WaitForPostgresAsync"/>.</summary>
    public static Task WaitForPostgresAsync(
        string initConnectionString,
        PostgresReadinessOptions options,
        CancellationToken ct)
        => InProcessSeedWaits.WaitForPostgresAsync(initConnectionString, options, ct);

    /// <summary>Seeds via <see cref="PostgresSeeder"/>; base string only needs host/port,
    /// the orchestrator rewrites user/password/database per call.</summary>
    public static async Task SeedPostgresAsync(
        string baseConnectionString,
        PostgresSeedOptions options,
        CancellationToken ct)
    {
        var executor = new NpgsqlPostgresExecutor(baseConnectionString);
        await PostgresSeeder.BootstrapAsync(executor, options, NoOpDatabaseInitLogger.Instance, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Delegates to <see cref="InProcessSeedWaits.WaitForScyllaAsync"/>.</summary>
    public static Task WaitForScyllaAsync(string host, int port, CancellationToken ct)
        => InProcessSeedWaits.WaitForScyllaAsync(host, port, ct);

    /// <summary>Seeds via <see cref="ScyllaSeeder"/> with the in-process DataStax executor.</summary>
    public static async Task SeedScyllaAsync(
        string host, int port,
        ScyllaSeedOptions options,
        CancellationToken ct)
    {
        var executor = new DataStaxScyllaExecutor(host, port);
        await ScyllaSeeder.BootstrapAsync(executor, options, NoOpDatabaseInitLogger.Instance, ct)
            .ConfigureAwait(false);
    }
}
