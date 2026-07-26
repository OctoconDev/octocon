using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;
using Interfold.Infrastructure.Persistence;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Domain.Abstractions.Repository;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaNotificationTokenRepository : INotificationTokenRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly IScyllaScopeResolver _scopeResolver;

    public ScyllaNotificationTokenRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IScyllaScopeResolver scopeResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _scopeResolver = scopeResolver;
    }

    public async Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<bool>(async scope =>
        {
            var session = scope.Session;
            var now = DateTimeOffset.UtcNow;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"INSERT INTO {ScyllaGlobalKeyspace.Name}.notification_tokens (user_id, push_token, inserted_at, updated_at) VALUES (?, ?, ?, ?)",
                normalizedSystemId, token.Value, now.UtcDateTime, now.UtcDateTime));
            batch.Add(new SimpleStatement(
                $"INSERT INTO {ScyllaGlobalKeyspace.Name}.notification_tokens_by_push_token (push_token, user_id) VALUES (?, ?)",
                token.Value, normalizedSystemId));

            await session.ExecuteAsync(batch);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<bool>(async scope =>
        {
            var session = scope.Session;

            var findByToken = await session.PrepareAsync($@"
                SELECT user_id FROM {ScyllaGlobalKeyspace.Name}.notification_tokens_by_push_token
                WHERE push_token = ?");

            var rows = await session.ExecuteAsync(findByToken.Bind(token.Value));
            
            var deleteBatch = new BatchStatement();
            foreach (var row in rows)
            {
                var userId = row.GetValue<string>("user_id");
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {ScyllaGlobalKeyspace.Name}.notification_tokens WHERE user_id = ? AND push_token = ?",
                    userId, token.Value));
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {ScyllaGlobalKeyspace.Name}.notification_tokens_by_push_token WHERE push_token = ? AND user_id = ?",
                    token.Value, userId));
            }

            if (!deleteBatch.IsEmpty)
            {
                await session.ExecuteAsync(deleteBatch);                
            }

            return true;
        }, cancellationToken);
    }

    // Reads global.friendships for the friend-id list, then multi-partition selects
    // global.notification_tokens per friend with concurrency capped by
    // PersistenceConfiguration.HydrationMaxConcurrency. Both tables partition on
    // user_id, so each per-friend fetch is single-partition. Friends with zero tokens
    // are omitted from the result so callers can iterate without a guard.
    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<IReadOnlyList<FriendNotificationTokens>>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var friendRows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT friend_id FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ?",
                normalizedSystemId));

            var friendIds = friendRows.Select(r => r.GetValue<string>("friend_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (friendIds.Length == 0)
                return (IReadOnlyList<FriendNotificationTokens>)Array.Empty<FriendNotificationTokens>();

            var perFriendGroups = await ConcurrentProjection.SelectWithConcurrencyAsync(
                friendIds,
                _options.HydrationMaxConcurrency,
                async friendId =>
                {
                    var tokenRows = await session.ExecuteAsync(new SimpleStatement(
                        $"SELECT push_token FROM {ScyllaGlobalKeyspace.Name}.notification_tokens WHERE user_id = ?",
                        friendId));
                    var tokens = tokenRows.Select(r => r.GetValue<string>("push_token"))
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.Ordinal)
                        .Select(t => new PushToken(t))
                        .ToArray();
                    return new FriendNotificationTokens(new(friendId), tokens);
                },
                cancellationToken);

            return (IReadOnlyList<FriendNotificationTokens>)perFriendGroups
                .Where(g => g.Tokens.Count > 0)
                .ToArray();
        }, cancellationToken);
    }
}
