using Interfold.Contracts.Ids;

namespace Interfold.Domain;

/// <summary>
/// Canonicalise a friendship-candidate id into the principal's region so downstream event
/// publishers, WebSocket routers, and repository lookups all see byte-identical scoped
/// ids regardless of whether the input carried its own prefix (from a cross-region
/// candidate) or arrived bare. Wraps a single <see cref="ScopedSystemId.Compose"/> call
/// and returns the composed <see cref="ScopedSystemId"/> so caller event constructors
/// (which declare <see cref="ScopedSystemId"/> parameters via
/// <see cref="Interfold.Contracts.Events.ITargetedClusterEvent"/>) can flow the value
/// through without a second parse.
/// </summary>
internal static class FriendshipIdNormalization
{
    /// <summary>
    /// Canonicalise <paramref name="candidateSystemId"/> into the region of a scoped
    /// principal. Since the principal is always scoped, the Compose call is unconditional.
    /// </summary>
    public static ScopedSystemId CanonicalizeForPrincipal(ScopedSystemId principal, ScopedSystemId candidateSystemId)
        => ScopedSystemId.Compose(principal.Region, candidateSystemId);

    /// <summary>
    /// Overload for the "resolved-but-unscoped candidate" call site (repository lookups
    /// that hand back a raw <see cref="SystemId"/> before it's been composed).
    /// </summary>
    public static ScopedSystemId CanonicalizeForPrincipal(ScopedSystemId principal, SystemId candidateSystemId)
        => ScopedSystemId.Compose(principal.Region, candidateSystemId);

    /// <summary>
    /// "Flip" overload used by the symmetric friendship canonicalisation
    /// (<c>CanonicalizeForPrincipal(sourceId, command.PrincipalId)</c>) — the caller
    /// wants the principal id expressed in the source's region. When the source id is
    /// parseable as scoped we compose in its region; otherwise the candidate flows
    /// through unchanged so an unresolvable source doesn't corrupt the principal shape.
    /// </summary>
    public static ScopedSystemId CanonicalizeForPrincipal(SystemId principalSystemId, ScopedSystemId candidateSystemId)
    {
        if (ScopedSystemId.TryParseScoped(principalSystemId, out var scopedPrincipal))
        {
            return ScopedSystemId.Compose(scopedPrincipal.Region, candidateSystemId);
        }
        return candidateSystemId;
    }
}
