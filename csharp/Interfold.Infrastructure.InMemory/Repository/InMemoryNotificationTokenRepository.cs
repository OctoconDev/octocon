using System.Collections.Concurrent;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryNotificationTokenRepository : INotificationTokenRepository
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tokensBySystem = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenOwners = new(StringComparer.Ordinal);
    private readonly IFriendshipRepository _friendshipRepository;

    public InMemoryNotificationTokenRepository(IFriendshipRepository friendshipRepository)
    {
        _friendshipRepository = friendshipRepository;
    }

    public Task<bool> AddAsync(string systemId, string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId?.Trim() ?? string.Empty);
        var normalizedToken = token.Trim();
        _tokenOwners[normalizedToken] = normalizedSystemId;

        var systemTokens = _tokensBySystem.GetOrAdd(normalizedSystemId, _ => new(StringComparer.Ordinal));
        systemTokens[normalizedToken] = 1;

        return Task.FromResult(true);
    }

    private static string NormalizeSystemId(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return systemId;

        var separator = systemId.IndexOf(':');
        if (separator <= 0 || separator >= systemId.Length - 1)
            return systemId;

        return systemId[(separator + 1)..];
    }

    public Task<bool> RemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedToken = token.Trim();
        if (_tokenOwners.TryRemove(normalizedToken, out var ownerSystemId) &&
            _tokensBySystem.TryGetValue(ownerSystemId, out var systemTokens))
        {
            systemTokens.TryRemove(normalizedToken, out _);
        }

        return Task.FromResult(true);
    }

    // Delegates friend enumeration to IFriendshipRepository, then attaches each friend's
    // token set. Friends with zero registered tokens are omitted so callers can iterate
    // groups without a Count > 0 guard. Mirrors the Scylla impl's shape so backends
    // stay swappable via OCTOCON_PERSISTENCE.
    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSystemId = NormalizeSystemId(systemId?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedSystemId))
            return Array.Empty<FriendNotificationTokens>();

        var friendships = await _friendshipRepository.ListFriendshipsAsync(normalizedSystemId, cancellationToken);
        if (friendships.Count == 0)
            return Array.Empty<FriendNotificationTokens>();

        var groups = new List<FriendNotificationTokens>(friendships.Count);
        var seenFriends = new HashSet<string>(StringComparer.Ordinal);
        foreach (var friendship in friendships)
        {
            var friendId = NormalizeSystemId(friendship.Friend?.Id ?? string.Empty);
            if (string.IsNullOrWhiteSpace(friendId) || !seenFriends.Add(friendId))
                continue;

            if (!_tokensBySystem.TryGetValue(friendId, out var tokens))
                continue;

            var distinctTokens = tokens.Keys
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctTokens.Length == 0)
                continue;

            groups.Add(new FriendNotificationTokens(friendId, distinctTokens));
        }

        return groups;
    }
}
