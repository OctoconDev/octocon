using Interfold.Shared.Contracts.Ids;

namespace Interfold.Friendships.Contracts.Ids;

public static class FriendLookupExtensions
{
    // Route-binding self-check for the friend-request send path. Id shapes delegate to
    // ScopedSystemId.RepresentsSameUserAs(SystemId) so raw and same-region-scoped inputs
    // both self-reject; username shapes require a registry hop and return false so the
    // command handler's post-resolution guard takes over.
    public static bool RepresentsSameUserAs(this FriendLookup candidate, ScopedSystemId principal)
        => candidate.Kind switch
        {
            FriendLookupKind.Id => principal.RepresentsSameUserAs(new SystemId(candidate.Value)),
            FriendLookupKind.Username => false,
            _ => false,
        };
}
