using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

public interface INotificationTokenRepository
{
    Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns push tokens for every friend of <paramref name="systemId"/>, grouped by
    /// the friend's system id. Grouping is required because per-recipient visibility
    /// filtering (see <c>IAlterRepository.ListGuardedAsync</c>) means each friend can
    /// see a different subset of fronting alters and therefore receives a different
    /// notification payload.
    /// <para>
    /// Implementations should keep the fan-out bounded: the Scylla implementation reads
    /// <c>global.friendships</c> for the friend-id list then multi-partition selects
    /// <c>global.notification_tokens</c>, throttled by
    /// <c>PersistenceConfiguration.HydrationMaxConcurrency</c>. An empty list is the
    /// expected shape for systems with no friends; individual friends with no registered
    /// devices should be omitted rather than returned with an empty <see cref="FriendNotificationTokens.Tokens"/>.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(SystemId systemId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One friend's registered push tokens. Multiple entries per friend (phone + tablet +
/// browser) collapse into a single <see cref="Tokens"/> list.
/// </summary>
public sealed record FriendNotificationTokens(SystemId FriendSystemId, IReadOnlyList<PushToken> Tokens);
