using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory;

// Shared key/normalization helpers for the InMemory repositories. Normalisation strips
// a legacy <c>"region:"</c> prefix (mirrors <c>ScyllaKeyspaceResolver.NormalizeSystemId</c>);
// system partition keys are <c>"{region}:{systemId}"</c>.
internal static class InMemoryStorageKeys
{
    // Returns a wrapper so callers can key dictionaries directly instead of on string.
    public static SystemId Normalize(SystemId systemId)
        => new(ScopedSystemId.StripRegionPrefix(systemId.Value));

    // Compose is idempotent — an already-scoped SystemId won't become "nam:nam:abcdefg".
    public static ScopedSystemId ForSystem(IRegionContext regionContext, SystemId systemId)
    {
        var region = regionContext.ResolveUserRegion(systemId);
        return ScopedSystemId.Compose(region, systemId);
    }

    // Public read → null; self → TrustedFriend; else defer to the (optional) friendship
    // repo. Null friendships is the "InMemory instance without a friend graph" branch.
    public static async Task<FriendshipLevel?> ResolveFriendshipLevelAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        IFriendshipRepository? friendships,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return null;
        }

        if (ScopedSystemId.StripRegionPrefix(systemId) ==
            ScopedSystemId.StripRegionPrefix(viewerSystemId.Value))
        {
            return FriendshipLevel.TrustedFriend;
        }

        if (friendships is null)
        {
            return null;
        }

        return await friendships.GetFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
    }
}
