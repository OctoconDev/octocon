using System.Collections.Concurrent;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryNotificationTokenRepository : INotificationTokenRepository
{
    private readonly ConcurrentDictionary<SystemId, ConcurrentDictionary<PushToken, byte>> _tokensBySystem = new();
    private readonly ConcurrentDictionary<PushToken, SystemId> _tokenOwners = new();
    private readonly IFriendshipRepository _friendshipRepository;

    public InMemoryNotificationTokenRepository(IFriendshipRepository friendshipRepository)
    {
        _friendshipRepository = friendshipRepository;
    }

    public Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        PushToken normalizedToken = new(token.Value.Trim());
        _tokenOwners[normalizedToken] = normalizedSystemId;

        var systemTokens = _tokensBySystem.GetOrAdd(normalizedSystemId, _ => new ConcurrentDictionary<PushToken, byte>());
        systemTokens[normalizedToken] = 1;

        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PushToken normalizedToken = new(token.Value.Trim());
        if (_tokenOwners.TryRemove(normalizedToken, out var ownerSystemId) &&
            _tokensBySystem.TryGetValue(ownerSystemId, out var systemTokens))
        {
            systemTokens.TryRemove(normalizedToken, out _);
        }

        return Task.FromResult(true);
    }

    // Friends with zero registered tokens are omitted so callers can iterate without a
    // Count > 0 guard. Mirrors the Scylla port so backends stay swappable via OCTOCON_PERSISTENCE.
    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        if (string.IsNullOrWhiteSpace(normalizedSystemId))
            return Array.Empty<FriendNotificationTokens>();

        var friendships = await _friendshipRepository.ListFriendshipsAsync(normalizedSystemId, cancellationToken);
        if (friendships.Count == 0)
            return Array.Empty<FriendNotificationTokens>();

        var groups = new List<FriendNotificationTokens>(friendships.Count);
        var seenFriends = new HashSet<SystemId>();
        foreach (var friendship in friendships)
        {
            if (friendship.Friend is null)
                continue;

            var friendId = InMemoryStorageKeys.Normalize(friendship.Friend.Id);
            if (string.IsNullOrWhiteSpace(friendId) || !seenFriends.Add(friendId))
                continue;

            if (!_tokensBySystem.TryGetValue(friendId, out var tokens))
                continue;

            var distinctTokens = tokens.Keys
                .Where(t => !string.IsNullOrWhiteSpace(t.Value))
                .Distinct()
                .ToArray();
            if (distinctTokens.Length == 0)
                continue;

            groups.Add(new FriendNotificationTokens(friendId, distinctTokens));
        }

        return groups;
    }
}
