using System.Net.Sockets;
using Cassandra;
using Npgsql;

namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Cold-start driver-side wait loops used by the in-process seed callers
/// (<c>Interfold.AppHost</c>'s dev seed hosted service and the TUnit.Aspire fixtures).
/// The bootstrapper's own compose-exec seed uses different probes because "healthy" for a
/// container-exec differs from "healthy" for an out-of-container Npgsql/DataStax connection.
/// </summary>
public static class InProcessSeedWaits
{
    /// <summary>Built-in Scylla / Cassandra superuser present before <see cref="ScyllaSeeder"/> locks it.</summary>
    public const string ScyllaDefaultUser = "cassandra";

    /// <summary>Built-in password for <see cref="ScyllaDefaultUser"/>.</summary>
    public const string ScyllaDefaultPassword = "cassandra";

    /// <summary>
    /// Waits until the caller's init role can complete <c>SELECT 1</c> against Postgres.
    /// Requires <paramref name="options"/>.<c>RequiredConsecutiveSuccesses</c> back-to-back
    /// successes so we don't return during the socket-open-but-still-crash-recovering window
    /// that fresh TimescaleDB starts hit.
    /// </summary>
    public static async Task WaitForPostgresAsync(
        string initConnectionString,
        PostgresReadinessOptions options,
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
            // Broad catch by driver exception type: any transient socket / auth / timeout
            // failure resets the streak; the deadline is the real timeout.
            catch (NpgsqlException) { consecutiveSuccesses = 0; }
            catch (SocketException) { consecutiveSuccesses = 0; }
            catch (TimeoutException) { consecutiveSuccesses = 0; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Postgres did not become ready within {options.Timeout.TotalMinutes} minutes ({attempt} probes).");
    }

    /// <summary>
    /// Waits until the built-in <c>cassandra</c> account answers a <c>system.local</c> query.
    /// Fixed 5-minute budget — gossip-bootstrap is the slow phase and 5min is already generous
    /// on cold-start DinD hosts.
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
            // DataStax wraps gossip / auth / socket faults in a handful of exception types;
            // any startup-phase throw is transient until the deadline.
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"scylla/cassandra at {host}:{port} did not become ready within 5 minutes ({attempt} probes).");
    }
}
