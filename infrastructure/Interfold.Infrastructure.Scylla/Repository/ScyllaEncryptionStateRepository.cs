using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaEncryptionStateRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Bind the raw string — wrapper structs have no driver serializer and user-defined
            // implicit conversions don't fire through the params-object[] boxing path.
            var query = new SimpleStatement(
                $"SELECT encryption_initialized, encryption_key_checksum, salt FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new EncryptionState(
                    row.GetValue<bool?>("encryption_initialized") ?? false,
                    KeyChecksum.FromNullable(row.GetValue<string?>("encryption_key_checksum")),
                    EncryptionSalt.FromNullable(row.GetValue<string?>("salt")));
        }, cancellationToken);
    }

    public async Task<bool> UpsertAsync(SystemId systemId, bool initialized, KeyChecksum? keyChecksum, EncryptionSalt? salt, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            SimpleStatement statement;
            if (salt is not { } newSalt)
            {
                statement = new SimpleStatement(
                    $"UPDATE {keyspace}.users SET encryption_initialized = ?, encryption_key_checksum = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    initialized,
                    keyChecksum?.Value,
                    normalizedSystemId
                );
            }
            else
            {
                statement = new SimpleStatement(
                    $"UPDATE {keyspace}.users SET encryption_initialized = ?, encryption_key_checksum = ?, salt = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    initialized,
                    keyChecksum?.Value,
                    newSalt.Value,
                    normalizedSystemId
                );
            }

            await session.ExecuteAsync(statement);
            return true;
        }, cancellationToken);
    }
}
