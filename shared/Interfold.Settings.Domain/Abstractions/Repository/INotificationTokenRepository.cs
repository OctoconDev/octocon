using Interfold.Shared.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

public interface INotificationTokenRepository
{
    Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default);

    /// <summary>Push tokens for every friend of <paramref name="systemId"/>, grouped by
    /// friend (each friend sees a different payload due to per-recipient visibility
    /// filtering). Implementations must throttle fan-out via HydrationMaxConcurrency and
    /// omit friends with zero devices rather than returning an empty token list.</summary>
    Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(SystemId systemId, CancellationToken cancellationToken = default);
}

/// <summary>One friend's registered push tokens (phone + tablet + browser collapse into one list).</summary>
public sealed record FriendNotificationTokens(SystemId FriendSystemId, IReadOnlyList<PushToken> Tokens);
