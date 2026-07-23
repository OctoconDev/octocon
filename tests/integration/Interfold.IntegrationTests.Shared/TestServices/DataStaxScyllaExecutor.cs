using System.Text;
using Cassandra;
using Interfold.DatabaseBootstrap;
using ISession = Cassandra.ISession;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// <see cref="IScyllaExecutor"/> implementation that talks to Scylla/Cassandra in-process
/// via the DataStax driver. Used by the TUnit.Aspire fixtures alongside
/// <see cref="NpgsqlPostgresExecutor"/>.
/// </summary>
/// <remarks>
/// Each call builds a single-use <see cref="Cluster"/> + <see cref="ISession"/> because the
/// auth credentials are call-scoped (we want to validate role lockouts mid-seed). The
/// underlying socket pool is small (1 connection) and explicitly torn down on dispose so
/// nothing leaks across test cases. Tests run on a single host:port pair so the cluster cost
/// is dwarfed by the seed work itself.
/// </remarks>
internal sealed class DataStaxScyllaExecutor(string host, int port) : IScyllaExecutor
{
    public async Task ExecCqlAsync(string user, string password, string cql, CancellationToken ct)
    {
        using var cluster = BuildCluster(user, password);
        using var session = await cluster.ConnectAsync().ConfigureAwait(false);
        // SimpleStatement.SetIdempotence(true) is not strictly true for ALTER ROLE etc. but
        // we never trigger driver-side retries in the seed flow — the orchestrator owns
        // retry semantics by short-circuiting on the idempotency probes.
        var stmt = new SimpleStatement(cql);
        await session.ExecuteAsync(stmt).ConfigureAwait(false);
    }

    public async Task<ScyllaExecResult> TryExecCqlAsync(string user, string password, string cql, CancellationToken ct)
    {
        try
        {
            using var cluster = BuildCluster(user, password);
            using var session = await cluster.ConnectAsync().ConfigureAwait(false);
            var stmt = new SimpleStatement(cql);
            var rs = await session.ExecuteAsync(stmt).ConfigureAwait(false);

            // Flatten the result rows into a single string so the orchestrator's `Output`
            // can be substring-searched (e.g. ScyllaSeeder probes "does the admin name
            // appear in LIST ROLES OF '<admin>'"). For zero-row resultsets we return empty.
            var sb = new StringBuilder();
            foreach (var row in rs)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(row.GetValue<object?>(i)?.ToString() ?? string.Empty);
                }
                sb.AppendLine();
            }
            return new ScyllaExecResult(true, sb.ToString());
        }
        catch (Exception ex)
        {
            // Authentication / unavailable / timeout — let the caller decide whether this
            // is "not yet seeded" (idempotency probe miss) or "transient and retry".
            return new ScyllaExecResult(false, ex.Message);
        }
    }

    private Cluster BuildCluster(string user, string password)
    {
        return Cluster.Builder()
            .AddContactPoint(host)
            .WithPort(port)
            .WithCredentials(user, password)
            // Localhost test runs are reachable in well under 5s even on cold-start; the
            // default 5s socket timeout is fine. Connection pool kept at the driver default
            // (1 / host) — seed work is sequential, more connections wouldn't help.
            .Build();
    }
}
