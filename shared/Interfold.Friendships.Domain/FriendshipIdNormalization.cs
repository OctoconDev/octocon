using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain;

// Canonicalise a friendship candidate id into the principal's region so downstream
// publishers, routers, and repos all see byte-identical scoped ids.
internal static class FriendshipIdNormalization
{
    // Compose candidate in principal's region when principal parses scoped; else pass through.
    public static ScopedSystemId CanonicalizeForPrincipal(SystemId principalSystemId, ScopedSystemId candidateSystemId)
    {
        if (ScopedSystemId.TryParseScoped(principalSystemId, out var scopedPrincipal))
        {
            return ScopedSystemId.Compose(scopedPrincipal.Region, candidateSystemId);
        }
        return candidateSystemId;
    }
}
