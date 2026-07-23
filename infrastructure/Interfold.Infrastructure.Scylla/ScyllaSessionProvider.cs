using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

public interface IScyllaSessionProvider
{
    Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default);
}

public sealed class ScyllaSessionProvider : IScyllaSessionProvider
{
    private readonly PersistenceConfiguration _options;
    private readonly IScyllaConfigResolver _configResolver;
    private readonly Lazy<Task<ISession>> _session;

    public ScyllaSessionProvider(
        IOptions<PersistenceConfiguration> options,
        IScyllaConfigResolver configResolver)
    {
        _options = options.Value;
        _configResolver = configResolver;
        _session = new Lazy<Task<ISession>>(ConnectAsync);
    }

    public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) => _session.Value;

    private async Task<ISession> ConnectAsync()
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var contactPoints = await _configResolver.GetContactPointsAsync();
            var datacenter = await _configResolver.GetDatacenterAsync();
            var username = await _configResolver.GetUsernameAsync();
            var password = await _configResolver.GetPasswordAsync();
            var keyspace = _configResolver.GetKeyspace();
            var port = await _configResolver.GetPortAsync();

            var builder = Cluster.Builder()
                .AddContactPoints(contactPoints)
                .WithPort(port)
                .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(datacenter))
                .WithReconnectionPolicy(new ExponentialReconnectionPolicy(1000, 30000))
                .WithQueryTimeout(10000)
                .WithSocketOptions(new SocketOptions()
                    .SetConnectTimeoutMillis(10000)
                    .SetKeepAlive(true));

            if (!string.IsNullOrWhiteSpace(username))
            {
                builder = builder.WithCredentials(username, password);
            }

            var cluster = builder.Build();
            return await cluster.ConnectAsync(keyspace);
        }, _options);
    }
}
