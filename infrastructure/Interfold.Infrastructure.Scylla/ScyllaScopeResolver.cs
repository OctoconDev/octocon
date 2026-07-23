using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

public record ScyllaScope(ISession Session, string Keyspace, string NormalizedSystemId);

public record ScyllaGlobalScope(ISession Session, string Keyspace);

public interface IScyllaScopeResolver
{
    Task<ScyllaScope> ResolveAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        SystemId systemId,
        Func<ScyllaScope, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteAsync<T>(
        SystemId systemId,
        Func<ScyllaScope, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteGlobalAsync(
        Func<ScyllaGlobalScope, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteGlobalAsync<T>(
        Func<ScyllaGlobalScope, Task<T>> operation,
        CancellationToken cancellationToken = default);
}


public sealed class ScyllaScopeResolver : IScyllaScopeResolver
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaScopeResolver> _logger;

    public ScyllaScopeResolver(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaScopeResolver> logger)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ScyllaScope> ResolveAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionProvider.GetSessionAsync(cancellationToken);
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

        return new ScyllaScope(session, keyspace, normalizedSystemId);
    }

    public Task ExecuteAsync(
        SystemId systemId,
        Func<ScyllaScope, Task> operation,
        CancellationToken cancellationToken = default)
    {
        return DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var scope = await ResolveAsync(systemId, cancellationToken);
            await operation(scope);
        }, _options, cancellationToken, _logger);
    }

    public Task<T> ExecuteAsync<T>(
        SystemId systemId,
        Func<ScyllaScope, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        return DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var scope = await ResolveAsync(systemId, cancellationToken);
            return await operation(scope);
        }, _options, cancellationToken, _logger);
    }

    public Task ExecuteGlobalAsync(
        Func<ScyllaGlobalScope, Task> operation,
        CancellationToken cancellationToken = default)
    {
        return DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveGlobalKeyspace();
            var scope = new ScyllaGlobalScope(session, keyspace);
            await operation(scope);
        }, _options, cancellationToken, _logger);
    }

    public Task<T> ExecuteGlobalAsync<T>(
        Func<ScyllaGlobalScope, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        return DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveGlobalKeyspace();
            var scope = new ScyllaGlobalScope(session, keyspace);
            return await operation(scope);
        }, _options, cancellationToken, _logger);
    }
}
