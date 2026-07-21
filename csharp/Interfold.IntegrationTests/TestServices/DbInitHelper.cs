using System.Net.Sockets;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.DatabaseBootstrap;
using Npgsql;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Cold-start wait helpers plus thin entry points that hand seed work off to
/// <see cref="Interfold.DatabaseBootstrap.PostgresSeeder"/> /
/// <see cref="Interfold.DatabaseBootstrap.ScyllaSeeder"/>. The SQL/CQL bodies + idempotency
/// logic live in the shared <c>Interfold.DatabaseBootstrap</c> project so this file and
/// <c>DatabaseInitPhase.cs</c> stay in lockstep without duplicated strings.
/// </summary>
/// <remarks>
/// <para>
/// The wait loops stay transport-specific because the driver-based path (used here) and the
/// compose-exec path (used in the bootstrapper) have fundamentally different "ready"
/// signals: the bootstrapper scans container logs for the normal-mode banner because a
/// SELECT 1 against the unix socket succeeds even during initdb; the driver here opens a
/// TCP socket so it never sees init-mode connections in the first place.
/// </para>
/// <para>
/// The fixtures supply the base connection string + a <see cref="PostgresSeedOptions"/>
/// with <c>ScrambleInitUserPassword: false</c> — tests want db_init's password stable
/// across idempotent reruns of <c>WaitForResourcesAsync</c> in the same fixture session.
/// </para>
/// </remarks>
internal static class DbInitHelper
{
    /// <summary>Hard-coded by msg-db's <c>POSTGRES_USER</c> in the AppHost.</summary>
    public const string PostgresInitUser = PostgresRoles.Init;

    /// <summary>Application database name used across the in-process integration test suite.
    /// <para>
    /// Deliberately set to a value that does NOT match the production default
    /// (<c>interfold</c>) — the test fixture pushes this through
    /// <c>Parameters:postgres-db</c> on the AppHost and uses it in both the seeder's
    /// <c>CREATE DATABASE</c> call and the host-side app connection string. If any layer in
    /// that chain (AppHost connection string, <see cref="PostgresSeedOptions.DefaultDatabase"/>,
    /// the API's <c>OCTOCON_POSTGRES_CONNECTION</c> binding) accidentally regresses to a
    /// hardcoded literal, the test that opens a connection on this name will fail —
    /// proving the configurability wiring is load-bearing rather than vacuously satisfied
    /// by everyone agreeing on "interfold".
    /// </para>
    /// </summary>
    public const string DefaultPostgresDb = "test_pg_db";

    /// <summary>Scylla / Cassandra built-in superuser before lockdown.</summary>
    public const string ScyllaDefaultUser = "cassandra";

    /// <summary>Default password for <see cref="ScyllaDefaultUser"/>.</summary>
    public const string ScyllaDefaultPassword = "cassandra";

    /// <summary>
    /// Waits up to 6 minutes for <c>db_init</c> to be able to <c>SELECT 1</c> against the
    /// <c>postgres</c> database. Worst-case cold start on DinD-on-WSL2 is around 4 minutes:
    /// timescaledb-tune does an extra restart cycle that can checkpoint to slow backing
    /// storage. Catches every transient driver/socket exception until the deadline.
    /// </summary>
    public static async Task WaitForPostgresAsync(
        string initConnectionString,
        Interfold.DatabaseBootstrap.PostgresReadinessOptions options,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(options.Timeout);
        var attempt = 0;
        var consecutiveSuccesses = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(initConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (result is int one && one == 1)
                {
                    consecutiveSuccesses++;
                    if (consecutiveSuccesses >= options.RequiredConsecutiveSuccesses) return;
                }
                else if (consecutiveSuccesses > 0)
                {
                    consecutiveSuccesses = 0;
                }
            }
            catch (NpgsqlException) { consecutiveSuccesses = 0; /* server not up yet */ }
            catch (SocketException)  { consecutiveSuccesses = 0; /* port not bound yet */ }
            catch (TimeoutException) { consecutiveSuccesses = 0; /* server tearing down */ }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"msg-db did not become ready within {options.Timeout.TotalMinutes} minutes ({attempt} probes).");
    }

    /// <summary>
    /// Seeds Postgres via the shared <see cref="PostgresSeeder"/>, plugging in the
    /// in-process Npgsql transport adapter. <paramref name="baseConnectionString"/> only
    /// needs the host/port — the orchestrator rewrites <c>Username</c> / <c>Password</c> /
    /// <c>Database</c> on each call.
    /// </summary>
    public static async Task SeedPostgresAsync(
        string baseConnectionString,
        PostgresSeedOptions options,
        CancellationToken ct)
    {
        var executor = new NpgsqlPostgresExecutor(baseConnectionString);
        await PostgresSeeder.BootstrapAsync(executor, options, NoOpDatabaseInitLogger.Instance, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits up to 5 minutes for the Scylla/Cassandra default <c>cassandra</c> account to
    /// accept a CQL connection and answer <c>SELECT * FROM system.local</c>. Matches the
    /// budget used by <c>DatabaseInitPhase.WaitForScyllaAsync</c>.
    /// </summary>
    public static async Task WaitForScyllaAsync(string host, int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                using var cluster = Cluster.Builder()
                    .AddContactPoint(host)
                    .WithPort(port)
                    .WithCredentials(ScyllaDefaultUser, ScyllaDefaultPassword)
                    .WithSocketOptions(new SocketOptions().SetConnectTimeoutMillis(10000))
                    .Build();
                using var session = await cluster.ConnectAsync().ConfigureAwait(false);
                var rs = await session.ExecuteAsync(new SimpleStatement("SELECT cluster_name FROM system.local"))
                    .ConfigureAwait(false);
                if (rs.GetRows().Any()) return;
            }
            // We deliberately catch broadly: the DataStax driver wraps a handful of distinct
            // failure modes (gossip-not-ready, auth-not-loaded, socket-refused, partial cluster
            // membership) in `NoHostAvailableException` and a couple of more specific types.
            // Anything during startup is treated as transient; once we've passed gossip-bootstrap
            // the next probe will succeed.
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                /* transient */
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"scylla/cassandra at {host}:{port} did not become ready within 5 minutes ({attempt} probes).");
    }

    /// <summary>
    /// Seeds Scylla via the shared <see cref="ScyllaSeeder"/>, plugging in the in-process
    /// DataStax transport adapter.
    /// </summary>
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
