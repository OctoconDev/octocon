using System.Net.Sockets;
using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.DatabaseBootstrap;
using Npgsql;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>Cold-start waits + thin driver-side entry points that hand seed work to the
/// shared <see cref="PostgresSeeder"/>/<see cref="ScyllaSeeder"/>. Wait loops stay
/// transport-specific: driver connects (here) vs. container-exec probes (bootstrapper) have
/// different "ready" signals.</summary>
public static class DbInitHelper
{
    /// <summary>Hard-coded by msg-db's <c>POSTGRES_USER</c> in the AppHost.</summary>
    public const string PostgresInitUser = PostgresRoles.Init;

    /// <summary>Deliberately non-default name so a regressed hardcoded literal fails a test.</summary>
    public const string DefaultPostgresDb = "test_pg_db";

    /// <summary>Scylla / Cassandra built-in superuser before lockdown.</summary>
    public const string ScyllaDefaultUser = "cassandra";

    /// <summary>Default password for <see cref="ScyllaDefaultUser"/>.</summary>
    public const string ScyllaDefaultPassword = "cassandra";

    /// <summary>Waits for db_init to answer <c>SELECT 1</c>; catches every transient
    /// driver/socket exception until the deadline.</summary>
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
            catch (NpgsqlException) { consecutiveSuccesses = 0; }
            catch (SocketException)  { consecutiveSuccesses = 0; }
            catch (TimeoutException) { consecutiveSuccesses = 0; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"msg-db did not become ready within {options.Timeout.TotalMinutes} minutes ({attempt} probes).");
    }

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

    /// <summary>Waits for the default cassandra account to answer a system.local SELECT.</summary>
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
            // Broad catch: DataStax wraps gossip/auth/socket faults in a handful of types;
            // any startup-phase throw is transient until the deadline.
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"scylla/cassandra at {host}:{port} did not become ready within 5 minutes ({attempt} probes).");
    }

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
