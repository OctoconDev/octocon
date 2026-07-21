using Cassandra;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Thin <see cref="IAsyncDisposable"/> wrapper around a DataStax <see cref="Cluster"/> +
/// <see cref="ISession"/> pair. Centralises builder settings (credentials, DC-aware routing,
/// query timeout) that were previously duplicated across ~6 test sites and ensures the
/// cluster is shut down on disposal.
/// </summary>
internal sealed class ScyllaTestSession : IAsyncDisposable
{
    public Cluster Cluster { get; }
    public ISession Session { get; }

    private ScyllaTestSession(Cluster cluster, ISession session)
    {
        Cluster = cluster;
        Session = session;
    }

    /// <summary>
    /// Opens a session authenticated as the application-level user
    /// (<see cref="TestDbCredentials.ScyllaAppUser"/>).
    /// </summary>
    public static Task<ScyllaTestSession> OpenAsAppAsync(
        string host, int port,
        string dc = "nam", int queryTimeoutMs = 30_000,
        CancellationToken ct = default)
        => OpenAsync(host, port, TestDbCredentials.ScyllaAppUser, TestDbCredentials.ScyllaAppPassword, dc, queryTimeoutMs, ct);

    /// <summary>
    /// Opens a session authenticated as the admin user
    /// (<see cref="TestDbCredentials.ScyllaAdminUser"/>).
    /// </summary>
    public static Task<ScyllaTestSession> OpenAsAdminAsync(
        string host, int port,
        string dc = "nam", int queryTimeoutMs = 30_000,
        CancellationToken ct = default)
        => OpenAsync(host, port, TestDbCredentials.ScyllaAdminUser, TestDbCredentials.ScyllaAdminPassword, dc, queryTimeoutMs, ct);

    private static async Task<ScyllaTestSession> OpenAsync(
        string host, int port,
        string user, string password,
        string dc, int queryTimeoutMs,
        CancellationToken ct)
    {
        var cluster = Cluster.Builder()
            .AddContactPoint(host)
            .WithPort(port)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(dc))
            .WithCredentials(user, password)
            .WithQueryTimeout(queryTimeoutMs)
            .Build();

        var session = await cluster.ConnectAsync().ConfigureAwait(false);
        return new ScyllaTestSession(cluster, session);
    }

    public async ValueTask DisposeAsync()
    {
        Session.Dispose();
        await Cluster.ShutdownAsync().ConfigureAwait(false);
    }
}
