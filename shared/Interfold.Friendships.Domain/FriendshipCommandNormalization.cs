using Interfold.Shared.Contracts.Ids;

namespace Interfold.Friendships.Domain;

internal static class FriendshipCommandNormalization
{
    public static ScopedSystemId ComposePeerId(ScopedSystemId principalId, SystemId peerSystemId)
        => ScopedSystemId.Compose(principalId.Region, peerSystemId);

    public static ScopedSystemId CanonicalPrincipalForPeer(SystemId peerSystemId, ScopedSystemId principalId)
        => FriendshipIdNormalization.CanonicalizeForPrincipal(peerSystemId, principalId);
}
