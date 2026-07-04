using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaNotificationTokenRepository : INotificationTokenRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaNotificationTokenRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<bool> AddAsync(string systemId, string token, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                "INSERT INTO global.notification_tokens (user_id, push_token, inserted_at, updated_at) VALUES (?, ?, ?, ?)",
                normalizedSystemId, token, now.UtcDateTime, now.UtcDateTime));
            batch.Add(new SimpleStatement(
                "INSERT INTO global.notification_tokens_by_push_token (push_token, user_id) VALUES (?, ?)",
                token, normalizedSystemId));

            await session.ExecuteAsync(batch);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            var findByToken = await session.PrepareAsync(@"
                SELECT user_id FROM global.notification_tokens_by_push_token
                WHERE push_token = ?");

            var rows = await session.ExecuteAsync(findByToken.Bind(token));
            
            var deleteBatch = new BatchStatement();
            foreach (var row in rows)
            {
                var userId = row.GetValue<string>("user_id");
                deleteBatch.Add(new SimpleStatement(
                    "DELETE FROM global.notification_tokens WHERE user_id = ? AND push_token = ?",
                    userId, token));
                deleteBatch.Add(new SimpleStatement(
                    "DELETE FROM global.notification_tokens_by_push_token WHERE push_token = ? AND user_id = ?",
                    token, userId));
            }

            if (!deleteBatch.IsEmpty)
            {
                await session.ExecuteAsync(deleteBatch);                
            }

            return true;
        }, _options, cancellationToken);
    }

    // Reads global.friendships for the friend-id list, then multi-partition selects
    // global.notification_tokens per friend with concurrency capped by
    // PersistenceConfiguration.HydrationMaxConcurrency. Both tables partition on
    // user_id, so each per-friend fetch is single-partition. Friends with zero tokens
    // are omitted from the result so callers can iterate without a guard.
    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var friendRows = await session.ExecuteAsync(new SimpleStatement(
                "SELECT friend_id FROM global.friendships WHERE user_id = ?",
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
                        "SELECT push_token FROM global.notification_tokens WHERE user_id = ?",
                        friendId));
                    var tokens = tokenRows.Select(r => r.GetValue<string>("push_token"))
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    return new FriendNotificationTokens(friendId, tokens);
                },
                cancellationToken);

            return (IReadOnlyList<FriendNotificationTokens>)perFriendGroups
                .Where(g => g.Tokens.Count > 0)
                .ToArray();
        }, _options, cancellationToken);
    }
}
